@echo off
rem Installs or updates the WWDS mods into both games (found through Steam). Safe to run again.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %*
echo.
pause
