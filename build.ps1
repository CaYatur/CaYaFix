# Copyright (c) 2026 CaYaDev (https://cayadev.com)
# GitHub: CaYatur (https://github.com/CaYatur)
# Licensed under the MIT License. See LICENSE in the project root.

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipValidation,
    [ValidateRange(0, 1000)]
    [int]$SoakIterations = 0,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "CaYaFix.sln"
$publish = Join-Path $root "publish\win-x64"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0."
}

if (-not $SkipValidation) {
    & (Join-Path $root "tools\validate-repository.ps1")
    if (-not $?) { throw "Repository validation failed." }
}

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

dotnet build $solution -c $Configuration --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

if (-not $SkipTests) {
    dotnet test (Join-Path $root "CaYaFix.Tests\CaYaFix.Tests.csproj") -c $Configuration --no-build --blame-hang-timeout 5m
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}

if ($SoakIterations -gt 0) {
    & (Join-Path $root "tools\soak-test.ps1") -Iterations $SoakIterations -NoBuild
    if (-not $?) { throw "The soak test failed." }
}

dotnet publish (Join-Path $root "CaYaFix.App\CaYaFix.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -warnaserror `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host ""
Write-Host "CaYaFix is ready: $publish\CaYaFix.exe" -ForegroundColor Green
