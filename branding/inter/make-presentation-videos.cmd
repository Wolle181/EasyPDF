@echo off
REM Records the presentation videos (German and English) of the brand whose folder this script is in, into
REM videos\ of that folder (introduction.de.mp4, introduction.en.mp4; shipped with the app). The same file works in every
REM brand folder: copy it along with a new brand.
FOR %%I IN ("%~dp0.") DO SET "EASYPDF_BRAND=%%~nxI"
ECHO Brand: %EASYPDF_BRAND%
CALL "%~dp0..\..\tools\DemoVideo\make-video.cmd"
