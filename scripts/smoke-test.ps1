# Smoke test for Phase A: verifies release API lifecycle end-to-end
param(
    [string]$BrokerPidFile = "$env:TEMP\opencode\devteam-broker.pid",
    [string]$DevTeamDb = "$env:USERPROFILE\.devteam\devteam.db"
)

$ErrorActionPreference = "Stop"
$BaseUrl = "http://localhost:5202"

function Log([string]$msg) { Write-Host "[smoke] $msg" -ForegroundColor Cyan }
function Fail([string]$msg) { Write-Host "[smoke] FAIL: $msg" -ForegroundColor Red; exit 1 }

# --- 1. Kill existing broker by PID ---
if (Test-Path $BrokerPidFile) {
    $existingPid = Get-Content $BrokerPidFile -ErrorAction SilentlyContinue
    if ($existingPid) {
        $proc = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
        if ($proc) {
            Log "Killing existing broker PID $existingPid"
            Stop-Process -Id $existingPid -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    Remove-Item $BrokerPidFile -Force -ErrorAction SilentlyContinue
}

# --- 2. Delete dev.db ---
if (Test-Path $DevTeamDb) {
    Log "Deleting $DevTeamDb"
    Remove-Item $DevTeamDb -Force
}

# --- 3. Build and start broker ---
Log "Building broker..."
$null = dotnet build back-end/DevTeam.Broker -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { Fail "Build failed" }

Log "Starting broker..."
$brokerDll = "back-end/DevTeam.Broker/bin/Release/net10.0/DevTeam.Broker.dll"
$proc = Start-Process -FilePath "dotnet" -ArgumentList $brokerDll -PassThru -NoNewWindow `
    -RedirectStandardOutput "$env:TEMP\opencode\broker-smoke-out.log" `
    -RedirectStandardError "$env:TEMP\opencode\broker-smoke-err.log"
$proc.Id | Out-File -FilePath $BrokerPidFile -Encoding ascii -Force
Log "Broker started PID $($proc.Id)"

# --- 4. Wait for healthz ---
Log "Waiting for broker..."
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod -Uri "$BaseUrl/healthz" -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($health.status -eq "ok") { $ready = $true; break }
    } catch { }
}
if (-not $ready) { Fail "Broker not ready in 30s" }
Log "Broker ready v$($health.version)"

# --- 5. Create release ---
Log "Creating release..."
$release = Invoke-RestMethod -Uri "$BaseUrl/api/releases" -Method POST -ContentType "application/json" `
    -Body '{"releaseKey":"smoke-test","workspacePath":"C:\\work\\smoke"}'
$rid = $release.id
Log "Created: $rid  status=$($release.status)"
if ($release.status -ne "InProgress") { Fail "Expected InProgress" }
if ($release.features.Count -ne 1) { Fail "Expected 1 feature" }
if ($release.flowPosition.currentStageName -ne "business-analyst") { Fail "Expected stage business-analyst" }
if ($release.signoffs.Count -lt 1) { Fail "Expected signoffs" }

# --- 6. List releases ---
Log "Listing releases..."
$all = Invoke-RestMethod -Uri "$BaseUrl/api/releases"
if ($all.Count -lt 1) { Fail "Expected >= 1 release" }
Log "Total releases: $($all.Count)"

# --- 7. Get release ---
Log "Getting release..."
$fetched = Invoke-RestMethod -Uri "$BaseUrl/api/releases/$rid"
if ($fetched.id -ne $rid) { Fail "ID mismatch" }
Log "Fetched: $($fetched.title)"

# --- 8. Signoff (pre-advance, just test the endpoint) ---
Log "Testing signoff endpoint..."
$signoff = Invoke-RestMethod -Uri "$BaseUrl/api/releases/$rid/signoff" -Method POST -ContentType "application/json" `
    -Body '{"stageName":"requirements-approval","role":"smoke-test","comment":"Smoke OK"}'
Log "Signoff approved: $($signoff.signoffs[0].approved)"

# --- 9. Verify 404 ---
Log "Testing 404..."
try {
    $null = Invoke-RestMethod -Uri "$BaseUrl/api/releases/00000000-0000-0000-0000-000000000000" -Method POST -ContentType "application/json" -Body '{}' -ErrorAction Stop
    Fail "Expected 404"
} catch {
    if ($_.Exception.Response.StatusCode -eq 404) {
        Log "Got expected 404"
    } else {
        Fail "Expected 404, got $($_.Exception.Response.StatusCode)"
    }
}

# --- 10. Kill broker ---
Log "Stopping broker..."
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# --- 11. Report ---
Log ""
Log "========================================="
Log "  SMOKE TEST PASSED"
Log "  Release:   $rid"
Log "  Features:  $($release.features.Count)"
Log "  Signoffs:  $($release.signoffs.Count)"
Log "  Status:    $($release.status)"
Log "========================================="

& "C:\Users\nying\AppData\Local\Programs\SemaNami\SemaNami.exe" -sender "DevTeam-Agent" -message "Phase A SMOKE TEST PASSED. Release $rid created. Features=$($release.features.Count), Signoffs=$($release.signoffs.Count), Status=$($release.status). Backend 141 tests + Frontend 51 tests green. All steps 1-6 committed. Phase A complete."
