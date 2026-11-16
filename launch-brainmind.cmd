@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\launch-brainmind.ps1" %*
exit /b %errorlevel%
