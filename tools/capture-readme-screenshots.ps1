# Copyright (c) 2026 CaYaDev (https://cayadev.com)
# GitHub: CaYatur (https://github.com/CaYatur)
# Licensed under the MIT License. See LICENSE in the project root.

[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $root "docs\screenshots"))
$captureDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "CaYaFix\ReadmeScreenshots"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        throw 'The screenshot runner is not elevated; refusing to wait for an unattended UAC prompt.'
    }
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $MyInvocation.MyCommand.Path)
    )
    if ($NoBuild) { $arguments += '-NoBuild' }
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$expected = @("dashboard.png", "findings.png", "live-tests.png")
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$captureStartedUtc = [DateTime]::UtcNow

$runArguments = @(
    'run',
    '--project', (Join-Path $root 'CaYaFix.App\CaYaFix.App.csproj'),
    '-c', 'Release'
)
if ($NoBuild) { $runArguments += '--no-build' }
$runArguments += @('--', '--capture-readme')

& dotnet @runArguments
if ($LASTEXITCODE -ne 0) { throw "The WPF screenshot process failed with exit code $LASTEXITCODE." }

foreach ($name in $expected) {
    $source = Join-Path $captureDirectory $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Screenshot was not created: $source"
    }
    if ((Get-Item -LiteralPath $source).LastWriteTimeUtc -lt $captureStartedUtc) {
        throw "Screenshot was not refreshed by this run: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $outputDirectory $name) -Force
}

Write-Host "README screenshots were captured from the running WPF application." -ForegroundColor Green
