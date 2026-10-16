@echo off
setlocal EnableExtensions enabledelayedexpansion
title Build Cadence - Release

:: ============================================================
:: Cadence - LSPDFR Ambient Policing System
:: Fast Automated Build, Validation & Packaging Script
:: ============================================================

set "VERSION=1.2.0"
set "ZIP_NAME=Cadence_v%VERSION%.zip"

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..") do set "ROOT=%%~fI"

set "SRC=%ROOT%\src"
set "DIST=%ROOT%\dist"
set "STAGE=%DIST%\_staging"
set "SLN=%SRC%\Cadence.sln"
set "PROJ=%SRC%\Cadence.csproj"

echo ============================================================
echo   Cadence v%VERSION% - Automated Build ^& Packaging
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
    if not "%1"=="--nopause" pause
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
    if not "%1"=="--nopause" pause
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
    if not "%1"=="--nopause" pause
    exit /b %errorlevel%
)

:: Run Native & Architectural Integrity Validator [F4, F26]
echo.
echo [VALIDATE] Running Native ^& Architectural Integrity Validator...
python "%ROOT%\scripts\validate_natives.py"
if errorlevel 1 (
    echo.
    echo ============================================================
    echo   [VALIDATION ERROR] Native validator detected violations.
    echo ============================================================
    echo.
    if not "%1"=="--nopause" pause
    exit /b 1
)

:: Run Automated Test Suite
if exist "%ROOT%\tests\Cadence.Tests\bin\Release\Cadence.Tests.exe" (
    echo.
    echo [TEST] Executing Automated Test Suite...
    "%ROOT%\tests\Cadence.Tests\bin\Release\Cadence.Tests.exe"
    if errorlevel 1 (
        echo.
        echo ============================================================
        echo   [TEST ERROR] Automated test suite reported failures.
        echo ============================================================
        echo.
        if not "%1"=="--nopause" pause
        exit /b 1
    )
)

:: Locate built Cadence.dll [F3]
set "BUILT_DLL="
if exist "%SRC%\bin\Release\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\Cadence.dll"
if exist "%SRC%\bin\Release\net48\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\net48\Cadence.dll"
if exist "%ROOT%\bin\Release\Cadence.dll" set "BUILT_DLL=%ROOT%\bin\Release\Cadence.dll"

if not defined BUILT_DLL (
    echo.
    echo ============================================================
    echo   [ERROR] Missing Cadence.dll! Compilation produced no binary.
    echo ============================================================
    echo.
    if not "%1"=="--nopause" pause
    exit /b 1
)

:: Package output into clean zip archive using temporary staging
echo.
echo [PACKAGE] Assembling distribution release into %ZIP_NAME%...

if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%\plugins\LSPDFR" >nul 2>nul
mkdir "%STAGE%\plugins\LSPDFR\Cadence\Logs" >nul 2>nul
type nul > "%STAGE%\plugins\LSPDFR\Cadence\Logs\.placeholder" 2>nul

copy /y "%BUILT_DLL%" "%STAGE%\plugins\LSPDFR\Cadence.dll" >nul
echo   Staged Cadence.dll

if exist "%SRC%\Cadence.ini" (
    copy /y "%SRC%\Cadence.ini" "%STAGE%\plugins\LSPDFR\Cadence.ini" >nul
    echo   Staged Cadence.ini
) else (
    echo [ERROR] Missing src\Cadence.ini template!
    if not "%1"=="--nopause" pause
    exit /b 1
)
echo   Staged plugins\LSPDFR\Cadence\Logs\

if exist "%SRC%\Audio\PANIC_BUTTON.wav" (
    mkdir "%STAGE%\plugins\LSPDFR\Cadence\Audio" >nul 2>nul
    copy /y "%SRC%\Audio\PANIC_BUTTON.wav" "%STAGE%\plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav" >nul
    echo   Staged plugins\LSPDFR\Cadence\Audio\PANIC_BUTTON.wav
) else (
    echo [ERROR] Missing src\Audio\PANIC_BUTTON.wav asset!
    if not "%1"=="--nopause" pause
    exit /b 1
)

:: Create README.txt in staging [F20]
(
echo ============================================================
echo   Cadence v%VERSION% - Ambient Policing System for LSPDFR
echo ============================================================
echo.
echo REQUIREMENTS ^& DEPENDENCIES:
echo   - Grand Theft Auto V ^(v1.0.3095.0 or later recommended^)
echo   - RagePluginHook ^(v1.08 or later^)
echo   - LSPD First Response ^(v0.4.9 or later^)
echo   - RAGENativeUI ^(v1.9.2 or later^)
echo   - Microsoft .NET Framework 4.8 Runtime
echo   - [RECOMMENDED] Ultimate Backup ^(v1.8.7.0+^)
echo     * Note: If Ultimate Backup is not installed, set Fallback = true
echo       under [Integration] in Cadence.ini to enable native ambient spawning.
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
echo   All keybinds are fully configurable under [Keybinds] in plugins\LSPDFR\Cadence.ini.
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
if exist "%DIST%\%ZIP_NAME%" del /f /q "%DIST%\%ZIP_NAME%" >nul 2>nul

:: Create clean release archive
where tar >nul 2>nul
if %errorlevel%==0 (
    tar -a -c -f "%DIST%\%ZIP_NAME%" -C "%STAGE%" plugins README.txt
    echo   Created release archive -^> dist\%ZIP_NAME%
) else (
    powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%DIST%\%ZIP_NAME%' -Force"
    echo   Created release archive via PowerShell -^> dist\%ZIP_NAME%
)

:: Clean up staging
if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
if exist "%DIST%\plugins" rmdir /s /q "%DIST%\plugins" >nul 2>nul
if exist "%DIST%\README.txt" del /f /q "%DIST%\README.txt" >nul 2>nul

:: Assert archive actually contains required assets [F3]
echo.
echo [VERIFY] Asserting release archive contents...
set "VERIFY_FAILED="
where tar >nul 2>nul
if %errorlevel%==0 (
    tar -tf "%DIST%\%ZIP_NAME%" | findstr /i "plugins/LSPDFR/Cadence.dll" >nul || set "VERIFY_FAILED=1"
    tar -tf "%DIST%\%ZIP_NAME%" | findstr /i "plugins/LSPDFR/Cadence.ini" >nul || set "VERIFY_FAILED=1"
    tar -tf "%DIST%\%ZIP_NAME%" | findstr /i "PANIC_BUTTON.wav" >nul || set "VERIFY_FAILED=1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip = [System.IO.Compression.ZipFile]::OpenRead('%DIST%\%ZIP_NAME%'); $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }); $zip.Dispose(); if (-not ($entries -match 'plugins/LSPDFR/Cadence\.dll' -and $entries -match 'plugins/LSPDFR/Cadence\.ini' -and $entries -match 'PANIC_BUTTON\.wav')) { exit 1 }"
    if errorlevel 1 set "VERIFY_FAILED=1"
)

if defined VERIFY_FAILED (
    echo [ERROR] Archive integrity check failed! Archive is missing Cadence.dll, Cadence.ini, or PANIC_BUTTON.wav.
    if not "%1"=="--nopause" pause
    exit /b 1
)
echo   Archive integrity verified: Cadence.dll, Cadence.ini, and PANIC_BUTTON.wav all present.

echo.
echo ============================================================
echo   [BUILD SUCCESS] Release archive ready: dist\%ZIP_NAME%
echo ============================================================
echo.
if not "%1"=="--nopause" pause
exit /b 0
