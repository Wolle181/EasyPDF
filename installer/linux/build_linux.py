"""Builds the Linux packages from the linux-x64 publish output, without dpkg (runs on Linux, macOS and Windows):

  artifacts/installer/<package>_<version>_amd64.deb      Debian/Ubuntu (apt install ./file.deb)
  artifacts/installer/<name>-<version>-linux-x64.tar.gz  any distribution (unpack and run)

<name> is AppDisplayName from the brand profile (branding/<Brand>/Branding.props), <package> the same in lower case: package
and command name. There is only ever one package: a newer version replaces the installed one, an older one is refused
(preinst). Package version = app version 1.0.0.<number of commits>, so every commit is newer; apt reports a package of
the same commit as already installed (to install a local rebuild anyway: apt install --reinstall ./file.deb).
Layout of the .deb: /opt/<name> (app, program file <name>), /usr/bin/<package> (link), desktop entry and hicolor icons,
file manager context menus "Read with" (one PDF) / "Merge with" (several PDFs) for Dolphin (KDE) and Nemo (Cinnamon).
GNOME Files has no installable context menus; there "Open With <app>" on several PDFs also merges them.
The .desktop and .nemo_action files say @APPNAME@ (shown name) and @PACKAGE@ (command, icon); installed they are
named after the package. The Maintainer is Company from the brand profile, the icons are the brand's (branding/<Brand>/icons).
Usage: python installer/linux/build_linux.py   (after: dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64)
"""
import gzip
import io
import os
import re
import subprocess
import sys
import tarfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PUBLISH = os.path.join(ROOT, "artifacts", "publish", "linux-x64")
OUT = os.path.join(ROOT, "artifacts", "installer")
HERE = os.path.dirname(os.path.abspath(__file__))
EXECUTABLE = ""   # program file = shown name, set by main()
MTIME = int(time.time())


def version():
    """The app's version, 1.0.0.<number of commits>, as computed by Directory.Build.targets."""
    out = subprocess.run(["dotnet", "msbuild", os.path.join(ROOT, "src", "EasyPDF.App"), "-getProperty:Version",
                          "-t:SetVersionFromGit"], capture_output=True, text=True, check=True).stdout.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", out):
        sys.exit(f"Unexpected version from dotnet msbuild: {out!r}")
    return out


def build_property(prop):
    """A property of the build, e.g. from the brand profile (AppDisplayName, Company, BrandDir)."""
    out = subprocess.run(["dotnet", "msbuild", os.path.join(ROOT, "src", "EasyPDF.App"), f"-getProperty:{prop}"],
                         capture_output=True, text=True, check=True).stdout.strip()
    if not out:
        sys.exit(f"{prop} missing in the build properties")
    return out


def package_name(name):
    """Debian package and command name: the shown name in lower case, only characters Debian allows."""
    package = re.sub(r"[^a-z0-9+.-]", "", name.lower())
    if not re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", package):
        sys.exit(f"No valid package name from AppDisplayName {name!r}")
    return package


def with_name(path, name, package):
    """A desktop/nemo file with @APPNAME@ and @PACKAGE@ replaced."""
    with open(path, "rb") as f:
        return (f.read().replace(b"\r\n", b"\n").replace(b"@APPNAME@", name.encode("utf-8"))
                .replace(b"@PACKAGE@", package.encode("ascii")))


def is_executable(name):
    return name == EXECUTABLE or name.endswith(".so") or ".so." in name or name == "createdump"


def add_dir(tar, path):
    info = tarfile.TarInfo(path)
    info.type, info.mode, info.mtime = tarfile.DIRTYPE, 0o755, MTIME
    tar.addfile(info)


def add_bytes(tar, path, data, mode=0o644):
    info = tarfile.TarInfo(path)
    info.size, info.mode, info.mtime = len(data), mode, MTIME
    tar.addfile(info, io.BytesIO(data))


def add_file(tar, path, source, mode):
    with open(source, "rb") as f:
        add_bytes(tar, path, f.read(), mode)


def add_symlink(tar, path, target):
    info = tarfile.TarInfo(path)
    info.type, info.linkname, info.mode, info.mtime = tarfile.SYMTYPE, target, 0o777, MTIME
    tar.addfile(info)


def add_tree(tar, prefix):
    """The publish output below prefix; returns its size in bytes."""
    total = 0
    for folder, dirs, files in os.walk(PUBLISH):
        dirs.sort()
        rel = os.path.relpath(folder, PUBLISH).replace(os.sep, "/")
        target = prefix if rel == "." else f"{prefix}/{rel}"
        add_dir(tar, target)
        for name in sorted(files):
            source = os.path.join(folder, name)
            add_file(tar, f"{target}/{name}", source, 0o755 if is_executable(name) else 0o644)
            total += os.path.getsize(source)
    return total


def tar_gz(build):
    buffer = io.BytesIO()
    with gzip.GzipFile(fileobj=buffer, mode="wb", mtime=MTIME) as gz:
        with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tar:
            build(tar)
    return buffer.getvalue()


