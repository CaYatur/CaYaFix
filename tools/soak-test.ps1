# Copyright (c) 2026 CaYaDev (https://cayadev.com)
# GitHub: CaYatur (https://github.com/CaYatur)
# Licensed under the MIT License. See LICENSE in the project root.

[CmdletBinding()]
param(
    [ValidateRange(1, 1000)]
    [int]$Iterations = 50,
    [switch]$NoBuild,
    [ValidateRange(128, 8192)]
    [int]$MaxWorkingSetMB = 2048,
    [ValidateRange(1000, 100000)]
    [int]$MaxHandleCount = 20000
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root "CaYaFix.Tests\CaYaFix.Tests.csproj"
$results = Join-Path $root "TestResults\Soak"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$resourceReport = Join-Path $results "resource-usage.csv"

function Get-ProcessTreeSnapshot([int]$RootProcessId) {
    try {
        $rows = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction Stop)
        $ids = [System.Collections.Generic.HashSet[int]]::new()
        [void]$ids.Add($RootProcessId)
        do {
            $added = $false
            foreach ($row in $rows) {
                $processId = [int]$row.ProcessId
                $parentId = [int]$row.ParentProcessId
                if ($ids.Contains($parentId) -and $ids.Add($processId)) { $added = $true }
            }
        } while ($added)

        $processes = foreach ($processId in $ids) {
            Get-Process -Id $processId -ErrorAction SilentlyContinue
        }
        return @($processes)
    }
    catch {
        return @(Get-Process -Id $RootProcessId -ErrorAction SilentlyContinue)
    }
}

if (-not $NoBuild) {
    & dotnet build $project -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "The soak-test build failed." }
}

$started = Get-Date
$resourceRecords = [System.Collections.Generic.List[object]]::new()
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $name = "soak-{0:D4}.trx" -f $iteration
    Write-Progress -Activity "CaYaFix process-isolated soak test" -Status "$iteration / $Iterations" -PercentComplete (($iteration / $Iterations) * 100)
    $arguments = @(
        "test",
        "`"$project`"",
        "-c", "Release",
        "--no-build",
        "--logger", "trx;LogFileName=$name",
        "--results-directory", "`"$results`"",
        "--blame-hang-timeout", "5m"
    )
    $iterationStarted = Get-Date
    $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $root -NoNewWindow -PassThru
    [long]$peakWorkingSet = 0
    [int]$peakHandles = 0
    $observedProcessIds = [System.Collections.Generic.HashSet[int]]::new()
    do {
        $snapshot = @(Get-ProcessTreeSnapshot $process.Id)
        [long]$workingSet = 0
        [int]$handles = 0
        foreach ($item in $snapshot) {
            try {
                [void]$observedProcessIds.Add($item.Id)
                $workingSet += [long]$item.WorkingSet64
                $handles += [int]$item.HandleCount
            }
            catch {
                # A short-lived child can exit between the CIM and process snapshots.
            }
        }
        if ($workingSet -gt $peakWorkingSet) { $peakWorkingSet = $workingSet }
        if ($handles -gt $peakHandles) { $peakHandles = $handles }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while (-not $process.HasExited)
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()

    $duration = (Get-Date) - $iterationStarted
    $record = [pscustomobject]@{
        Iteration = $iteration
        DurationSeconds = [Math]::Round($duration.TotalSeconds, 3)
        PeakWorkingSetMB = [Math]::Round($peakWorkingSet / 1MB, 2)
        PeakHandleCount = $peakHandles
        ExitCode = $exitCode
    }
    $resourceRecords.Add($record)
    $resourceRecords | Export-Csv -LiteralPath $resourceReport -NoTypeInformation -Encoding UTF8

    if ($exitCode -ne 0) {
        throw "Soak iteration $iteration failed. Results: $results"
    }
    if ($record.PeakWorkingSetMB -gt $MaxWorkingSetMB) {
        throw "Soak iteration $iteration exceeded the $MaxWorkingSetMB MB process-tree memory ceiling."
    }
    if ($record.PeakHandleCount -gt $MaxHandleCount) {
        throw "Soak iteration $iteration exceeded the $MaxHandleCount process-tree handle ceiling."
    }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        $lingering = @($observedProcessIds | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } |
            Where-Object { $_.ProcessName -match '^(testhost|vstest\.console)$' })
        if ($lingering.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if ($lingering.Count -gt 0) {
        throw "Soak iteration $iteration left a test host process running."
    }
}
Write-Progress -Activity "CaYaFix process-isolated soak test" -Completed
$duration = (Get-Date) - $started
if ($resourceRecords.Count -ge 10) {
    [int]$sampleCount = [Math]::Max(2, [Math]::Ceiling($resourceRecords.Count * 0.2))
    $first = @($resourceRecords | Select-Object -First $sampleCount)
    $last = @($resourceRecords | Select-Object -Last $sampleCount)
    $firstMemory = ($first | Measure-Object PeakWorkingSetMB -Average).Average
    $lastMemory = ($last | Measure-Object PeakWorkingSetMB -Average).Average
    $firstHandles = ($first | Measure-Object PeakHandleCount -Average).Average
    $lastHandles = ($last | Measure-Object PeakHandleCount -Average).Average
    if (($lastMemory - $firstMemory) -gt 128 -and $lastMemory -gt ($firstMemory * 1.30)) {
        throw "The final soak sample shows sustained process-tree memory growth. Review $resourceReport."
    }
    if (($lastHandles - $firstHandles) -gt 1000 -and $lastHandles -gt ($firstHandles * 1.30)) {
        throw "The final soak sample shows sustained process-tree handle growth. Review $resourceReport."
    }
}
Write-Host "Completed $Iterations clean iterations in $($duration.ToString()). Results and resource report: $results" -ForegroundColor Green
