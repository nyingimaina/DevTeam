# build-installer.ps1 - Standalone DevTeam installer build.
#
# Builds the Next.js front-end, publishes the broker (back-end) and the desktop shell
# framework-dependent win-x64 into one folder, then runs InnoSetup (iscc) to produce:
#   installer/DevTeam-Setup-<version>-win-x64.exe
#
# It is self-contained: it does NOT call the root build script. Every step is checked and the
# script exits non-zero if anything fails (REQ-12).
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1
# Params: -SkipFrontend   reuse the existing wwwroot (rebuild only .NET + installer)

param(
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"

# packaging\ -> repo root
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$BrokerProject = Join-Path $Root "back-end\DevTeam.Broker\DevTeam.Broker.csproj"
$DesktopProject = Join-Path $Root "DevTeam.Desktop\DevTeam.Desktop.csproj"
$FrontendDir = Join-Path $Root "front-end"
$FrontendOut = Join-Path $FrontendDir "out"
$PublishDir = Join-Path $Root "publish\app"
$InstallerDir = Join-Path $Root "installer"
$IssFile = Join-Path $Root "packaging\devteam.iss"

function Write-Step([string]$Message) { Write-Host "[installer] $Message" -ForegroundColor Cyan }
function Fail([string]$Message) {
    Write-Host "[installer] ERROR: $Message" -ForegroundColor Red
    exit 1
}
function Assert-ExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) { Fail "$Step failed (exit code $LASTEXITCODE)." }
}

# Deletes a directory even when it contains very deep paths (longer than MAX_PATH), which plain
# Remove-Item chokes on. robocopy /MIR from an empty folder empties the tree first.
function Remove-DirectoryRobust([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $empty = Join-Path $env:TEMP ("devteam-empty-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    try {
        robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    } finally {
        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Path) { Fail "Failed to remove '$Path'. A stale copy would be merged into the new build, so stopping." }
}

# --- Version: single source of truth is Directory.Build.props (REQ-7 / REQ-17) ---
$propsPath = Join-Path $Root "Directory.Build.props"
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found at $propsPath" }
[xml]$props = Get-Content -LiteralPath $propsPath
$version = $props.Project.PropertyGroup.InformationalVersion
if ([string]::IsNullOrWhiteSpace($version)) { Fail "InformationalVersion is not set in Directory.Build.props" }
$version = $version.Trim()
Write-Step "version $version"

function Assert-WebUiAssetsPresent([string]$WebRoot) {
    $indexPath = Join-Path $WebRoot "index.html"
    if (-not (Test-Path -LiteralPath $indexPath)) { Fail "The packaged web UI has no index.html." }

    $html = Get-Content -LiteralPath $indexPath -Raw
    $refs = [regex]::Matches($html, '(?:href|src)="(/_next/[^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique

    if ($refs.Count -eq 0) { Fail "index.html references no /_next/ assets; the export looks malformed." }

    $missing = @($refs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $WebRoot $_.TrimStart('/').Replace('/', '\'))) })
    if ($missing.Count -gt 0) {
        $missing | ForEach-Object { "  missing: $_" }
        Fail "The published web UI is missing $($missing.Count) asset(s) referenced by index.html (CSS or scripts would not load)."
    }
    Write-Step "web UI asset check: all $($refs.Count) referenced /_next/ assets present"
}

# --- 1. Front-end (static Next.js export) ---
if (-not $SkipFrontend) {
    if (-not (Test-Path (Join-Path $FrontendDir "package.json"))) {
        Fail "front-end/package.json not found; cannot build the web UI."
    }
    Write-Step "building front-end (npm ci + npm run build)"
    Remove-DirectoryRobust (Join-Path $FrontendDir "out")
    Push-Location $FrontendDir
    try {
        npm ci
        Assert-ExitCode "npm ci"
        npm run build
        Assert-ExitCode "npm run build"
    } finally {
        Pop-Location
    }
} else {
    Write-Step "skipping front-end build (-SkipFrontend); reusing front-end/out"
}

# --- 2. Publish broker + desktop shell framework-dependent win-x64 into one folder (REQ-1) ---
# The verification pipeline builds into a bin_verify\ scratch folder; the Web SDK would otherwise
# glob that scratch output as content and publish a deeply-nested copy of it. Remove it first so
# only the real application is packaged (REQ-16, REQ-19).
Remove-DirectoryRobust (Join-Path $Root "back-end\DevTeam.Broker\bin_verify")
Remove-DirectoryRobust $PublishDir

Write-Step "publishing broker (framework-dependent win-x64)"
dotnet publish $BrokerProject -c Release -r win-x64 --self-contained false -o $PublishDir -p:PublishSingleFile=false
Assert-ExitCode "broker publish"

Write-Step "publishing desktop shell (framework-dependent win-x64)"
dotnet publish $DesktopProject -c Release -r win-x64 --self-contained false -o $PublishDir -p:PublishSingleFile=false
Assert-ExitCode "desktop publish"

# Belt-and-braces: strip any stray scratch output that still slipped into the publish folder.
Remove-DirectoryRobust (Join-Path $PublishDir "bin_verify")

# Copy the exported web UI into the published app (the broker serves it from wwwroot). This writes
# only into the build output — the tracked source wwwroot is left untouched.
if (Test-Path $FrontendOut) {
    Write-Step "copying web UI into the published app"
    Remove-DirectoryRobust (Join-Path $PublishDir "wwwroot")
    $webRoot = Join-Path $PublishDir "wwwroot"
    # Copy-Item with a trailing "\*" and -Recurse into a destination that does not exist flattens the
    # first directory it meets instead of recreating it, which drops the _next level and 404s every
    # stylesheet and script. Create the destination first.
    New-Item -ItemType Directory -Force -Path $webRoot | Out-Null
    Copy-Item -Path (Join-Path $FrontendOut "*") -Destination $webRoot -Recurse -Force
    Assert-WebUiAssetsPresent $webRoot
} elseif (-not (Test-Path (Join-Path $PublishDir "wwwroot\index.html"))) {
    Fail "No web UI found (front-end/out is missing). Run without -SkipFrontend."
}

if (-not (Test-Path (Join-Path $PublishDir "DevTeam.Broker.exe"))) { Fail "DevTeam.Broker.exe missing from publish output." }
if (-not (Test-Path (Join-Path $PublishDir "DevTeam.Desktop.exe"))) { Fail "DevTeam.Desktop.exe missing from publish output." }
if (-not (Test-Path (Join-Path $PublishDir "wwwroot\index.html"))) { Fail "The web UI was not packaged (wwwroot\index.html missing)." }

# --- 3. Run InnoSetup (REQ-1, REQ-7) ---
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $iscc) {
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { $iscc = $candidate; break }
    }
}
if (-not $iscc) { Fail "InnoSetup compiler (iscc.exe) not found. Install Inno Setup 6 and retry." }

Write-Step "compiling installer with iscc"
& $iscc "/DAppVersion=$version" $IssFile
Assert-ExitCode "iscc"

$artifact = Join-Path $InstallerDir "DevTeam-Setup-$version-win-x64.exe"
if (-not (Test-Path $artifact)) { Fail "Installer artifact was not produced at $artifact" }

$sizeMb = [math]::Round((Get-Item $artifact).Length / 1MB, 1)
Write-Host "[installer] done. $artifact ($sizeMb MB)" -ForegroundColor Green
