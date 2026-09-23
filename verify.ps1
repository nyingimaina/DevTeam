<#
.SYNOPSIS
    Milele Ship Readiness Check - single command to verify build, type-check, tests, and coverage.

.DESCRIPTION
    Runs all phases sequentially and generates an HTML report with pass/fail/skip status.
    Exits with code 0 if all non-skipped phases pass, 1 otherwise.

.PARAMETER SkipE2E
    Skip backend E2E (integration) tests.

.PARAMETER SkipFrontend
    Skip all frontend phases (typecheck, build, lint, tests).

.EXAMPLE
    ./verify.ps1
    ./verify.ps1 -SkipE2E
    ./verify.ps1 -SkipFrontend -SkipE2E
#>
param(
    [switch]$SkipE2E,
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Web
$script:ExitCode = 0
$script:StartTime = Get-Date

$projectRoot = $PSScriptRoot
$reportDir = Join-Path $projectRoot "test-reports"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$htmlReport = Join-Path $reportDir "verify-$timestamp.html"
$trxDir = Join-Path $reportDir "trx-$timestamp"

New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

# Clean stale coverage data
Remove-Item "$projectRoot\frontend\coverage" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$trxDir\*.cobertura.xml" -Force -ErrorAction SilentlyContinue

$script:Phases = [System.Collections.ArrayList]::new()

function Add-Phase {
    param(
        [string]$Name, [string]$Status, [string]$Detail,
        [int]$Passed = 0, [int]$Failed = 0, [int]$Skipped = 0,
        [double]$DurationSec = 0, [array]$TestResults = @(),
        [string]$Output = "",
        [hashtable]$Coverage = $null
    )
    [void]$script:Phases.Add(@{
        Name        = $Name
        Status      = $Status
        Detail      = $Detail
        Passed      = $Passed
        Failed      = $Failed
        Skipped     = $Skipped
        DurationSec = $DurationSec
        TestResults = $TestResults
        Output      = $Output
        Coverage    = $Coverage
    })
}

function Test-DatabaseReachable {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("localhost", 3306)
        $tcp.Close()
        return $true
    } catch {
        return $false
    }
}

function Parse-TrxFile {
    param([string]$TrxPath)
    if (-not (Test-Path $TrxPath)) { return @() }

    [xml]$trx = Get-Content $TrxPath -Raw
    $results = @()
    $testDefinitions = @{}

    if ($trx.TestRun.TestDefinitions) {
        $trx.TestRun.TestDefinitions.UnitTest | ForEach-Object {
            $testDefinitions[$_.id] = $_.name
        }
    }

    if ($trx.TestRun.Results) {
        $trx.TestRun.Results.UnitTestResult | ForEach-Object {
            $outcome = $_.outcome
            $duration = 0
            if ($_.execution.time) {
                $ts = [TimeSpan]::TryParse($_.execution.time, [ref]$null)
                if ($ts) { $duration = $ts.TotalSeconds }
            }
            $results += @{
                Name     = $_.testName
                Status   = $outcome
                Duration = $duration
            }
        }
    }

    return $results
}

function Get-TrxSummary {
    param([string]$TrxPath)
    if (-not (Test-Path $TrxPath)) { return @{ Passed = 0; Failed = 0; Skipped = 0; Total = 0 } }

    [xml]$trx = Get-Content $TrxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    return @{
        Passed  = [int]$counters.passed
        Failed  = [int]$counters.failed
        Skipped = [int]$counters.notExecuted
        Total   = [int]$counters.total
    }
}

function Parse-CoberturaCoverage {
    param([string]$CoverageDir)
    $cobertura = Get-ChildItem $CoverageDir -Filter "coverage.cobertura.xml" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cobertura) { return $null }

    [xml]$xml = Get-Content $cobertura.FullName -Raw
    $lineRate = [double]$xml.coverage.GetAttribute("line-rate")
    $branchRate = [double]$xml.coverage.GetAttribute("branch-rate")
    return @{
        Line      = [math]::Round($lineRate * 100, 1)
        Branch    = [math]::Round($branchRate * 100, 1)
    }
}

function Parse-VitestCoverage {
    param([string]$CoverageJsonPath)
    if (-not (Test-Path $CoverageJsonPath)) { return $null }

    $raw = Get-Content $CoverageJsonPath -Raw | ConvertFrom-Json
    $total = $raw.total
    return @{
        Line   = [math]::Round($total.lines.pct, 1)
        Branch = [math]::Round($total.branches.pct, 1)
        Funcs  = [math]::Round($total.functions.pct, 1)
    }
}

