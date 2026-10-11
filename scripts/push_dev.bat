@echo off
setlocal EnableExtensions enabledelayedexpansion
title Push to Dev - Cadence

echo ============================================================
echo   Cadence - Push Changes to DEV Branch
echo ============================================================
echo.

:: Ensure we are in project root
cd /d "%~dp0\.."

:: Check git is installed
where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git is not installed or not in PATH!
    pause
    exit /b 1
)

:: Check current git branch
for /f "tokens=*" %%i in ('git branch --show-current 2^>nul') do set CURRENT_BRANCH=%%i

if "%CURRENT_BRANCH%"=="" (
    echo [INFO] No active branch detected. Initializing 'dev' branch...
    git checkout -b dev 2>nul
    set CURRENT_BRANCH=dev
)

if not "%CURRENT_BRANCH%"=="dev" (
    echo [WARNING] Current branch is '%CURRENT_BRANCH%', not 'dev'!
    set /p SWITCH="Switch to dev branch now? (Y/N): "
    if /i "!SWITCH!"=="Y" (
        git checkout dev 2>nul
        if errorlevel 1 (
            echo Creating new 'dev' branch...
            git checkout -b dev
        )
    ) else (
        echo Aborted. Please switch to 'dev' before running this script.
        pause
        exit /b 1
    )
)

echo.
echo Changes detected:
git status -s
echo.

set "MSG="
set /p MSG="Enter commit message (press Enter for default): "
if "%MSG%"=="" (
    for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value 2^>nul') do set dt=%%I
    if defined dt (
        set "MSG=Cadence Dev update !dt:~0,4!-!dt:~4,2!-!dt:~6,2! !dt:~8,2!:!dt:~10,2!"
    ) else (
        set "MSG=Cadence Dev update"
    )
)

echo.
echo Staging files...
git add -A

echo Committing: "%MSG%"...
git commit -m "%MSG%"
if errorlevel 1 (
    echo [INFO] Nothing new to commit or commit cancelled.
)

echo.
echo Pushing to GitHub (origin dev)...
git push -u origin dev

if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo   [SUCCESS] Dev branch successfully pushed to GitHub!
    echo ============================================================
) else (
    echo.
    echo [ERROR] Push failed. Check your network or GitHub credentials.
)

echo.
pause
