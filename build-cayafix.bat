@echo off
rem Copyright (c) 2026 CaYaDev (https://cayadev.com)
rem GitHub: CaYatur (https://github.com/CaYatur)
rem Licensed under the MIT License. See LICENSE in the project root.

setlocal
set "ROOT=%~dp0"
set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "FINDSTR=%SystemRoot%\System32\findstr.exe"

if not exist "%ROOT%build.ps1" (
    echo ERROR: build.ps1 was not found next to this BAT file.
    echo Copy build-cayafix.bat into the CaYaFix source folder and run it again.
    pause
    exit /b 2
)

if not exist "%POWERSHELL%" (
    echo ERROR: Windows PowerShell was not found.
    pause
    exit /b 3
)

call :ensure_dotnet8
if errorlevel 1 (
    pause
    exit /b 4
)

echo Building CaYaFix with a process-only execution policy bypass...
"%POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1" %*
set "RESULT=%ERRORLEVEL%"

if not "%RESULT%"=="0" (
    echo.
    echo CaYaFix build failed with exit code %RESULT%.
    pause
    exit /b %RESULT%
)

echo.
echo CaYaFix build completed successfully.
pause
exit /b 0

:ensure_dotnet8
where dotnet.exe >nul 2>nul
if not errorlevel 1 (
    dotnet.exe --list-sdks 2>nul | "%FINDSTR%" /R /B "8\." >nul
    if not errorlevel 1 exit /b 0
)

echo .NET 8 SDK was not found. Installing the official Microsoft package...
where winget.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows Package Manager was not found.
    echo Install App Installer from Microsoft Store, then run this BAT again.
    exit /b 1
)

winget.exe install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-source-agreements --accept-package-agreements --silent
if errorlevel 1 (
    echo ERROR: .NET 8 SDK installation failed.
    exit /b 1
)

set "PATH=%ProgramFiles%\dotnet;%PATH%"
where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet.exe is still unavailable after installation.
    exit /b 1
)

dotnet.exe --list-sdks 2>nul | "%FINDSTR%" /R /B "8\." >nul
if errorlevel 1 (
    echo ERROR: .NET 8 SDK could not be verified after installation.
    exit /b 1
)

echo .NET 8 SDK installation completed.
exit /b 0
