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
echo [PACKAGE] Assembling distribution release into Cadence_v1.1.0.zip...

if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%\plugins\LSPDFR" >nul 2>nul
mkdir "%STAGE%\plugins\LSPDFR\Cadence\Logs" >nul 2>nul
type nul > "%STAGE%\plugins\LSPDFR\Cadence\Logs\.placeholder" 2>nul

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
echo   Staged plugins\LSPDFR\Cadence\Logs\

if exist "%SRC%\Audio\PANIC_BUTTON.wav" (
    mkdir "%STAGE%\plugins\LSPDFR\Cadence\Audio" >nul 2>nul
    copy /y "%SRC%\Audio\PANIC_BUTTON.wav" "%STAGE%\plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav" >nul
    echo   Staged plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav
)

:: Create README.txt in staging
(
echo ============================================================
echo   Cadence v1.1.0 - Ambient Policing System for LSPDFR
echo ============================================================
echo.
echo INSTALLATION:
echo 1. Drag the "plugins" folder from this zip into your Grand Theft Auto V root folder.
echo 2. Verify that files reside in:
echo      Grand Theft Auto V\plugins\LSPDFR\Cadence.dll
echo      Grand Theft Auto V\plugins\LSPDFR\Cadence.ini
echo      Grand Theft Auto V\plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav
echo      Grand Theft Auto V\plugins\LSPDFR\Cadence\Logs\
echo 3. Launch GTA V via RagePluginHook and go On Duty.
echo.
echo CONTROLS ^& KEYBINDINGS ^(DEFAULT^):
echo   - Cadence Dashboard:   Q + PageDown  ^(MenuKey = PageDown, MenuModifierKey = Q^)
echo   - Lean Backup Menu:    X + PageDown  ^(BackupMenuKey = PageDown, BackupMenuModifierKey = X^)
echo   - Panic Button:        LControlKey + Z  ^(PanicButtonKey = Z, PanicButtonModifierKey = LControlKey^)
echo   - Group Backup:        LControlKey + C  ^(GroupBackupKey = C, GroupBackupModifierKey = LControlKey^)
echo   - Dismiss Backup:      Enter  ^(DismissBackupKey = Enter, hold ~1.2s^)
echo.
echo KEYBIND CONFIGURATION ^& DISABLING:
echo   All keybinds are fully configurable in plugins\LSPDFR\Cadence.ini.
echo   Setting any key or modifier to None, 0, Disabled, or false turns it off.
echo.
echo MASTER ACTIVATION TOGGLE:
echo   To toggle or completely disable Cadence, set EnablePlugin = true/false
echo   ^(or PluginActive = true/false^) under [General] in Cadence.ini.
echo.
echo AUDIO ASSETS:
echo   plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav provides authentic dispatch
echo   distress tones during Panic Button activation.
echo.
echo COMPATIBILITY:
echo   Fully compatible with Ultimate Backup, StopThePed, and custom callout packs.
) > "%STAGE%\README.txt"

:: Remove old zip before packaging
if exist "%DIST%\Cadence_v1.1.0.zip" del /f /q "%DIST%\Cadence_v1.1.0.zip" >nul 2>nul

:: Create clean release archive
where tar >nul 2>nul
if %errorlevel%==0 (
    tar -a -c -f "%DIST%\Cadence_v1.1.0.zip" -C "%STAGE%" plugins README.txt
    echo   Created release archive -^> dist\Cadence_v1.1.0.zip
) else (
    powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%DIST%\Cadence_v1.1.0.zip' -Force"
    echo   Created release archive via PowerShell -^> dist\Cadence_v1.1.0.zip
)

:: Clean up all temporary staging folders and loose files in dist
if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
if exist "%DIST%\plugins" rmdir /s /q "%DIST%\plugins" >nul 2>nul
if exist "%DIST%\README.txt" del /f /q "%DIST%\README.txt" >nul 2>nul

echo.
echo ============================================================
echo   [BUILD SUCCESS] Release archive ready: dist\Cadence_v1.1.0.zip
echo ============================================================
echo.
if not "%1"=="--nopause" pause
exit /b 0