def ar_archive(members):
    """Debian packages are 'ar' archives: debian-binary, control.tar.gz, data.tar.gz (in this order)."""
    out = io.BytesIO()
    out.write(b"!<arch>\n")
    for name, data in members:
        header = f"{name:<16}{MTIME:<12}{0:<6}{0:<6}{'100644':<8}{len(data):<10}`\n"
        out.write(header.encode("ascii"))
        out.write(data)
        if len(data) % 2:
            out.write(b"\n")
    return out.getvalue()


def build_deb(ver, name, package, maintainer, icons):
    installed = [0]

    def data(tar):
        for d in ("./", "./opt", "./usr", "./usr/bin", "./usr/share", "./usr/share/applications", "./usr/share/icons",
                  "./usr/share/icons/hicolor", "./usr/share/kio", "./usr/share/kio/servicemenus", "./usr/share/nemo",
                  "./usr/share/nemo/actions"):
            add_dir(tar, d)
        installed[0] += add_tree(tar, f"./opt/{name}")
        add_symlink(tar, f"./usr/bin/{package}", f"/opt/{name}/{EXECUTABLE}")
        # Source files keep their names in the repository, installed they are named after the package
        add_bytes(tar, f"./usr/share/applications/{package}.desktop",
                  with_name(os.path.join(HERE, "easypdf.desktop"), name, package))
        menus = os.path.join(HERE, "filemanager")
        for action in ("read", "merge"):
            add_bytes(tar, f"./usr/share/kio/servicemenus/{package}-{action}.desktop",
                      with_name(os.path.join(menus, f"easypdf-{action}.desktop"), name, package))
            add_bytes(tar, f"./usr/share/nemo/actions/{package}-{action}.nemo_action",
                      with_name(os.path.join(menus, f"easypdf-{action}.nemo_action"), name, package))
        for size in (16, 24, 32, 48, 64, 128, 256, 512):
            base = f"./usr/share/icons/hicolor/{size}x{size}"
            add_dir(tar, base)
            add_dir(tar, f"{base}/apps")
            add_file(tar, f"{base}/apps/{package}.png", os.path.join(icons, f"app-{size}.png"), 0o644)

    data_tar = tar_gz(data)
    control_text = "\n".join([
        f"Package: {package}",
        f"Version: {ver}",
        "Section: graphics",
        "Priority: optional",
        "Architecture: amd64",
        f"Maintainer: {maintainer}",
        f"Installed-Size: {installed[0] // 1024 + 1}",
        # Avalonia (X11, fontconfig), .NET globalization (ICU, any of the common versions);
        # ONNX Runtime (OCR) needs glibc 2.28 (Debian 10, Ubuntu 18.10) and libstdc++ (GLIBCXX_3.4.22)
        "Depends: libc6 (>= 2.28), libstdc++6 (>= 6), libfontconfig1, libx11-6, libice6, libsm6, "
        "libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu63",
        f"Homepage: {build_property('RepositoryUrl')}",
        "Description: Read and reorganize PDF documents",
        f" {name} shows PDF pages side by side and lets you reorder, rotate, delete,",
        " insert, extract and split pages, and concatenate files. Text recognition (OCR,",
        " German and English) makes scanned pages searchable. Changes stay in memory",
        " (undo/redo) until you save.",
        "",
    ]).encode("utf-8")
    # preinst refuses to replace a newer installed version; a Git checkout on Windows may have given it CRLF
    with open(os.path.join(HERE, "preinst"), "rb") as f:
        preinst = f.read().replace(b"\r\n", b"\n")
    control_tar = tar_gz(lambda tar: (add_dir(tar, "./"), add_bytes(tar, "./control", control_text),
                                      add_bytes(tar, "./preinst", preinst, 0o755)))
    deb = ar_archive([("debian-binary", b"2.0\n"), ("control.tar.gz", control_tar), ("data.tar.gz", data_tar)])
    path = os.path.join(OUT, f"{package}_{ver}_amd64.deb")
    with open(path, "wb") as f:
        f.write(deb)
    return path


def build_tarball(ver, name, package, icons):
    folder = f"{name}-{ver}"

    def content(tar):
        add_tree(tar, folder)
        add_file(tar, f"{folder}/{package}.png", os.path.join(icons, "app-256.png"), 0o644)

    path = os.path.join(OUT, f"{name}-{ver}-linux-x64.tar.gz")
    with open(path, "wb") as f:
        f.write(tar_gz(content))
    return path


def main():
    global EXECUTABLE
    ver, name = version(), build_property("AppDisplayName")
    package = package_name(name)
    EXECUTABLE = name
    if not os.path.isfile(os.path.join(PUBLISH, EXECUTABLE)):
        sys.exit(f"{PUBLISH}/{EXECUTABLE} not found. Publish first: dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64")
    icons = os.path.join(build_property("BrandDir"), "icons")
    os.makedirs(OUT, exist_ok=True)
    for path in (build_deb(ver, name, package, build_property("Company"), icons), build_tarball(ver, name, package, icons)):
        print(f"{path}  ({os.path.getsize(path) // 1024} KB)")


if __name__ == "__main__":
    main()
