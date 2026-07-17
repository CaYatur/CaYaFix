# Copyright (c) 2026 CaYaDev (https://cayadev.com)
# GitHub: CaYatur (https://github.com/CaYatur)
# Licensed under the MIT License. See LICENSE in the project root.

[CmdletBinding()]
param(
    [switch]$RequireScreenshots
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Get-ResourceMap([string]$Path) {
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $map = @{}
    foreach ($node in $document.root.data) {
        $key = [string]$node.name
        if ($map.ContainsKey($key)) { Add-Failure "Duplicate resource key '$key' in $Path." }
        $map[$key] = [string]$node.value
    }
    return $map
}

$englishPath = Join-Path $root "CaYaFix.App\Properties\Strings.resx"
$turkishPath = Join-Path $root "CaYaFix.App\Properties\Strings.tr.resx"
$english = Get-ResourceMap $englishPath
$turkish = Get-ResourceMap $turkishPath
foreach ($key in $english.Keys) {
    if (-not $turkish.ContainsKey($key)) { Add-Failure "Turkish resource is missing '$key'." }
    if ([string]::IsNullOrWhiteSpace($english[$key])) { Add-Failure "English resource '$key' is empty." }
}
foreach ($key in $turkish.Keys) {
    if (-not $english.ContainsKey($key)) { Add-Failure "English resource is missing '$key'." }
    if ([string]::IsNullOrWhiteSpace($turkish[$key])) { Add-Failure "Turkish resource '$key' is empty." }
}

$formatTokenPattern = '\{(?<Index>\d+)(?:[^}]*)\}'
foreach ($key in $english.Keys) {
    if (-not $turkish.ContainsKey($key)) { continue }
    $englishTokens = @([regex]::Matches($english[$key], $formatTokenPattern) |
        ForEach-Object { $_.Groups['Index'].Value } | Sort-Object -Unique)
    $turkishTokens = @([regex]::Matches($turkish[$key], $formatTokenPattern) |
        ForEach-Object { $_.Groups['Index'].Value } | Sort-Object -Unique)
    if (Compare-Object $englishTokens $turkishTokens -SyncWindow 0) {
        Add-Failure "Format placeholders differ between English and Turkish for '$key'."
    }
}

$sourceFiles = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|publish|TestResults|upload)[\\/]' -and
    $_.Extension -in '.cs', '.xaml', '.ps1'
}
$policyFiles = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|publish|TestResults|upload|\.git)[\\/]' -and
    ($_.Name -eq '.gitignore' -or $_.Extension -in '.cs', '.xaml', '.ps1', '.yml', '.yaml', '.csproj', '.resx', '.manifest', '.svg', '.props', '.md')
}
$allSource = ($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$resourcePrefixes = 'Action|AdminMode|AppName|AppTagline|Check|Console|Dashboard|Dialog|Escalation|Expert|Finding|Fix|Force|Handoff|Hero|LiveMetrics|LiveTest|LiveTests|Module|Nav|Recovery|Report|SessionStatus|Settings|Severity|Status|Symptom|TestResult|TestStage|Tier|UpdateError'
$matches = [regex]::Matches($allSource, '"((' + $resourcePrefixes + ')_[A-Za-z0-9_]+|AdminMode|AppName|AppTagline)"')
foreach ($match in $matches) {
    $key = $match.Groups[1].Value
    if (-not $english.ContainsKey($key)) { Add-Failure "Source references missing resource '$key'." }
}

$xmlFiles = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|publish|TestResults|upload)[\\/]' -and
    $_.Extension -in '.xaml', '.csproj', '.resx', '.manifest', '.svg', '.props'
}
foreach ($file in $xmlFiles) {
    try { [xml](Get-Content -LiteralPath $file.FullName -Raw) | Out-Null }
    catch { Add-Failure "Invalid XML in $($file.FullName): $($_.Exception.Message)" }
}

