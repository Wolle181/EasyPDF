# Container of the web version: the server (src/EasyPDF.Server, pdfium) with the browser program (src/EasyPDF.Browser,
# .NET WebAssembly) in its wwwroot. Built by BuildContainer.sh / BuildContainer.cmd (scripts/BuildContainer.ps1), which
# pass brand, version and commit; see docs/BUILDING.md "Web version".
#
#   podman build --build-arg BRAND=easypdf --build-arg VERSION=1.0.0.123 --build-arg COMMIT=abcdef12 -t web .

# ---- Stage 1: test and publish ---------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BRAND=easypdf
ARG VERSION=1.0.0.0
ARG COMMIT=
WORKDIR /src

# WebAssembly toolchain for the browser program; its Emscripten needs Python
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/* \
    && dotnet workload install wasm-tools

# Project files first: the restore is cached as long as the dependencies do not change
COPY global.json Directory.Build.props Directory.Build.targets EasyPDF.slnx ./
COPY branding/ ./branding/
COPY src/EasyPDF.Core/EasyPDF.Core.csproj src/EasyPDF.Core/
COPY src/EasyPDF.Remote/EasyPDF.Remote.csproj src/EasyPDF.Remote/
COPY src/EasyPDF.UI/EasyPDF.UI.csproj src/EasyPDF.UI/
COPY src/EasyPDF.Browser/EasyPDF.Browser.csproj src/EasyPDF.Browser/
COPY src/EasyPDF.Server/EasyPDF.Server.csproj src/EasyPDF.Server/
COPY tests/EasyPDF.Core.Tests/EasyPDF.Core.Tests.csproj tests/EasyPDF.Core.Tests/
COPY tests/EasyPDF.Server.Tests/EasyPDF.Server.Tests.csproj tests/EasyPDF.Server.Tests/
RUN dotnet restore src/EasyPDF.Server/EasyPDF.Server.csproj -p:Brand=$BRAND \
    && dotnet restore src/EasyPDF.Browser/EasyPDF.Browser.csproj -p:Brand=$BRAND \
    && dotnet restore tests/EasyPDF.Core.Tests/EasyPDF.Core.Tests.csproj -p:Brand=$BRAND \
    && dotnet restore tests/EasyPDF.Server.Tests/EasyPDF.Server.Tests.csproj -p:Brand=$BRAND

COPY demo-pdfs/ ./demo-pdfs/
COPY licenses/ ./licenses/
COPY LICENSE ./
COPY src/ ./src/
COPY tests/EasyPDF.Core.Tests/ ./tests/EasyPDF.Core.Tests/
COPY tests/EasyPDF.Server.Tests/ ./tests/EasyPDF.Server.Tests/

# Gate: core and server tests (the UI tests run before the image build in the build script). A red test stops the
# image build, nothing is pushed.
ENV TZ=Europe/Berlin
RUN dotnet test tests/EasyPDF.Core.Tests/EasyPDF.Core.Tests.csproj --no-restore --nologo -p:Brand=$BRAND \
    && dotnet test tests/EasyPDF.Server.Tests/EasyPDF.Server.Tests.csproj --no-restore --nologo -p:Brand=$BRAND

# Server: self-contained, ReadyToRun (fast start, the JIT still optimizes hot code later). appsettings.Development.json
# stays out; the production settings come from the mounted config folder, never from the image.
RUN dotnet publish src/EasyPDF.Server/EasyPDF.Server.csproj -c Release -r linux-x64 --self-contained true \
        -p:PublishReadyToRun=true -p:DebugType=None -p:Brand=$BRAND -p:Version=$VERSION -p:SourceRevisionId=$COMMIT \
        -o /app/publish \
    && rm -f /app/publish/appsettings.Development.json

# Browser program (WebAssembly, with precompressed .br/.gz files) into the server's wwwroot
RUN dotnet publish src/EasyPDF.Browser/EasyPDF.Browser.csproj -c Release -p:Brand=$BRAND -p:Version=$VERSION \
        -p:SourceRevisionId=$COMMIT -o /app/browser \
    && mv /app/browser/wwwroot /app/publish/wwwroot

# ---- Stage 2: run ----------------------------------------------------------
# Chiseled ("distroless"-like): no shell, no package manager, non-root by default - minimal attack surface. "-extra":
# with ICU (German/English formats) and tzdata. pdfium (libpdfium.so) needs only glibc and libstdc++, which it has.
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled-extra AS final
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    TZ=Europe/Berlin
WORKDIR /app
COPY --from=build /app/publish .
# Writes only to /data (mounted: files, keys) and /tmp, so the container can run with a read-only root file system
USER app
EXPOSE 8080
# Without shell or curl: the server checks itself (Docker; Podman's OCI images ignore this, start.sh sets --health-cmd)
HEALTHCHECK --interval=60s --timeout=10s --retries=3 CMD ["/app/EasyPDF.Server", "--healthcheck"]
ENTRYPOINT ["/app/EasyPDF.Server"]
