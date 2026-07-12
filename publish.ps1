#requires -version 5
<#
.SYNOPSIS
  Build, pack, and publish a Quickening release.
.EXAMPLE
  .\publish.ps1 1.0.0
  .\publish.ps1 1.0.1 -NoServerUpload
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Version,

    # Skip the scp upload of Setup.exe to quickening.app (GitHub release still happens).
    [switch] $NoServerUpload
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$publishDir = Join-Path $repo 'artifacts\publish'
$releasesDir = Join-Path $repo 'artifacts\releases'

# --- Preflight ------------------------------------------------------------
if (-not ($Version -match '^\d+\.\d+\.\d+$')) {
    throw "Version must be SemVer major.minor.patch, e.g. 1.0.0 (got '$Version')."
}
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "The 'vpk' tool is not installed. Run: dotnet tool install -g vpk"
}
if (-not $env:GITHUB_TOKEN) {
    throw "GITHUB_TOKEN env var is not set (needs 'repo' scope for vpk upload)."
}

# --- Build ----------------------------------------------------------------
Write-Host "==> Publishing win-x64 self-contained build ($Version)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $repo 'src\Quickening.App\Quickening.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version `
    -o $publishDir

# --- Pack -----------------------------------------------------------------
Write-Host "==> Packing with Velopack..." -ForegroundColor Cyan
vpk pack `
    --packId Quickening `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Quickening.exe `
    --packTitle Quickening `
    --outputDir $releasesDir

# --- Upload to GitHub Releases --------------------------------------------
Write-Host "==> Uploading to GitHub Releases..." -ForegroundColor Cyan
vpk upload github `
    --repoUrl https://github.com/tvirelli/Quickening `
    --publish `
    --releaseName "Quickening $Version" `
    --tag "v$Version" `
    --token $env:GITHUB_TOKEN `
    --outputDir $releasesDir

# --- Upload Setup.exe to the self-hosted download endpoint -----------------
if (-not $NoServerUpload) {
    $setup = Join-Path $releasesDir 'Quickening-win-Setup.exe'
    if (-not (Test-Path $setup)) {
        # Velopack names the installer <PackId>-<channel>-Setup.exe (channel is
        # "win" for a default win-x64 pack); fall back to any *Setup.exe.
        $setup = (Get-ChildItem $releasesDir -Filter '*Setup.exe' | Select-Object -First 1).FullName
    }
    if (-not $setup) { throw "No Setup.exe found in $releasesDir." }
    Write-Host "==> Uploading $setup to quickening.app..." -ForegroundColor Cyan
    # -P is the SSH port. May prompt for the SSH password unless key auth is set.
    scp -P 21098 $setup 'ypemnnws@162.0.209.39:~/quickening.app/public_html/files/QuickeningSetup.exe'
}

Write-Host "==> Done. Release v$Version published." -ForegroundColor Green
