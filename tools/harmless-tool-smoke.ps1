# Copyright (c) 2026 CaYaDev (https://cayadev.com)
# GitHub: CaYatur (https://github.com/CaYatur)
# Licensed under the MIT License. See LICENSE in the project root.
#
# Harmless smoke test: read-only OS probes only.
# Does NOT apply network resets, DISM RestoreHealth, SFC, bcdboot rebuild, etc.
# Pair with: dotnet run --project tools/HarmlessDryRun

[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"
$outDir = Join-Path $env:TEMP ("CaYaFix-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$report = Join-Path $outDir "smoke-report.txt"
$results = New-Object System.Collections.Generic.List[string]

function Add-Result([string]$Status, [string]$Name, [string]$Detail = "") {
    $line = "[$Status] $Name$(if ($Detail) { ' - ' + $Detail })"
    $results.Add($line)
    $color = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "SKIP" { "Yellow" }
        default { "Cyan" }
    }
    Write-Host $line -ForegroundColor $color
}

function Invoke-ReadOnly([string]$Name, [scriptblock]$Block, [switch]$NativeCommand) {
    try {
        $global:LASTEXITCODE = 0
        $output = & $Block 2>&1 | Out-String
        $code = $global:LASTEXITCODE
        $trimmed = ($output | Out-String).Trim()
        if ($trimmed.Length -gt 350) { $trimmed = $trimmed.Substring(0, 350) + "..." }
        $flat = $trimmed -replace "\r?\n", " | "
        # Native PowerShell cmdlets often leave stale LASTEXITCODE; only trust exe exit codes.
        if ($NativeCommand -and $null -ne $code -and $code -ne 0) {
            if ($flat -match 'Eri.im engellendi|Access is denied|elevated|Y.kseltilmi.|740|5$|Parametre hatal') {
                Add-Result "INFO" $Name ("needs-admin exit=$code $flat")
            } else {
                Add-Result "FAIL" $Name ("exit=$code $flat")
            }
        } else {
            Add-Result "PASS" $Name $flat
        }
    } catch {
        Add-Result "FAIL" $Name $_.Exception.Message
    }
}

Write-Host "=== CaYaFix harmless OS smoke test ===" -ForegroundColor Cyan
Write-Host "Report dir: $outDir"

# Network / IP (read-only)
Invoke-ReadOnly "ipconfig /all" { ipconfig.exe /all } -NativeCommand
Invoke-ReadOnly "ipconfig /displaydns" { ipconfig.exe /displaydns 2>&1 | Select-Object -First 8 } -NativeCommand
Invoke-ReadOnly "netsh winsock show catalog" { netsh.exe winsock show catalog 2>&1 | Select-Object -First 10 } -NativeCommand
Invoke-ReadOnly "netsh interface ipv4 show interfaces" { netsh.exe interface ipv4 show interfaces } -NativeCommand
Invoke-ReadOnly "arp -a" { arp.exe -a 2>&1 | Select-Object -First 8 } -NativeCommand
Invoke-ReadOnly "route print" { route.exe print 2>&1 | Select-Object -First 15 } -NativeCommand

# Services (read-only)
Invoke-ReadOnly "sc query Dnscache" { sc.exe query Dnscache } -NativeCommand
Invoke-ReadOnly "sc query Dhcp" { sc.exe query Dhcp } -NativeCommand
Invoke-ReadOnly "sc query Audiosrv" { sc.exe query Audiosrv } -NativeCommand
Invoke-ReadOnly "sc query AudioEndpointBuilder" { sc.exe query AudioEndpointBuilder } -NativeCommand
Invoke-ReadOnly "sc query WSearch" { sc.exe query WSearch } -NativeCommand
Invoke-ReadOnly "sc query Spooler" { sc.exe query Spooler } -NativeCommand

# Boot / recovery (read-only)
Invoke-ReadOnly "bcdedit /enum {current}" { bcdedit.exe /enum '{current}' } -NativeCommand
Invoke-ReadOnly "bcdedit /enum firmware" { bcdedit.exe /enum firmware 2>&1 | Select-Object -First 15 } -NativeCommand
Invoke-ReadOnly "reagentc /info" { reagentc.exe /info 2>&1 } -NativeCommand

# DISM CheckHealth is read-only (no repair) but often needs admin
Invoke-ReadOnly "dism CheckHealth" { dism.exe /Online /Cleanup-Image /CheckHealth /English 2>&1 } -NativeCommand

# Devices (read-only)
Invoke-ReadOnly "pnputil Display class" { pnputil.exe /enum-devices /class Display 2>&1 | Select-Object -First 25 } -NativeCommand
Invoke-ReadOnly "pnputil Net class" { pnputil.exe /enum-devices /class Net 2>&1 | Select-Object -First 15 } -NativeCommand

# PowerShell inventory (read-only) — do not treat LASTEXITCODE
Invoke-ReadOnly "Get-NetAdapter" {
    Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object Name, Status, LinkSpeed | Format-Table | Out-String
}
Invoke-ReadOnly "Get-PnpDevice Display" {
    Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue |
        Select-Object Status, FriendlyName | Format-Table | Out-String
}
Invoke-ReadOnly "Get-Service audio/net" {
    Get-Service Dnscache, Dhcp, Audiosrv, AudioEndpointBuilder -ErrorAction SilentlyContinue |
        Select-Object Name, Status, StartType | Format-Table | Out-String
}
Invoke-ReadOnly "Get-ComputerRestorePoint" {
    Get-ComputerRestorePoint -ErrorAction SilentlyContinue |
        Select-Object -First 3 Description, CreationTime | Format-List | Out-String
}

# Explicitly not run
$skipped = @(
    "ipconfig /release or /renew",
    "netsh winsock reset / int ip reset / full network reset",
    "SFC /scannow",
    "DISM RestoreHealth / ScanHealth / StartComponentCleanup",
    "Win+Ctrl+Shift+B graphics soft-reset",
    "display.restart-all / pnputil restart-device",
    "bcdboot rebuild / reagentc /enable",
    "chkdsk /spotfix or scheduled offline chkdsk",
    "audio mmdevices-reset / driver-reset",
    "firewall reset / driver uninstall"
)
foreach ($s in $skipped) {
    Add-Result "SKIP" $s "Not applied - mutates system, long-running, or needs reboot"
}

$pass = @($results | Where-Object { $_.StartsWith("[PASS]") }).Count
$fail = @($results | Where-Object { $_.StartsWith("[FAIL]") }).Count
$skip = @($results | Where-Object { $_.StartsWith("[SKIP]") }).Count
$info = @($results | Where-Object { $_.StartsWith("[INFO]") }).Count
$summary = "SUMMARY PASS=$pass FAIL=$fail SKIP=$skip INFO=$info Report=$report"
$results.Add($summary)
$results | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host $summary -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Yellow" })
Write-Host "Full report: $report"
if ($fail -gt 0) { exit 2 }
exit 0
