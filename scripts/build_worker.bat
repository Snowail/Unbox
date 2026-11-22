@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_worker.ps1"
exit /b %errorlevel%
