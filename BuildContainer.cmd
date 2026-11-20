@echo off
REM Builds and pushes the web version's container image (installer\container\build-container.ps1, needs pwsh and Podman).
REM Parameters are passed on, e.g.: BuildContainer.cmd -NoPush
pwsh -NoProfile -File "%~dp0installer\container\build-container.ps1" %*
if errorlevel 1 (
    ECHO Build failed!
    PAUSE
    EXIT /B 1
)
PAUSE
