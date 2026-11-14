@echo off
ECHO Working...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0linux\build-linux-on-windows.ps1" %*
REM Failed: keep the window open so the error can be read
IF ERRORLEVEL 1 (
    PAUSE
    EXIT /B 1
)
REM Built: show the installers in Explorer and close the window
IF EXIST "%~dp0..\artifacts\installer" explorer "%~dp0..\artifacts\installer"
EXIT /B 0
