@echo off
setlocal EnableExtensions enabledelayedexpansion
title Build Cadence - Release

:: ============================================================
:: Cadence - LSPDFR Ambient Policing System
:: Fast Automated Build & Packaging Script
:: ============================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..") do set "ROOT=%%~fI"

set "SRC=%ROOT%\src"
set "DIST=%ROOT%\dist"
set "STAGE=%DIST%\_staging"
set "SLN=%SRC%\Cadence.sln"
set "PROJ=%SRC%\Cadence.csproj"

echo ============================================================
echo   Cadence - Fast Solution Build ^& Packaging
echo ============================================================
echo.

:: Locate MSBuild
set "MSBUILD_CMD="
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" (
    set "MSBUILD_CMD=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD_CMD if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" (
    set "MSBUILD_CMD=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD_CMD if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe" (
    set "MSBUILD_CMD=C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD_CMD if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe" (
    set "MSBUILD_CMD=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD_CMD (
    where msbuild >nul 2>nul
    if %errorlevel%==0 set "MSBUILD_CMD=msbuild"
)

if not defined MSBUILD_CMD (
    echo [ERROR] MSBuild.exe was not found. Please verify Visual Studio 2022 or Build Tools is installed.
    echo.
    pause
    exit /b 1
)

:: Choose build target (Solution or Project)
set "BUILD_TARGET="
if exist "%SLN%" (
    set "BUILD_TARGET=%SLN%"
) else if exist "%PROJ%" (
    set "BUILD_TARGET=%PROJ%"
) else (
    echo [ERROR] Neither src\Cadence.sln nor src\Cadence.csproj was found.
    echo.
    pause
    exit /b 1
)

echo [BUILD] Compiling Cadence in Release configuration (x64)...
echo Target: "%BUILD_TARGET%"
echo Compiler: "%MSBUILD_CMD%"
echo.

"%MSBUILD_CMD%" "%BUILD_TARGET%" /p:Configuration=Release /p:Platform=x64 /p:TreatWarningsAsErrors=true /warnaserror /nologo /v:m
if errorlevel 1 (
    echo.
    echo ============================================================
    echo   [BUILD ERROR] Compilation failed with error code %errorlevel%.
    echo ============================================================
    echo.
    pause
    exit /b %errorlevel%
)

:: Package output into clean zip archive using temporary staging
echo.
echo [PACKAGE] Assembling distribution release into Cadence_v1.0.0.zip...

if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%\plugins\LSPDFR" >nul 2>nul

set "BUILT_DLL="
if exist "%SRC%\bin\Release\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\Cadence.dll"
if exist "%SRC%\bin\Release\net48\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\net48\Cadence.dll"
if exist "%ROOT%\bin\Release\Cadence.dll" set "BUILT_DLL=%ROOT%\bin\Release\Cadence.dll"

if defined BUILT_DLL (
    copy /y "%BUILT_DLL%" "%STAGE%\plugins\LSPDFR\Cadence.dll" >nul
    echo   Staged Cadence.dll
) else (
    echo   [WARN] Could not find compiled Cadence.dll to package.
)

if exist "%SRC%\Cadence.ini" (
    copy /y "%SRC%\Cadence.ini" "%STAGE%\plugins\LSPDFR\Cadence.ini" >nul
    echo   Staged Cadence.ini
)

:: Create README.txt in staging
(
echo ============================================================
echo   Cadence v1.0.0 - Ambient Policing System for LSPDFR
echo ============================================================
echo.
echo INSTALLATION:
echo 1. Drag the "plugins" folder from this zip into your Grand Theft Auto V root folder.
echo 2. Verify that Cadence.dll and Cadence.ini reside in:
echo      Grand Theft Auto V\plugins\LSPDFR\
echo 3. Launch GTA V via RagePluginHook and go On Duty.
echo.
echo IN-GAME MENU:
echo   Press F8 (configurable in Cadence.ini^) to open the Cadence menu.
echo.
echo COMPATIBILITY:
echo   Fully compatible with Ultimate Backup, StopThePed, and custom callout packs.
) > "%STAGE%\README.txt"

:: Remove old zip before packaging
if exist "%DIST%\Cadence_v1.0.0.zip" del /f /q "%DIST%\Cadence_v1.0.0.zip" >nul 2>nul

:: Create clean release archive
where tar >nul 2>nul
if %errorlevel%==0 (
    tar -a -c -f "%DIST%\Cadence_v1.0.0.zip" -C "%STAGE%" plugins README.txt
    echo   Created release archive -^> dist\Cadence_v1.0.0.zip
) else (
    powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%DIST%\Cadence_v1.0.0.zip' -Force"
    echo   Created release archive via PowerShell -^> dist\Cadence_v1.0.0.zip
)

:: Clean up all temporary staging folders and loose files in dist
if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
if exist "%DIST%\plugins" rmdir /s /q "%DIST%\plugins" >nul 2>nul
if exist "%DIST%\README.txt" del /f /q "%DIST%\README.txt" >nul 2>nul

echo.
echo ============================================================
echo   [BUILD SUCCESS] Release archive ready: dist\Cadence_v1.0.0.zip
echo ============================================================
exit /b 0