$xamlFiles = $xmlFiles | Where-Object { $_.Extension -eq '.xaml' }
$designer = Get-Content -LiteralPath (Join-Path $root "CaYaFix.App\Properties\Strings.Designer.cs") -Raw
$allXaml = ($xamlFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$definedXamlResources = @([regex]::Matches($allXaml, 'x:Key\s*=\s*["'']([^"'']+)["'']') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
foreach ($file in $xamlFiles) {
    $xaml = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($xaml, 'p:Strings\.([A-Za-z0-9_]+)')) {
        $key = $match.Groups[1].Value
        if ($designer -notmatch ('public static string ' + [regex]::Escape($key) + '\b')) {
            Add-Failure "Strings.Designer.cs is missing the XAML property '$key'."
        }
    }

    foreach ($match in [regex]::Matches($xaml, '\{StaticResource\s+([^},\s]+)')) {
        $key = $match.Groups[1].Value
        if ($key -notin $definedXamlResources) {
            Add-Failure "XAML references the undefined StaticResource '$key' in $($file.FullName)."
        }
    }
}

$svgFiles = Get-ChildItem -Path (Join-Path $root "CaYaFix.App\Assets\Icons") -Filter *.svg
foreach ($file in $svgFiles) {
    $svg = Get-Content -LiteralPath $file.FullName -Raw
    try { [xml]$svg | Out-Null } catch { Add-Failure "Invalid SVG '$($file.Name)'." }
    if ($svg -match '<script|<foreignObject|\bon\w+\s*=|(?:href|src)\s*=\s*["'']https?:') {
        Add-Failure "SVG '$($file.Name)' contains active or remote content."
    }
}

foreach ($file in $policyFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($content -match '[\u2600-\u27BF\uFE0F]' -or $content -match '[\uD800-\uDBFF][\uDC00-\uDFFF]') {
        Add-Failure "Emoji characters are not allowed; use an SVG icon instead: $($file.FullName)."
    }
}

$processStartFiles = $sourceFiles | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -cmatch 'Process\.Start\s*\(' }
foreach ($file in $processStartFiles) {
    if ($file.FullName -notlike '*CaYaFix.App\ViewModels\MainViewModel.cs') {
        Add-Failure "Direct Process.Start is forbidden outside the reviewed OpenPath helper: $($file.FullName)."
    }
}

$newProcessFiles = $sourceFiles | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'new\s+Process\s*[{(]' }
foreach ($file in $newProcessFiles) {
    if ($file.FullName -notlike '*CaYaFix.Core\Execution\CommandRunner.cs') {
        Add-Failure "Direct process construction is forbidden outside CommandRunner: $($file.FullName)."
    }
}

foreach ($file in $sourceFiles | Where-Object { $_.Extension -eq '.cs' }) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($content -match '(?i)\b(?:Invoke-Expression|IEX|DownloadString|DownloadFile)\b') {
        Add-Failure "Dynamic script or download execution is forbidden: $($file.FullName)."
    }
    if ($content -match 'AllowTier3WithoutBackup') {
        Add-Failure "A repair-without-backup escape hatch is forbidden: $($file.FullName)."
    }
}

$forbiddenPatterns = @(
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '\bgh[pousr]_[A-Za-z0-9]{30,}\b',
    '\bAKIA[0-9A-Z]{16}\b',
    '(?i)password\s*=\s*["''][^"'']+["'']',
    '(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*["''][^"'']{8,}["'']'
)
$scanFiles = Get-ChildItem -Path $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|publish|TestResults|upload|\.git)[\\/]' -and
    $_.Extension -notin '.png', '.ico', '.dll', '.exe', '.zip'
}
foreach ($file in $scanFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    foreach ($pattern in $forbiddenPatterns) {
        if ($content -match $pattern) { Add-Failure "Possible secret in $($file.FullName)."; break }
    }
}

$requiredFiles = @(
    'LICENSE',
    'README.md',
    'SECURITY.md',
    'CONTRIBUTING.md',
    'docs\ARCHITECTURE.md',
    'docs\SECURITY-MODEL.md',
    'docs\TEST-PLAN.md',
    'docs\VALIDATION-REPORT.md',
    '.github\workflows\ci.yml',
    '.github\workflows\codeql.yml',
    '.github\workflows\screenshots.yml',
    '.github\workflows\soak.yml',
    '.github\workflows\release.yml'
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) { Add-Failure "Required file is missing: $relative" }
}

foreach ($file in $policyFiles) {
    $head = ((Get-Content -LiteralPath $file.FullName -TotalCount 8) -join "`n")
    if ($head -notmatch 'Copyright \(c\) 2026 CaYaDev \(https://cayadev\.com\)' -or
        $head -notmatch 'GitHub: CaYatur \(https://github\.com/CaYatur\)' -or
        $head -notmatch 'Licensed under the MIT License') {
        Add-Failure "The required CaYaDev/CaYatur MIT header is missing from $($file.FullName)."
    }
}

