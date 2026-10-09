@echo off
rem Removes the WWDS mods (and BepInEx, if the setup installed it) from both games.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
echo.
pause