# ─── HTML Report Generator ────────────────────────────────────────
function New-HtmlReport {
    param([string]$OutputPath)

    $totalPassed = ($script:Phases | Where-Object { $_.Status -eq "PASS" } | Measure-Object).Count
    $totalFailed = ($script:Phases | Where-Object { $_.Status -eq "FAIL" } | Measure-Object).Count
    $totalSkipped = ($script:Phases | Where-Object { $_.Status -eq "SKIPPED" } | Measure-Object).Count
    $allTestsPassed = $totalFailed -eq 0
    $overallColor = if ($allTestsPassed) { "#16a34a" } else { "#dc2626" }
    $overallIcon = if ($allTestsPassed) { "&#9989;" } else { "&#10060;" }
    $overallText = if ($allTestsPassed) { "PROCEED" } else { "DO NOT PROCEED" }
    $elapsed = (Get-Date) - $script:StartTime
    $elapsedStr = "{0:mm\:ss}" -f $elapsed

    $emDash = [char]0x2014

    # Aggregate test counts
    $allPassed = 0; $allFailed = 0; $allSkipped = 0
    foreach ($p in $script:Phases) {
        $allPassed += $p.Passed
        $allFailed += $p.Failed
        $allSkipped += $p.Skipped
    }
    $allTotal = $allPassed + $allFailed + $allSkipped

    # Build phase cards
    $phaseCards = ""
    foreach ($p in $script:Phases) {
        $statusColor = switch ($p.Status) {
            "PASS"    { "#16a34a" }
            "FAIL"    { "#dc2626" }
            "SKIPPED" { "#ca8a04" }
        }
        $statusBg = switch ($p.Status) {
            "PASS"    { "#f0fdf4" }
            "FAIL"    { "#fef2f2" }
            "SKIPPED" { "#fefce8" }
        }
        $statusBorder = switch ($p.Status) {
            "PASS"    { "#bbf7d0" }
            "FAIL"    { "#fecaca" }
            "SKIPPED" { "#fde68a" }
        }
        $statusIcon = switch ($p.Status) {
            "PASS"    { "&#9989;" }
            "FAIL"    { "&#10060;" }
            "SKIPPED" { "&#9888;&#65039;" }
        }
        $dur = if ($p.DurationSec -gt 0) { "{0:N1}s" -f $p.DurationSec } else { $emDash }

        # Test counts line
        $countsText = ""
        if ($p.Passed -gt 0 -or $p.Failed -gt 0 -or $p.Skipped -gt 0) {
            $phaseTotal = $p.Passed + $p.Failed + $p.Skipped
            $parts = @()
            $parts += "$($p.Passed)/$phaseTotal passed"
            if ($p.Failed -gt 0) { $parts += "$($p.Failed) failed" }
            if ($p.Skipped -gt 0) { $parts += "$($p.Skipped) skipped" }
            $countsText = ($parts -join ", ")
        }

        # Coverage bar
        $coverageBar = ""
        if ($p.Coverage -and $p.Coverage.Line -ne $null) {
            $linePct = $p.Coverage.Line
            $branchPct = if ($p.Coverage.Branch -ne $null) { $p.Coverage.Branch } else { 0 }
            $funcPct = if ($p.Coverage.Funcs -ne $null) { $p.Coverage.Funcs } else { 0 }
            $lineHue = [math]::Round($linePct * 1.2)
            $lineColor = "hsl($lineHue, 70%, 45%)"
            $branchHue = [math]::Round($branchPct * 1.2)
            $branchColor = "hsl($branchHue, 70%, 45%)"
            $funcHue = [math]::Round($funcPct * 1.2)
            $funcColor = "hsl($funcHue, 70%, 45%)"
            $branchBar = ""
            if ($funcPct -gt 0) {
                $branchBar = @"
                    <div class="cov-detail"><span class="cov-label">Branch</span> <div class="cov-track"><div class="cov-fill" style="width:${branchPct}%;background:${branchColor};"></div></div> <span class="cov-val">${branchPct}%</span></div>
                    <div class="cov-detail"><span class="cov-label">Funcs</span> <div class="cov-track"><div class="cov-fill" style="width:${funcPct}%;background:${funcColor};"></div></div> <span class="cov-val">${funcPct}%</span></div>
"@
            } else {
                $branchBar = @"
                    <div class="cov-detail"><span class="cov-label">Branch</span> <div class="cov-track"><div class="cov-fill" style="width:${branchPct}%;background:${branchColor};"></div></div> <span class="cov-val">${branchPct}%</span></div>
"@
            }
            $coverageBar = @"
            <div class="coverage">
                <div class="cov-detail"><span class="cov-label">Line</span> <div class="cov-track"><div class="cov-fill" style="width:${linePct}%;background:${lineColor};"></div></div> <span class="cov-val">${linePct}%</span></div>
                $branchBar
            </div>
"@
        }

        # Detail / skip reason
        $detailHtml = ""
        if ($p.Detail) {
            $detailHtml = "<div class='phase-detail'>$([System.Web.HttpUtility]::HtmlEncode($p.Detail))</div>"
        }

        $phaseCards += @"
        <div class="phase-card" style="border-left-color:$statusBorder;">
            <div class="phase-header">
                <span class="phase-icon">$statusIcon</span>
                <span class="phase-name">$($p.Name)</span>
                <span class="phase-dur">$dur</span>
            </div>
            <div class="phase-status" style="color:$statusColor;font-weight:600;">$($p.Status)</div>
            $(if ($countsText) { "<div class='phase-counts'>$countsText</div>" } else { "" })
            $detailHtml
            $coverageBar
        </div>
"@
    }

    # Build test detail sections (collapsible, only for phases with tests)
    $testDetailSections = ""
    foreach ($p in $script:Phases) {
        if ($p.TestResults.Count -eq 0) { continue }

        $testRows = ""
        $failCount = 0
        foreach ($t in $p.TestResults) {
            $tStatus = switch ($t.Status) {
                "Passed"      { "<span class='t-pass'>&#9989;</span>" }
                "Failed"      { "<span class='t-fail'>&#10060;</span>" }
                "NotExecuted" { "<span class='t-skip'>&#9888;&#65039;</span>" }
                default       { $t.Status }
            }
            $dur = if ($t.Duration -gt 0) { "{0:N2}s" -f $t.Duration } else { $emDash }
            $rowClass = if ($t.Status -eq "Failed") { " class='row-fail'" } else { "" }
            $failCount += if ($t.Status -eq "Failed") { 1 } else { 0 }
            $testRows += @"
            <tr$rowClass>
                <td class="test-name">$([System.Web.HttpUtility]::HtmlEncode($t.Name))</td>
                <td class="test-dur">$dur</td>
            </tr>
"@
        }

        $failBadge = if ($failCount -gt 0) { " <span class='fail-badge'>$failCount failed</span>" } else { "" }
        $testDetailSections += @"
        <details class="test-details">
            <summary>$($p.Name) $emDash $($p.TestResults.Count) tests$failBadge</summary>
            <table class="test-table">
                <thead><tr><th>Test</th><th class="test-dur-col">Duration</th></tr></thead>
                <tbody>$testRows</tbody>
            </table>
        </details>
"@
    }

    # Build failure detail sections
    $failureDetails = ""
    foreach ($p in $script:Phases) {
        if ($p.Status -ne "FAIL" -or [string]::IsNullOrWhiteSpace($p.Output)) { continue }
        $escapedOutput = [System.Web.HttpUtility]::HtmlEncode($p.Output)
        $failureDetails += @"
        <details class="failure-details">
            <summary>$($p.Name) $emDash Failure Output</summary>
            <pre class="failure-output">$escapedOutput</pre>
        </details>
"@
    }

    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Milele $emDash Ship Readiness Report</title>
<style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
        background: #f1f5f9; color: #1e293b; line-height: 1.5;
        padding: 1rem; font-size: 15px;
    }
    @media (min-width: 640px) { body { padding: 1.5rem; } }
    @media (min-width: 1024px) { body { padding: 2rem; font-size: 16px; } }

    .container { max-width: 960px; margin: 0 auto; }

    /* Header */
    .header {
        background: #fff; border-radius: 10px; padding: 1rem 1.25rem;
        margin-bottom: 1rem; box-shadow: 0 1px 3px rgba(0,0,0,0.06);
        border-left: 5px solid $overallColor;
    }
    @media (min-width: 640px) { .header { padding: 1.5rem 2rem; margin-bottom: 1.25rem; border-radius: 12px; } }
    .header h1 { font-size: 1.15rem; margin-bottom: 0.15rem; }
    @media (min-width: 640px) { .header h1 { font-size: 1.4rem; } }
    .header .meta { color: #64748b; font-size: 0.8rem; }
    @media (min-width: 640px) { .header .meta { font-size: 0.875rem; } }

    /* Verdict */
    .verdict {
        text-align: center; padding: 1.25rem 1rem; background: #fff;
        border-radius: 10px; margin-bottom: 1rem;
        box-shadow: 0 1px 3px rgba(0,0,0,0.06);
    }
    @media (min-width: 640px) { .verdict { padding: 1.5rem; border-radius: 12px; margin-bottom: 1.25rem; } }
    .verdict .icon { font-size: 2.25rem; }
    @media (min-width: 640px) { .verdict .icon { font-size: 3rem; } }
    .verdict .label { font-size: 1.5rem; font-weight: 700; color: $overallColor; margin: 0.35rem 0 0.2rem; }
    @media (min-width: 640px) { .verdict .label { font-size: 2rem; } }
    .verdict .sub { color: #64748b; font-size: 0.8rem; }
    @media (min-width: 640px) { .verdict .sub { font-size: 0.875rem; } }

    /* Stats row */
    .stats {
        display: grid; grid-template-columns: repeat(2, 1fr); gap: 0.75rem;
        margin-bottom: 1rem;
    }
    @media (min-width: 640px) { .stats { grid-template-columns: repeat(4, 1fr); gap: 1rem; } }
    .stat {
        background: #fff; border-radius: 10px; padding: 0.75rem 1rem;
        box-shadow: 0 1px 3px rgba(0,0,0,0.06); text-align: center;
    }
    .stat .stat-val { font-size: 1.5rem; font-weight: 700; color: #1e293b; }
    @media (min-width: 640px) { .stat .stat-val { font-size: 1.75rem; } }
    .stat .stat-label { font-size: 0.7rem; color: #64748b; text-transform: uppercase; letter-spacing: 0.04em; font-weight: 600; }
    @media (min-width: 640px) { .stat .stat-label { font-size: 0.75rem; } }

    /* Phase cards */
    .section-title {
        font-size: 0.95rem; font-weight: 700; color: #334155;
        margin-bottom: 0.75rem; padding-left: 0.15rem;
    }
    @media (min-width: 640px) { .section-title { font-size: 1.05rem; margin-bottom: 1rem; } }
    .phases-grid {
        display: grid; gap: 0.65rem; margin-bottom: 1.25rem;
    }
    @media (min-width: 640px) { .phases-grid { grid-template-columns: repeat(2, 1fr); gap: 0.85rem; } }
    .phase-card {
        background: #fff; border-radius: 10px; padding: 0.85rem 1rem;
        box-shadow: 0 1px 3px rgba(0,0,0,0.06);
        border-left: 4px solid #e2e8f0;
    }
    @media (min-width: 640px) { .phase-card { padding: 1rem 1.15rem; } }
    .phase-header {
        display: flex; align-items: center; gap: 0.4rem; margin-bottom: 0.35rem;
    }
    .phase-icon { font-size: 1rem; flex-shrink: 0; }
    .phase-name { font-weight: 600; font-size: 0.875rem; flex: 1; min-width: 0; }
    @media (min-width: 640px) { .phase-name { font-size: 0.9375rem; } }
    .phase-dur { font-size: 0.75rem; color: #94a3b8; font-variant-numeric: tabular-nums; flex-shrink: 0; }
    .phase-status { font-size: 0.8rem; margin-bottom: 0.15rem; }
    .phase-counts { font-size: 0.75rem; color: #64748b; }
    .phase-detail { font-size: 0.75rem; color: #94a3b8; margin-top: 0.25rem; }

    /* Coverage bars inside phase cards */
    .coverage { margin-top: 0.5rem; padding-top: 0.5rem; border-top: 1px solid #f1f5f9; }
    .cov-detail { display: flex; align-items: center; gap: 0.4rem; margin-bottom: 0.3rem; }
    .cov-label { font-size: 0.7rem; color: #64748b; width: 3.5rem; flex-shrink: 0; font-weight: 500; }
    .cov-track { flex: 1; height: 6px; background: #f1f5f9; border-radius: 3px; overflow: hidden; }
    .cov-fill { height: 100%; border-radius: 3px; transition: width 0.3s; }
    .cov-val { font-size: 0.7rem; font-weight: 600; color: #334155; width: 2.75rem; text-align: right; font-variant-numeric: tabular-nums; }

    /* Test details */
    .test-details {
        background: #fff; border-radius: 10px; margin-bottom: 0.85rem;
        box-shadow: 0 1px 3px rgba(0,0,0,0.06); overflow: hidden;
    }
    .test-details summary {
        padding: 0.75rem 1rem; cursor: pointer; font-weight: 600;
        font-size: 0.875rem; user-select: none; list-style: none;
    }
    @media (min-width: 640px) { .test-details summary { font-size: 0.9375rem; padding: 0.85rem 1.25rem; } }
    .test-details summary::-webkit-details-marker { display: none; }
    .test-details summary::before { content: "\25B6"; font-size: 0.6rem; margin-right: 0.5rem; color: #94a3b8; }
    .test-details[open] summary::before { content: "\25BC"; }
    .test-details summary:hover { background: #f8fafc; }
    .fail-badge {
        background: #fef2f2; color: #dc2626; font-size: 0.7rem;
        padding: 0.1rem 0.45rem; border-radius: 4px; font-weight: 600;
        margin-left: 0.4rem;
    }

    /* Test table */
    .test-table { width: 100%; border-collapse: collapse; }
    .test-table th {
        text-align: left; padding: 0.5rem 1rem; background: #f8fafc;
        color: #64748b; font-size: 0.7rem; text-transform: uppercase;
        letter-spacing: 0.04em; font-weight: 600; border-top: 1px solid #f1f5f9;
    }
    .test-table td {
        padding: 0.4rem 1rem; border-top: 1px solid #f8fafc;
        font-size: 0.8125rem; word-break: break-word;
    }
    .test-name { font-family: 'SF Mono', 'Cascadia Code', Consolas, monospace; font-size: 0.75rem; }
    @media (min-width: 640px) { .test-name { font-size: 0.8125rem; } }
    .test-dur { color: #94a3b8; font-size: 0.75rem; font-variant-numeric: tabular-nums; white-space: nowrap; }
    .test-dur-col { width: 5rem; }
    .row-fail { background: #fef2f2; }
    .row-fail:hover td { background: #fee2e2; }
    .t-pass { color: #16a34a; }
    .t-fail { color: #dc2626; }
    .t-skip { color: #ca8a04; }

    /* Failure output */
    .failure-details {
        background: #fff; border-radius: 10px; margin-bottom: 0.85rem;
        box-shadow: 0 1px 3px rgba(0,0,0,0.06); overflow: hidden;
    }
    .failure-details summary {
        padding: 0.75rem 1rem; cursor: pointer; font-weight: 600;
        font-size: 0.875rem; color: #dc2626; user-select: none; list-style: none;
    }
    .failure-details summary::-webkit-details-marker { display: none; }
    .failure-details summary::before { content: "\25B6"; font-size: 0.6rem; margin-right: 0.5rem; color: #94a3b8; }
    .failure-details[open] summary::before { content: "\25BC"; }
    .failure-output {
        background: #1e293b; color: #e2e8f0; padding: 0.85rem 1rem;
        border-radius: 0; font-size: 0.75rem; overflow-x: auto;
        max-height: 350px; overflow-y: auto; white-space: pre-wrap;
        word-break: break-word; margin: 0;
        font-family: 'SF Mono', 'Cascadia Code', Consolas, monospace;
    }
    @media (min-width: 640px) { .failure-output { padding: 1rem 1.25rem; font-size: 0.8125rem; } }

    .footer {
        text-align: center; color: #94a3b8; font-size: 0.75rem;
        margin-top: 1.5rem; padding-bottom: 1rem;
    }
</style>
</head>
<body>
<div class="container">
    <div class="header">
        <h1>Milele $emDash Ship Readiness Report</h1>
        <div class="meta">$(Get-Date -Format "yyyy-MM-dd HH:mm:ss") $emDash $elapsedStr</div>
    </div>

    <div class="verdict">
        <div class="icon">$overallIcon</div>
        <div class="label">$overallText</div>
        <div class="sub">$allPassed/$allTotal tests passed, $allFailed failed, $allSkipped skipped</div>
    </div>

    <div class="stats">
        <div class="stat"><div class="stat-val">$allTotal</div><div class="stat-label">Tests</div></div>
        <div class="stat"><div class="stat-val" style="color:#16a34a;">$allPassed/$allTotal</div><div class="stat-label">Passed</div></div>
        <div class="stat"><div class="stat-val" style="color:#64748b;">$elapsedStr</div><div class="stat-label">Duration</div></div>
        <div class="stat"><div class="stat-val" style="color:$(if ($totalFailed -eq 0) { '#16a34a' } else { '#dc2626' });">$totalPassed/$($totalPassed + $totalFailed + $totalSkipped)</div><div class="stat-label">Phases</div></div>
    </div>

    <div class="section-title">Phases</div>
    <div class="phases-grid">
        $phaseCards
    </div>

    $testDetailSections

    $failureDetails

    <div class="footer">Milele Ship Readiness $emDash verify.ps1</div>
</div>
</body>
</html>
"@

    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($OutputPath, $html, $utf8Bom)
}

# ═══════════════════════════════════════════════════════════════════
# PHASE 1: Backend Build
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PHASE 1: Backend Build" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$phaseStart = Get-Date
$buildOutput = & dotnet build "$projectRoot/backend/Milele.csproj" 2>&1 | Out-String
$buildExit = $LASTEXITCODE
$buildDur = ((Get-Date) - $phaseStart).TotalSeconds

if ($buildExit -ne 0) {
    Write-Host "  FAILED" -ForegroundColor Red
    Add-Phase "Backend Build" "FAIL" "dotnet build exited with code $buildExit" -DurationSec $buildDur -Output $buildOutput
    $script:ExitCode = 1
    New-HtmlReport $htmlReport
    Start-Process $htmlReport
    exit $script:ExitCode
}

Write-Host "  PASSED" -ForegroundColor Green
Add-Phase "Backend Build" "PASS" -DurationSec $buildDur

# ═══════════════════════════════════════════════════════════════════
# PHASE 2: Backend Unit Tests
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PHASE 2: Backend Unit Tests" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$phaseStart = Get-Date
$unitTrx = Join-Path $trxDir "unit-tests.trx"
$unitOutput = & dotnet test "$projectRoot/tests/Milele.Tests/Milele.Tests.csproj" `
    --filter "Category!=Integration" `
    --settings "$projectRoot/.runsettings" `
    --logger "trx;LogFileName=unit-tests.trx" `
    --results-directory $trxDir 2>&1 | Out-String
$unitExit = $LASTEXITCODE
$unitDur = ((Get-Date) - $phaseStart).TotalSeconds

$unitSummary = Get-TrxSummary -TrxPath $unitTrx
$unitResults = Parse-TrxFile -TrxPath $unitTrx
$unitCoverage = Parse-CoberturaCoverage -CoverageDir $trxDir

if ($unitExit -ne 0) {
    Write-Host "  FAILED ($($unitSummary.Failed) failed, $($unitSummary.Passed) passed)" -ForegroundColor Red
    Add-Phase "Backend Unit Tests" "FAIL" -Passed $unitSummary.Passed -Failed $unitSummary.Failed -Skipped $unitSummary.Skipped -DurationSec $unitDur -TestResults $unitResults -Output $unitOutput -Coverage $unitCoverage
    $script:ExitCode = 1
} else {
    Write-Host "  PASSED ($($unitSummary.Passed) passed)" -ForegroundColor Green
    if ($unitCoverage) { Write-Host "  Coverage: $($unitCoverage.Line)% line, $($unitCoverage.Branch)% branch" -ForegroundColor Gray }
    Add-Phase "Backend Unit Tests" "PASS" -Passed $unitSummary.Passed -Failed $unitSummary.Failed -Skipped $unitSummary.Skipped -DurationSec $unitDur -TestResults $unitResults -Coverage $unitCoverage
}

# ═══════════════════════════════════════════════════════════════════
# PHASE 3: Backend E2E Tests
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PHASE 3: Backend E2E Tests" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

if ($SkipE2E) {
    Write-Host "  SKIPPED (flag: -SkipE2E)" -ForegroundColor Yellow
    Add-Phase "Backend E2E Tests" "SKIPPED" "Skipped via -SkipE2E flag. Run without -SkipE2E to include integration tests."
} elseif (-not (Test-DatabaseReachable)) {
    Write-Host "  SKIPPED (MariaDB not reachable on localhost:3306)" -ForegroundColor Yellow
    Add-Phase "Backend E2E Tests" "SKIPPED" "MariaDB not reachable on localhost:3306. Start MariaDB to enable E2E tests."
} else {
    $phaseStart = Get-Date
    $e2eTrx = Join-Path $trxDir "e2e-tests.trx"
    $e2eOutput = & dotnet test "$projectRoot/tests/Milele.Tests/Milele.Tests.csproj" `
        --no-build `
        --filter "Category=Integration" `
        --settings "$projectRoot/.runsettings" `
        --logger "trx;LogFileName=e2e-tests.trx" `
        --results-directory $trxDir 2>&1 | Out-String
    $e2eExit = $LASTEXITCODE
    $e2eDur = ((Get-Date) - $phaseStart).TotalSeconds

    $e2eSummary = Get-TrxSummary -TrxPath $e2eTrx
    $e2eResults = Parse-TrxFile -TrxPath $e2eTrx

    if ($e2eExit -ne 0) {
        Write-Host "  FAILED ($($e2eSummary.Failed) failed, $($e2eSummary.Passed) passed)" -ForegroundColor Red
        Add-Phase "Backend E2E Tests" "FAIL" -Passed $e2eSummary.Passed -Failed $e2eSummary.Failed -Skipped $e2eSummary.Skipped -DurationSec $e2eDur -TestResults $e2eResults -Output $e2eOutput
        $script:ExitCode = 1
    } else {
        Write-Host "  PASSED ($($e2eSummary.Passed) passed)" -ForegroundColor Green
        Add-Phase "Backend E2E Tests" "PASS" -Passed $e2eSummary.Passed -Failed $e2eSummary.Failed -Skipped $e2eSummary.Skipped -DurationSec $e2eDur -TestResults $e2eResults
    }
}

# ═══════════════════════════════════════════════════════════════════
# PHASE 4: Frontend
# ═══════════════════════════════════════════════════════════════════
if (-not $SkipFrontend) {

    # ─── 4a: Frontend Type Check ──────────────────────────────────
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  PHASE 4a: Frontend Type Check" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    $phaseStart = Get-Date
    $tscTempFile = Join-Path $trxDir "tsc-output.txt"
    cmd /c "cd /d `"$projectRoot\frontend`" && npx tsc --noEmit > `"$tscTempFile`" 2>&1"
    $tscExit = $LASTEXITCODE
    $tscOutput = if (Test-Path $tscTempFile) { Get-Content $tscTempFile -Raw } else { "" }
    $tscDur = ((Get-Date) - $phaseStart).TotalSeconds

    if ($tscExit -ne 0) {
        Write-Host "  FAILED (tsc --noEmit found type errors)" -ForegroundColor Red
        Add-Phase "Frontend Type Check" "FAIL" "TypeScript compiler reported type errors" -DurationSec $tscDur -Output $tscOutput
        $script:ExitCode = 1
    } else {
        Write-Host "  PASSED" -ForegroundColor Green
        Add-Phase "Frontend Type Check" "PASS" -DurationSec $tscDur
    }

    # ─── 4b: Frontend Build ───────────────────────────────────────
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  PHASE 4b: Frontend Build" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    if ($tscExit -ne 0) {
        Write-Host "  SKIPPED (type check failed, build would fail too)" -ForegroundColor Yellow
        Add-Phase "Frontend Build" "SKIPPED" "Skipped because Frontend Type Check failed. Fix type errors first."
    } else {
        $phaseStart = Get-Date
        $buildTempFile = Join-Path $trxDir "next-build-output.txt"
        cmd /c "cd /d `"$projectRoot\frontend`" && npx next build > `"$buildTempFile`" 2>&1"
        $nextBuildExit = $LASTEXITCODE
        $nextBuildOutput = if (Test-Path $buildTempFile) { Get-Content $buildTempFile -Raw } else { "" }
        $nextBuildDur = ((Get-Date) - $phaseStart).TotalSeconds

        if ($nextBuildExit -ne 0) {
            Write-Host "  FAILED (next build exited with code $nextBuildExit)" -ForegroundColor Red
            Add-Phase "Frontend Build" "FAIL" "next build failed" -DurationSec $nextBuildDur -Output $nextBuildOutput
            $script:ExitCode = 1
        } else {
            Write-Host "  PASSED" -ForegroundColor Green
            Add-Phase "Frontend Build" "PASS" -DurationSec $nextBuildDur
        }
    }

    # ─── 4c: Frontend Lint ────────────────────────────────────────
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  PHASE 4c: Frontend Lint" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    $eslintConfig = Get-ChildItem "$projectRoot/frontend" -Filter "eslint.config.*" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $eslintConfig) {
        $eslintConfig = Get-ChildItem "$projectRoot/frontend" -Filter ".eslint*" -ErrorAction SilentlyContinue |
            Select-Object -First 1
    }
    $packageJsonEslint = Select-String -Path "$projectRoot/frontend/package.json" -Pattern '"eslintConfig"' -ErrorAction SilentlyContinue
    $hasEslintConfig = $null -ne $eslintConfig -or $null -ne $packageJsonEslint

    if (-not $hasEslintConfig) {
        Write-Host "  SKIPPED (no ESLint config found)" -ForegroundColor Yellow
        Add-Phase "Frontend Lint" "SKIPPED" "No .eslintrc.json or eslintConfig in package.json. Create one to enable linting."
    } else {
        $phaseStart = Get-Date
        Push-Location "$projectRoot\frontend"
        $lintTempFile = Join-Path $trxDir "lint-output.txt"
        cmd /c "npx eslint . > `"$lintTempFile`" 2>&1"
        $lintExit = $LASTEXITCODE
        $lintOutput = if (Test-Path $lintTempFile) { Get-Content $lintTempFile -Raw } else { "" }
        Pop-Location
        $lintDur = ((Get-Date) - $phaseStart).TotalSeconds

        if ($lintExit -ne 0) {
            Write-Host "  FAILED" -ForegroundColor Red
            Add-Phase "Frontend Lint" "FAIL" -DurationSec $lintDur -Output $lintOutput
            $script:ExitCode = 1
        } else {
            Write-Host "  PASSED" -ForegroundColor Green
            Add-Phase "Frontend Lint" "PASS" -DurationSec $lintDur
        }
    }

    # ─── 4d: Frontend Tests ───────────────────────────────────────
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  PHASE 4d: Frontend Tests" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    $phaseStart = Get-Date
    $testTempFile = Join-Path $trxDir "frontend-test-output.txt"
    cmd /c "cd /d `"$projectRoot\frontend`" && npx vitest run --coverage > `"$testTempFile`" 2>&1"
    $testExit = $LASTEXITCODE
    $testOutput = if (Test-Path $testTempFile) { Get-Content $testTempFile -Raw } else { "" }
    $testDur = ((Get-Date) - $phaseStart).TotalSeconds

    $vitestCoverage = Parse-VitestCoverage -CoverageJsonPath "$projectRoot\frontend\coverage\coverage-summary.json"

    if ($testExit -ne 0) {
        Write-Host "  FAILED" -ForegroundColor Red
        Add-Phase "Frontend Tests" "FAIL" -DurationSec $testDur -Output $testOutput -Coverage $vitestCoverage
        $script:ExitCode = 1
    } else {
        Write-Host "  PASSED" -ForegroundColor Green
        if ($vitestCoverage) { Write-Host "  Coverage: $($vitestCoverage.Line)% line, $($vitestCoverage.Branch)% branch" -ForegroundColor Gray }
        Add-Phase "Frontend Tests" "PASS" -DurationSec $testDur -Coverage $vitestCoverage
    }
}

# ═══════════════════════════════════════════════════════════════════
# Report
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Generating Report" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

New-HtmlReport $htmlReport
Write-Host "  Report: $htmlReport" -ForegroundColor White

Start-Process $htmlReport

$totalElapsed = (Get-Date) - $script:StartTime
Write-Host "`n========================================" -ForegroundColor $(if ($script:ExitCode -eq 0) { "Green" } else { "Red" })
if ($script:ExitCode -eq 0) {
    Write-Host "  RESULT: PROCEED" -ForegroundColor Green
} else {
    Write-Host "  RESULT: DO NOT PROCEED" -ForegroundColor Red
}
Write-Host "  Total time: $("{0:mm\:ss}" -f $totalElapsed)" -ForegroundColor Gray
Write-Host "========================================`n" -ForegroundColor $(if ($script:ExitCode -eq 0) { "Green" } else { "Red" })

exit $script:ExitCode
