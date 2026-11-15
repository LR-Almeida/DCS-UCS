@echo off
setlocal
REM ===========================================================================
REM  DCS-UCS build script
REM  Produces single-file, self-contained Windows x64 executables in .\publish
REM  Requires the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
REM  Optional: Inno Setup 6 (ISCC.exe) to build the installer as well.
REM ===========================================================================
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet not found. Install the .NET 8 SDK first.
    exit /b 1
)

if exist publish rmdir /s /q publish
mkdir publish

set PUBLISH_ARGS=-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish

for %%P in (UCS.Server UCS.Client UCS.ProfileGen) do (
    echo.
    echo === Publishing %%P ===
    dotnet publish "src\%%P\%%P.csproj" %PUBLISH_ARGS%
    if errorlevel 1 (
        echo [ERROR] Failed to publish %%P
        exit /b 1
    )
)

REM Portable package layout next to the executables
xcopy /y /i /q "profiles\*.json" "publish\profiles\" >nul
xcopy /y /i /q "src\Lua\UCS-Hook.lua" "publish\Lua\" >nul
xcopy /y /i /q "src\Lua\UCS-Export.lua" "publish\Lua\" >nul
xcopy /y /i /q "src\Lua\UCS-ServerHook.lua" "publish\Server-Files\" >nul
copy /y "Server-Files\UCS-AutoConnect.cfg" "publish\Server-Files\UCS-AutoConnect.cfg" >nul
del /q "publish\*.pdb" 2>nul

echo.
echo === Executables ===
dir /b publish\*.exe

REM Installer (optional)
set ISCC=
for %%I in (ISCC.exe) do set ISCC=%%~$PATH:I
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if defined ISCC (
    echo.
    echo === Building installer ===
    "%ISCC%" /Q "installer\UCS-Installer.iss"
    if errorlevel 1 (
        echo [ERROR] Installer build failed
        exit /b 1
    )
    echo Installer: installer\Output\
) else (
    echo.
    echo [INFO] Inno Setup 6 not found - skipping installer. Get it at https://jrsoftware.org/isinfo.php
)

echo.
echo Build finished. Output: %~dp0publish
endlocal
