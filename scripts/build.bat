@echo off
setlocal
cd /d "%~dp0\..\dist"
call build.bat
exit /b %errorlevel%
