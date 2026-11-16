@echo off
REM Trial: Native AOT build of the app as .msi and portable .zip (installer\windows\build-aot-windows-on-windows.ps1)
ECHO Working...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\build-aot-windows-on-windows.ps1" %*
REM Failed: keep the window open so the error can be read
IF ERRORLEVEL 1 (
    PAUSE
    EXIT /B 1
)
REM Built: show the result in Explorer and close the window
IF EXIST "%~dp0..\artifacts\installer" explorer "%~dp0..\artifacts\installer"
EXIT /B 0
