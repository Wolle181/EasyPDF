@echo off
SETLOCAL
REM Records the demo video in German and English into branding\<id>\videos\introduction.<de|en>.mp4 (committed with the
REM brand and shipped with the app: the button in the header plays it). ffmpeg is not installed: it is
REM downloaded into a temporary folder, used and deleted again. Needs the .NET SDK. The brand is the one the build
REM selects (EASYPDF_BRAND); branding\<id>\make-presentation-videos.cmd sets it and calls this.

SET "URL=https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
SET "WORK=%TEMP%\demo-video-ffmpeg-%RANDOM%%RANDOM%"
REM The tools of Windows by full path: a Git installation on the PATH brings a tar that cannot read zip files
SET "TOOLS=%SystemRoot%\System32"

REM The repository folder selects the SDK (global.json), wherever this was started from
PUSHD "%~dp0"
MKDIR "%WORK%" || GOTO failed

ECHO Downloading ffmpeg (about 110 MB)...
"%TOOLS%\curl.exe" -L --fail --progress-bar -o "%WORK%\ffmpeg.zip" "%URL%" || GOTO failed
"%TOOLS%\curl.exe" -sSL --fail -o "%WORK%\ffmpeg.sha256" "%URL%.sha256" || GOTO failed

SET "EXPECTED="
SET /P EXPECTED=<"%WORK%\ffmpeg.sha256"
SET "ACTUAL="
FOR /F "skip=1 delims=" %%H IN ('certutil -hashfile "%WORK%\ffmpeg.zip" SHA256') DO IF NOT DEFINED ACTUAL SET "ACTUAL=%%H"
IF /I NOT "%ACTUAL: =%"=="%EXPECTED%" (
    ECHO The download of ffmpeg is damaged: its checksum does not match.
    GOTO failed
)

ECHO Unpacking ffmpeg...
"%TOOLS%\tar.exe" -xf "%WORK%\ffmpeg.zip" -C "%WORK%" || GOTO failed
SET "FFMPEG="
FOR /R "%WORK%" %%F IN (ffmpeg.exe) DO IF EXIST "%%F" SET "FFMPEG=%%F"
IF NOT DEFINED FFMPEG (
    ECHO ffmpeg.exe was not found in the download.
    GOTO failed
)

ECHO Recording in German, this takes a few minutes...
dotnet run -c Release --project DemoVideo.csproj -- --ffmpeg "%FFMPEG%" --language de || GOTO failed
ECHO Recording in English, this takes a few minutes...
dotnet run -c Release --project DemoVideo.csproj -- --ffmpeg "%FFMPEG%" --language en || GOTO failed

RMDIR /S /Q "%WORK%"
POPD
REM Recorded: show the videos in Explorer and close the window
IF DEFINED EASYPDF_BRAND (explorer "%~dp0..\..\branding\%EASYPDF_BRAND%\videos") ELSE explorer "%~dp0..\..\branding"
EXIT /B 0

:failed
REM Keep the window open so the error can be read
IF EXIST "%WORK%" RMDIR /S /Q "%WORK%"
POPD
ECHO Recording failed.
PAUSE
EXIT /B 1
