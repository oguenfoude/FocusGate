# ==============================================================================
# FocusGate Build, Test, and Single-Distribution Deployment Script
# ==============================================================================
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host " FocusGate Pipeline: Build -> Test -> Publish -> Merge " -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

# 1. Solution Build Verification
Write-Host "`n[1/4] Building Solution ($Configuration)..." -ForegroundColor Yellow
dotnet build FocusGate.sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "[FAIL] Solution build failed. Aborting publish."
}

# 2. Automated Test Run
Write-Host "`n[2/4] Executing All Unit Tests..." -ForegroundColor Yellow
dotnet test FocusGate.sln -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "[FAIL] Unit tests failed. Aborting publish."
}

# 3. Publish Self-Contained Packages
$DistDir = Join-Path $PSScriptRoot "..\dist"
$HiLinkDist = Join-Path $DistDir "focusgate"
$DashDist = Join-Path $DistDir "focusgate-dashboard"

Write-Host "`n[3/4] Publishing Self-Contained Binaries ($Runtime)..." -ForegroundColor Yellow

Get-Process -Name "FocusGate.HiLink" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

try { if (Test-Path $HiLinkDist) { Remove-Item $HiLinkDist -Recurse -Force -ErrorAction SilentlyContinue } } catch {}
try { if (Test-Path $DashDist) { Remove-Item $DashDist -Recurse -Force -ErrorAction SilentlyContinue } } catch {}

dotnet publish src/FocusGate.HiLink -c $Configuration -r $Runtime --self-contained -o $HiLinkDist --nologo
dotnet publish src/FocusGate.Dashboard -c $Configuration -r $Runtime --self-contained -o $DashDist --nologo

# 4. Merge Dashboard Assets into Gateway dist
Write-Host "`n[4/4] Merging Dashboard Assets into dist/focusgate..." -ForegroundColor Yellow

$DashboardFiles = @(
    "FocusGate.Dashboard.exe",
    "FocusGate.Dashboard.dll",
    "FocusGate.Dashboard.pdb",
    "FocusGate.Dashboard.deps.json",
    "FocusGate.Dashboard.runtimeconfig.json",
    "FocusGate.Dashboard.staticwebassets.endpoints.json",
    "appsettings.json",
    "web.config"
)

foreach ($file in $DashboardFiles) {
    $srcFile = Join-Path $DashDist $file
    if (Test-Path $srcFile) {
        Copy-Item $srcFile $HiLinkDist -Force
    }
}

$DashboardFolders = @("en", "fr", "ar", "wwwroot")
foreach ($folder in $DashboardFolders) {
    $srcFolder = Join-Path $DashDist $folder
    if (Test-Path $srcFolder) {
        $destFolder = Join-Path $HiLinkDist $folder
        Copy-Item $srcFolder $HiLinkDist -Recurse -Force
    }
}

Write-Host "`n======================================================" -ForegroundColor Green
Write-Host " [SUCCESS] FocusGate Published Successfully!         " -ForegroundColor Green
Write-Host " Output Directory: $HiLinkDist                       " -ForegroundColor Green
Write-Host " Ready for deployment to client machines (BERRAR, etc.)" -ForegroundColor Green
Write-Host "======================================================`n" -ForegroundColor Green