$licenseText = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
foreach ($requiredNotice in 'MIT License', 'Copyright (c) 2026 CaYaDev (https://cayadev.com)', 'GitHub: CaYatur (https://github.com/CaYatur)', 'Permission is hereby granted') {
    if ($licenseText.IndexOf($requiredNotice, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "LICENSE is missing the required notice: $requiredNotice"
    }
}

$jsonFiles = Get-ChildItem -Path $root -Recurse -Filter *.json -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|publish|TestResults|upload|\.git)[\\/]'
}
foreach ($file in $jsonFiles) {
    try { Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json | Out-Null }
    catch { Add-Failure "Invalid JSON in $($file.FullName): $($_.Exception.Message)" }
}

$moduleSource = (Get-ChildItem -Path (Join-Path $root 'CaYaFix.Modules') -Recurse -Filter *.cs -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checkMatches = [regex]::Matches($moduleSource, 'new\s+DelegateDiagnosticCheck\s*\(\s*"([^"]+)"')
$fixMatches = [regex]::Matches($moduleSource, '"([a-z0-9.-]+)"\s*,\s*"Fix_[A-Za-z0-9_]+"')
$liveMatches = [regex]::Matches($moduleSource, 'new\s+DelegateLiveTest\s*\(\s*"([^"]+)"')
$checkIds = @($checkMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$fixIds = @($fixMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$liveIds = @($liveMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($checkIds.Count -ne 90 -or $checkMatches.Count -ne $checkIds.Count) { Add-Failure "Expected 90 unique diagnostics; found $($checkIds.Count)." }
if ($fixIds.Count -ne 163 -or $fixMatches.Count -ne $fixIds.Count) { Add-Failure "Expected 163 unique repair actions; found $($fixIds.Count)." }
if ($liveIds.Count -ne 8 -or $liveMatches.Count -ne $liveIds.Count) { Add-Failure "Expected 8 unique live tests; found $($liveIds.Count)." }

$previewSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Modules\Shared\RepairPreviewCatalog.cs') -Raw
$previewIds = @([regex]::Matches($previewSource, '\["([a-z0-9.-]+)"\]\s*=') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($previewIds.Count -ne $fixIds.Count -or (Compare-Object $fixIds $previewIds -SyncWindow 0)) {
    Add-Failure "Every repair action must have exactly one dry-run preview plan."
}

$fixEngineSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Engine\FixEngine.cs') -Raw
$intentIndex = $fixEngineSource.IndexOf('var recoveryIntent = AddRecoveryIntent(context.Session, finding, fix, backup', [System.StringComparison]::Ordinal)
$intentSaveIndex = if ($intentIndex -ge 0) {
    $fixEngineSource.IndexOf('await _sessions.SaveAsync(context.Session, ct)', $intentIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$applyStageIndex = if ($intentSaveIndex -ge 0) {
    $fixEngineSource.IndexOf('"FixStage_Applying"', $intentSaveIndex, [System.StringComparison]::Ordinal)
} else { -1 }
if ($intentIndex -lt 0 -or $intentSaveIndex -lt 0 -or $applyStageIndex -lt 0 -or
    $fixEngineSource -notmatch 'FixResult_ApplyInProgress') {
    Add-Failure 'A signed write-ahead recovery intent must be persisted before every repair apply stage.'
}

foreach ($forbiddenModulePattern in 'integrity\.repair-chain', 'store\.reregister', 'netcfg(?:\.exe)?\s+-d', 'route\.exe"\s*,\s*\["-f"\]') {
    if ($moduleSource -match $forbiddenModulePattern) {
        Add-Failure "A non-transactional or overly broad repair pattern remains in module source: $forbiddenModulePattern"
    }
}

if ($moduleSource -match '&\s+pnputil\.exe\b') {
    Add-Failure 'PowerShell recovery scripts must invoke pnputil from the trusted System32 path.'
}

$catalogSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Modules\ModuleCatalog.cs') -Raw
$moduleMatches = [regex]::Matches($catalogSource, 'new\s+[A-Za-z][A-Za-z0-9]*Module\s*\(\s*\)')
if ($moduleMatches.Count -ne 19) { Add-Failure "Expected 19 module registrations; found $($moduleMatches.Count)." }
$catalogTests = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Tests\ModuleCatalogTests.cs') -Raw
foreach ($expectation in 'Assert.Equal(19, modules.Count)', 'Assert.Equal(90, modules.Sum(module => module.Checks.Count))', 'Assert.Equal(163, modules.Sum(module => module.Fixes.Count))', 'Assert.Equal(8, modules.Sum(module => module.LiveTests.Count))') {
    if ($catalogTests.IndexOf($expectation, [System.StringComparison]::Ordinal) -lt 0) { Add-Failure "Catalog regression test is missing: $expectation" }
}

$runnerSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Execution\CommandRunner.cs') -Raw
$truncationMarkerCount = [regex]::Matches($runnerSource, 'captureTruncated').Count
if ($truncationMarkerCount -lt 3 -or $runnerSource -notmatch 'captured-output safety limit') {
    Add-Failure 'Command output truncation must be explicit to both callers and the audit log.'
}
$allowlistBlock = [regex]::Match($runnerSource, 'AllowedExecutables\s*=.*?\{(?<Body>.*?)\};', [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $allowlistBlock.Success) {
    Add-Failure 'CommandRunner executable allowlist could not be validated.'
}
else {
    $allowedExecutables = @([regex]::Matches($allowlistBlock.Groups['Body'].Value, '"([a-z0-9.-]+\.exe)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $executionFiles = Get-ChildItem -Path (Join-Path $root 'CaYaFix.Core'), (Join-Path $root 'CaYaFix.Modules') -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notlike '*CaYaFix.Core\Execution\CommandRunner.cs' }
    $executionSource = ($executionFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $usedExecutables = @([regex]::Matches($executionSource, '"([a-z0-9.-]+\.exe)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    foreach ($executable in $usedExecutables) {
        if ($executable -notin $allowedExecutables) { Add-Failure "Executable '$executable' is used but not trusted by CommandRunner." }
    }
}

$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
foreach ($requiredReadmeText in '90 diagnostic checks', '163 repair actions', 'https://github.com/CaYatur', 'https://cayadev.com', 'docs/screenshots/dashboard.png', 'docs/screenshots/findings.png', 'docs/screenshots/live-tests.png') {
    if ($readme.IndexOf($requiredReadmeText, [System.StringComparison]::Ordinal) -lt 0) { Add-Failure "README is missing: $requiredReadmeText" }
}
if ($readme -match '(?i)manifest\.sig') { Add-Failure 'README still describes the obsolete detached manifest signature format.' }

$appSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.App\App.xaml.cs') -Raw
if ($appSource -notmatch 'Path\.Combine\(userDataRoot,\s*"ReadmeScreenshots"\)' -or
    $appSource -match 'captureIndex|e\.Args\s*\[\s*capture') {
    Add-Failure 'Privileged README capture must use only the fixed ACL-protected LocalAppData directory.'
}
$captureScriptSource = Get-Content -LiteralPath (Join-Path $root 'tools\capture-readme-screenshots.ps1') -Raw
if ($captureScriptSource -cmatch '\$OutputDirectory' -or
    $captureScriptSource -notmatch 'Join-Path\s+\$root\s+"docs\\screenshots"') {
    Add-Failure 'The elevated README capture script must use only the fixed repository screenshot directory.'
}

$mainViewModelSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.App\ViewModels\MainViewModel.cs') -Raw
foreach ($consoleBound in 'MaximumPendingConsoleLines = 2_000', 'ConsoleFlushBatchSize = 250', 'MaximumVisibleConsoleLines = 750') {
    if ($mainViewModelSource.IndexOf($consoleBound, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "The bounded live-console policy is missing: $consoleBound"
    }
}
if ($mainViewModelSource.IndexOf('MaximumRecoverySessionsShown = 100', [System.StringComparison]::Ordinal) -lt 0 -or
    $mainViewModelSource.IndexOf('.Take(MaximumRecoverySessionsShown)', [System.StringComparison]::Ordinal) -lt 0) {
    Add-Failure 'Recovery Center must keep its rendered session history bounded.'
}

$diagnosticEngineSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Engine\DiagnosticEngine.cs') -Raw
foreach ($diagnosticBound in 'MaximumTechnicalDetailCharacters = 64 * 1024', 'MaximumRecommendedFixes = 64', 'MaximumRepairParameters = 64', 'NormalizeFinding(check, finding)') {
    if ($diagnosticEngineSource.IndexOf($diagnosticBound, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "The diagnostic result boundary is missing: $diagnosticBound"
    }
}

$sessionSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Persistence\SessionManager.cs') -Raw
if ($sessionSource -notmatch 'MaximumSessionDirectories\s*=\s*512' -or $sessionSource -notmatch 'SortedSet<string>') {
    Add-Failure 'Session discovery must keep a bounded, newest-first directory set.'
}
if ($sessionSource -notmatch 'Flush\(flushToDisk:\s*true\)' -or
    $sessionSource.IndexOf('FileOptions.Asynchronous | FileOptions.WriteThrough', [System.StringComparison]::Ordinal) -lt 0) {
    Add-Failure 'Signed session envelopes must be explicitly flushed to disk before atomic replacement.'
}

$backupSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Persistence\BackupService.cs') -Raw
foreach ($durabilityGuard in 'FlushBackupContentToDisk', 'FileAccess.ReadWrite', 'Flush(flushToDisk: true)') {
    if ($backupSource.IndexOf($durabilityGuard, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "Recovery backups must be explicitly flushed before their signed intent is saved: $durabilityGuard"
    }
}

foreach ($recoveryGate in 'RecoveryRequired', 'SessionRecoveryGates.SessionRequiresRecovery', 'if (RecoveryRequired)') {
    if ($mainViewModelSource.IndexOf($recoveryGate, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "Interrupted repair workflow gate is missing: $recoveryGate"
    }
}
foreach ($handoffGuard in 'ShowHandoff = true', 'Handoff_Title', 'nextAvailableTier') {
    if ($mainViewModelSource.IndexOf($handoffGuard, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "The exhausted-repair handoff state is missing: $handoffGuard"
    }
}

$networkSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Modules\Network\NetworkModule.cs') -Raw
if ($networkSource -notmatch '"net\.live\.mtu"' -or $networkSource -notmatch 'maximumRounds\s*=\s*10') {
    Add-Failure 'The bounded path-MTU live test is missing.'
}
foreach ($targetedRouteGuard in 'NetworkParsers.EncodeRouteTargets', 'NetworkParsers.ParseRouteTargets', 'store=persistent', 'requiresTarget: true') {
    if ($networkSource.IndexOf($targetedRouteGuard, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "The persistent-route repair is not target-bound: $targetedRouteGuard"
    }
}

$soakSource = Get-Content -LiteralPath (Join-Path $root 'tools\soak-test.ps1') -Raw
foreach ($soakGuard in 'MaxWorkingSetMB', 'MaxHandleCount', 'resource-usage.csv', 'Get-ProcessTreeSnapshot', 'testhost|vstest') {
    if ($soakSource.IndexOf($soakGuard, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Add-Failure "The process-isolated soak guard is missing: $soakGuard"
    }
}
$soakWorkflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\soak.yml') -Raw
if ($soakWorkflow -notmatch 'soak-test\.ps1\s+-Iterations\s+50' -or
    $soakWorkflow -notmatch 'TestResults/Soak/\*\*') {
    Add-Failure 'The scheduled Windows soak workflow must run 50 iterations and retain its evidence.'
}

$audioSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Modules\Audio\AudioModule.cs') -Raw
$microphoneBody = [regex]::Match($audioSource, 'RunMicrophoneTestAsync\(.*?(?<Body>maximumCaptureBytes.*?)private static async Task<bool> PlayCapturedAudioAsync', [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $microphoneBody.Success -or
    $microphoneBody.Groups['Body'].Value -notmatch 'MemoryStream' -or
    $microphoneBody.Groups['Body'].Value -notmatch 'Array\.Clear' -or
    $microphoneBody.Groups['Body'].Value -match 'FileStream|WriteAllBytes|WaveFileWriter') {
    Add-Failure 'Microphone capture must remain bounded, memory-only, and explicitly cleared.'
}

$reportingSource = Get-Content -LiteralPath (Join-Path $root 'CaYaFix.Core\Persistence\ReportingServices.cs') -Raw
foreach ($redactor in 'EmailPattern', 'SidPattern', 'GuidPattern', 'GenericNameLinePattern') {
    if ($reportingSource.IndexOf($redactor, [System.StringComparison]::Ordinal) -lt 0) {
        Add-Failure "Privacy redactor is missing $redactor."
    }
}

if ($RequireScreenshots) {
    foreach ($name in 'dashboard.png', 'findings.png', 'live-tests.png') {
        $path = Join-Path $root "docs\screenshots\$name"
        if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 10000) {
            Add-Failure "A real screenshot is missing or too small: $path"
            continue
        }
        $bytes = [IO.File]::ReadAllBytes($path)
        $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
        if ($bytes.Length -lt 24 -or (Compare-Object $signature $bytes[0..7] -SyncWindow 0)) {
            Add-Failure "Screenshot is not a valid PNG file: $path"
            continue
        }
        $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
        # README captures are produced at 1600×1000 DIP × 2 (192 DPI) ≈ 3200×2000.
        if ($width -lt 1600 -or $height -lt 900) {
            Add-Failure "Screenshot resolution is below 1600x900: $path ($width x $height)"
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "Repository validation failed with $($failures.Count) issue(s)."
}

Write-Host "Repository validation passed: $($english.Count) localized keys, $($svgFiles.Count) SVG icons, 19 modules, 90 diagnostics, 163 repairs, and 8 live tests." -ForegroundColor Green
