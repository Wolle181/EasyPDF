# The web server in detail

What `src/EasyPDF.Server` does, from start-up to the deletion of a file. The decisions behind it are in
[DESIGN.md "Web version"](DESIGN.md#web-version), building and running the image in
[BUILDING.md "Web version (container)"](BUILDING.md#web-version-container).

## Role
The server is an ASP.NET Core program with two jobs:

1. It **serves the browser program** (the shared Avalonia user interface compiled to WebAssembly, `src/EasyPDF.Browser`).
2. It does **everything that needs pdfium** over a REST API under `/api`: pdfium does not run in the browser, so the server
   renders pages, reads text and attachments, recognizes text (OCR) and writes new PDFs.

**Division of work:** the user interface and the editing model run entirely in the browser: the page list, undo/redo,
placed stamps, texts and black out/white out boxes. The browser knows a file only by its id on the server
(`RemotePdfSource`). Only when the user saves does it send the finished page list with its overlays, and the server
writes a new PDF from it with the same `PdfSaver` as the desktop program.

## Start-up (`Program.cs`)
- **Configuration:** section `Web` (`ServerOptions`): `appsettings.json`, in the container `appsettings.Production.json`,
  or environment variables `Web__…`. It holds the sign-in mode, storage folders and quotas, rate limits and the trusted
  reverse proxies.
- **Kestrel:** no `Server` header; a request body may be the upload limit plus 1 MB (uploads, and page lists carrying the
  decoded pixels of placed pictures); request headers must arrive within 30 s.
- **Services**, one each for the whole server: `SessionStore` (anonymous sessions), `FileStore` (files and their owners),
  `OpenDocuments` (files opened with pdfium), `OcrService`, and `CleanupService` as a hosted background service.
  `TimeProvider` is registered with `TryAdd`, so the tests put in a simulated clock.
- **Data protection** encrypts the cookies (sign-in, OIDC correlation). Its keys are kept in `Storage:KeysFolder` when that
  folder is writable, so sign-ins survive a restart.
- **Health check:** started with `--healthcheck`, the program only asks the running server for `GET /health` and exits
  with 0 or 1. The container uses this because its image has neither a shell nor curl.

## Request pipeline
Every request passes these steps in order:

1. **Forwarded headers** (`Security/ForwardedHeadersSetup.cs`): client address and scheme are taken from
   `X-Forwarded-For`/`X-Forwarded-Proto`, but only from trusted proxies: loopback, in a container its gateway (read from
   `/proc/net/route`, `ReverseProxy:TrustContainerGateway`) and `ReverseProxy:TrustedProxies` (addresses or CIDR networks).
   Without this all users would count as one address for the rate limits, and HTTPS would not be recognized (secure
   cookies, redirects). The start log lists the trusted proxies.
2. **Security headers** (`Security/SecurityHeadersMiddleware.cs`): CSP (only the own host, `'wasm-unsafe-eval'` for
   WebAssembly, no inline scripts, `frame-ancestors 'self'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy`,
   `Permissions-Policy`. They come from the app because some reverse proxies can add custom headers only to the request, not to the response.
   They are set in `OnStarting` (so they survive the exception handler) and never overwrite headers set elsewhere. PDF
   responses get no CSP, otherwise the browser's PDF viewer (printing) would not work.
3. **Errors:** in Development the developer exception page; otherwise a generic JSON error without details, and HSTS for
   one year (`UseHsts`; not for localhost or plain HTTP).
4. **`GET /health`** answers `ok` (before rate limiting and sign-in).
5. **Rate limiting** per client address (fixed one-minute windows, no queue, answer 429 with a JSON error):
   all `/api` requests (`RateLimits:RequestsPerMinute`, default 1200) and additionally the expensive ones: upload, `build`,
   `extract`, `blank`, `split`, `ocr` (`RateLimits:UploadsPerMinute`, default 30). Static files are not limited.
6. **Sign-in**, only in mode `Oidc`: cookie `__Host-signin` (8 h, sliding) plus OpenID Connect (code flow with PKCE, scopes
   `openid profile`, no tokens kept). `SignInRequiredMiddleware` applies the policy (signed in, optionally
   `RequiredGroup` in the `groups` claim and `RequiredRole`) to everything except `/health`: the page redirects to the
   identity provider, API calls get 401, a signed-in user without the group or role gets 403.
7. **Owner** (`Security/OwnerMiddleware.cs`, only for `/api`):
   - **CSRF:** a changing request (anything but GET/HEAD) without the header `X-App-Request` gets 403. A foreign page
     cannot set it without a CORS permission, which the server never gives.
   - **Who owns the files:** in mode `Oidc` the signed-in user (`sub` claim); in mode `None` an anonymous session from the
     cookie `__Host-session` (256 random bits, `Secure`, `HttpOnly`, `SameSite=Strict`), created on first use or when
     the old one is unknown or expired. The owner key is a hash (`s…` for sessions, `u…` for users,
     `OwnerKeys`); neither the session id nor the user id is written to disk.
8. **API** (next section).
9. **Static files** (`StaticFiles.cs`), from `WebRoot` (default `wwwroot` next to the server):
   `PrecompressedFilesMiddleware` sends the `.br` copy of the publish when the browser accepts Brotli; `StaticCaching`
   lets the browser keep files with a content hash in their name (`_framework/*.<hash>.wasm`) for 30 days
   (`immutable`) and makes it ask for all others every time, answered with 304 by ETag while unchanged. Unknown paths
   (and `/` itself) get `index.html` (fallback). Without a web root only the API is served (warning in the log).

## API (`Api.cs`)
All file endpoints only find files of the request's owner. Every pdfium job has a time limit of 5 minutes and is also
cancelled when the browser aborts the request.

| Endpoint | What happens |
|---|---|
| `GET /api/config` | Sign-in mode, upload limit, retention times, when the server was deployed (build time of its program file), whether it runs in a container (`ServerHosting`) |
| `GET /api/library` | One ZIP with the sample stamps and the shared stamps and templates of `Storage:SharedFolder` (`Storage/Library.cs`; single files above 5 MB and anything beyond 50 MB in total are left out). ETag = hash of the ZIP, whose entries carry the files' own times, so an unchanged library answers 304 |
| `GET /api/downloads`, `/api/downloads/{name}` | Desktop packages in `Storage:DownloadsFolder`, the newest of each kind (`Downloads.cs`); only listed names are served, never other files or paths |
| `POST /api/files` | **Upload:** checks the size (`Content-Length` and while reading) and the quotas, streams the body into the owner's folder, checks for `%PDF-` within the first 1024 bytes (else 415), registers the file and opens it with pdfium. Answers id, name, page count and all page sizes; a password-protected or damaged file gets 422 and is deleted. The file name comes in the header `X-File-Name` and is cleaned (`FileStore.CleanName`) |
| `GET /api/files/{id}/pages/{n}?w&h&rot` | Renders a page as PNG (`PngCodec` in Core, no native image library); at most `RateLimits:MaxRenderPixels` (40 million) |
| `GET /api/files/{id}/pages/{n}/area` | Renders part of a page (zoom, copy area) |
| `GET /api/files/{id}/pages/{n}/words` | Words of the text layer with their boxes (find, select) |
| `GET /api/files/{id}/pages/{n}/font` | The font most of the page's visible text is written in, with its size (`PageFont`; new text "like the text on the page"); 204 without visible text |
| `GET /api/files/{id}/pages/{n}/chars` | Number of characters of the text layer (whether a page is a scan) |
| `GET /api/files/{id}/pages/{n}/ocr?rot` | Text recognition of one page (see "pdfium and OCR") |
| `GET /api/files/{id}/attachments` | List of the embedded files |
| `GET /api/files/{id}/attachments/{i}[?view=true]` | One embedded file as download; with `view=true` an XML file (e-invoice) as indented plain text under `sandbox; default-src 'none'` (`XmlText`, DTDs prohibited) |
| `GET /api/files/{id}/content[?inline=true]` | The PDF itself, as download or for the browser's viewer (printing); range requests supported; opened with `FileShare.Delete`, so it may expire while it is being sent |
| `DELETE /api/files/{id}` | Closes and deletes the file |
| `POST /api/build` | **Save:** the page list (`RemotePage`: file id, source page, rotation, stamps as BGRA pixels, texts, black out/white out boxes, recognized words) and a base file; optionally image reduction (36–1200 dpi, JPEG quality 10–100) or "save as image" (`Rasterize`: 150, 200 or 300 dpi, colour or grey). Written by `PdfSaver.WriteDocumentAsync` into a new file with a new id; the answer says how many images were reduced |
| `POST /api/extract` | Pages into a new PDF (`PdfSaver.ExtractAsync`) |
| `POST /api/split` | Writes the parts into a temporary folder in the owner's folder, answers them as one ZIP and removes the folder |
| `POST /api/blank` | A blank page of the given size (1–14400 pt) as a new PDF |

**Limits on page lists** (`ValidOverlays`), so a request cannot make the server allocate without end: at most 100,000
pages; per page at most 500 stamps, 2000 texts, 5000 boxes, 100,000 recognized words; stamps at most 10,000 × 10,000
pixels with exactly width × height × 4 bytes; texts at most 10,000 characters, font size below 2000; only finite,
non-negative rectangles; rotations in steps of 90°. Anything else gets 400.

## Files and retention (`Storage/FileStore.cs`, `Storage/CleanupService.cs`)
- **Storage:** `Storage:Folder/<owner key>/<id>.pdf`; the id is 128 random bits. The list of files exists only in memory,
  so the server forgets all files at a restart.
- **Access** only by id *and* owner: a foreign or guessed id is simply 404.
- **Quotas** (defaults): 200 MB per upload (`MaxUploadMegabytes`), 1 GB per owner (`MaxOwnerMegabytes`), 10 GB on the
  whole server (`MaxTotalMegabytes`); exceeding one gets 413 or 507.
- **A file is deleted** when its owner deletes it (the app does so when the document is closed, i.e. its last reference
  goes), 30 minutes after its last use (`IdleTimeoutMinutes`; every request for the file counts as use) or at the latest
  8 hours after it came to the server (`MaxAgeHours`). An anonymous session expires after 30 minutes without requests
  (`Authentication:SessionIdleMinutes`) and takes all its files with it, so an old cookie never reaches them again.
- **`CleanupService`:** at start-up deletes everything an earlier run left in the storage folder; then, every
  `CleanupIntervalSeconds` (default 60), removes expired files and sessions, closes their pdfium documents and deletes
  them. Files still open when removed (on Windows e.g. a running download) are tried again in the next pass.

The files are a temporary working copy only: the result is downloaded by the user, and the storage folder is not worth
a backup.

## pdfium and OCR
- **`Storage/OpenDocuments.cs`** keeps up to 64 files opened with pdfium (by file id, least recently used is closed first),
  so a page view that renders many pages one after the other does not open the file again for each. Opening also reads
  all page sizes. As on the desktop, every pdfium call runs on the one thread of `PdfiumWorker`; rendering for the
  browser uses the priority of visible pages.
- **`Storage/OcrService.cs`:** one `RapidOcrEngine`, its models loaded on first use (about 0.3 s), and one page at a time
  (semaphore): OCR takes a few seconds of a full CPU core per page, several at once would only slow each other down. The
  browser's `PageOcr` asks the server through `IServerOcr`.

## Example: open, edit, save
1. The browser loads the program (`index.html`, `_framework/*`, mostly from its cache), asks `/api/config` (on a first
   visit this sets the session cookie), then `/api/downloads` and `/api/library`.
2. The user drops a file: `POST /api/files` answers id and page sizes; the document's virtual path is
   `/remote/<id>/<name>`.
3. Reading: `GET …/pages/n` for each visible page (PNG), `…/words` for find and select.
4. Reordering pages, placing stamps, blacking out: **only in the browser**, the server knows nothing of it.
5. Saving: `POST /api/build` with the whole page list; the server writes a new PDF and answers its id; the browser fetches
   it from `…/content` (download, browser file API, or the share sheet).
6. When the document is closed or after 30 minutes without use, the files are gone.

## Operation
- **Image** (`Dockerfile`): built in the SDK image, runs the core and server tests (a failing test stops the build),
  publishes the server self-contained with ReadyToRun and the browser program with WebAssembly AOT into `wwwroot`. The
  runtime image is `runtime-deps` *chiseled-extra*: no shell, non-root user `app`, port 8080, `HEALTHCHECK` with
  `--healthcheck`.
- **Volumes:** `/data/files` (temporary files), `/data/keys` (data protection keys), `/data/shared` (shared stamps and
  templates, read-only for users), `/data/downloads` (desktop packages; not in the image, so new installers need no new
  image).
- **Hardening of the container** (see DESIGN.md): `--read-only`, `--cap-drop=ALL`, `no-new-privileges`, memory limit,
  behind a reverse proxy for HTTPS.
- **Tests:** `tests/EasyPDF.Server.Tests` run the server in memory (`TestServer`, simulated clock): API, separation of
  sessions, CSRF, limits, retention, headers.
