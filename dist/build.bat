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
set "DIST_LSPDFR=%DIST%\plugins\LSPDFR"
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
    exit /b 1
)

:: Choose build target (Solution or Project)
set "BUILD_TARGET="
if exist "%SLN%" (
    set "BUILD_TARGET=%SLN%"
) else if exist "%PROJ%" (
    set "BUILD_TARGET=%PROJ%"
) else (
    echo [ERROR] Neither Cadence.sln nor src\Cadence.csproj was found.
    exit /b 1
)

echo [BUILD] Compiling Cadence in Release configuration (x64)...
echo Target: "%BUILD_TARGET%"
echo Compiler: "%MSBUILD_CMD%"
echo.

"%MSBUILD_CMD%" "%BUILD_TARGET%" /p:Configuration=Release /p:Platform=x64 /nologo /v:m
if errorlevel 1 (
    echo.
    echo ============================================================
    echo   [BUILD ERROR] Compilation failed with error code %errorlevel%.
    echo ============================================================
    exit /b %errorlevel%
)

:: Package output into dist/
echo.
echo [PACKAGE] Assembling distribution release in dist\...
if not exist "%DIST_LSPDFR%" mkdir "%DIST_LSPDFR%" >nul 2>nul

set "BUILT_DLL="
if exist "%SRC%\bin\Release\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\Cadence.dll"
if exist "%SRC%\bin\Release\net48\Cadence.dll" set "BUILT_DLL=%SRC%\bin\Release\net48\Cadence.dll"
if exist "%ROOT%\bin\Release\Cadence.dll" set "BUILT_DLL=%ROOT%\bin\Release\Cadence.dll"

if defined BUILT_DLL (
    copy /y "%BUILT_DLL%" "%DIST_LSPDFR%\Cadence.dll" >nul
    echo   Copied Cadence.dll -^> dist\plugins\LSPDFR\
) else (
    echo   [WARN] Could not find compiled Cadence.dll to copy to dist.
)

if exist "%SRC%\Cadence.ini" (
    copy /y "%SRC%\Cadence.ini" "%DIST_LSPDFR%\Cadence.ini" >nul
    echo   Copied Cadence.ini -^> dist\plugins\LSPDFR\
)

:: Create Release Zip Archive
where tar >nul 2>nul
if %errorlevel%==0 (
    if exist "%DIST%\README.txt" (
        tar -a -c -f "%DIST%\Cadence_v1.0.0.zip" -C "%DIST%" plugins README.txt
        echo   Created release archive -^> dist\Cadence_v1.0.0.zip
    )
)

echo.
echo ============================================================
echo   [BUILD SUCCESS] Cadence compiled ^& packaged successfully!
echo ============================================================
exit /b 0
