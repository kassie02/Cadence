@echo off
setlocal EnableExtensions enabledelayedexpansion
title Promote DEV to MAIN - Cadence

echo ============================================================
echo   Cadence - Promote DEV to MAIN (Release)
echo ============================================================
echo.
echo This will merge all tested changes from 'dev' into 'main'
echo and push 'main' to GitHub (https://github.com/kassie02/Cadence).
echo.
set /p CONFIRM="Have you verified your changes in GTA V? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Aborted.
    pause
    exit /b 0
)

cd /d "%~dp0\.."

echo.
echo Switching to main branch...
git checkout main 2>nul
if errorlevel 1 (
    echo Creating and switching to 'main' branch...
    git checkout -b main
    if errorlevel 1 goto error
)

echo Pulling latest main...
git pull origin main 2>nul

echo Merging dev into main...
git merge dev --no-ff -m "Release: Merge dev into main"
if errorlevel 1 goto error

echo Pushing main to GitHub...
git push -u origin main
if errorlevel 1 goto error

echo Switching back to dev branch...
git checkout dev

echo.
echo ============================================================
echo   [SUCCESS] Dev changes successfully promoted to main!
echo ============================================================
pause
exit /b 0

:error
echo.
echo [ERROR] An error occurred during promotion. Check git status.
pause
