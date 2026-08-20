#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Honeygain Silent Install + Background Setup for 2 PCs
.DESCRIPTION
    - Downloads Honeygain installer
    - Silent install (no UI)
    - Sets up auto-start on boot via Task Scheduler
    - Runs in background forever
.NOTES
    Run as Administrator on each PC
    Update $HoneygainEmail and $HoneygainPassword before running
#>

# ============ CONFIGURATION ============
$HoneygainEmail    = "YOUR_EMAIL_HERE"        # <-- PUT YOUR HONEYGAIN EMAIL
$HoneygainPassword = "YOUR_PASSWORD_HERE"      # <-- PUT YOUR HONEYGAIN PASSWORD
$DeviceName        = "PC-$(hostname)"          # Unique device name per PC
# =======================================

$ErrorActionPreference = "Stop"
$InstallerUrl = "https://download.honeygain.com/windows-app/Honeygain_install.exe"
$InstallerPath = "$env:TEMP\Honeygain_install.exe"
$InstallDir = "${env:LOCALAPPDATA}\Honeygain"
$TaskName = "Honeygain Background Service"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Honeygain Auto-Setup for $DeviceName" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Download
Write-Host "[1/4] Downloading Honeygain installer..." -ForegroundColor Yellow
if (Test-Path $InstallerPath) { Remove-Item $InstallerPath -Force }
Invoke-WebRequest -Uri $InstallerUrl -OutFile $InstallerPath -UseBasicParsing
$size = (Get-Item $InstallerPath).Length / 1MB
Write-Host "  Downloaded: $([math]::Round($size, 1)) MB" -ForegroundColor Green

# Step 2: Silent Install
Write-Host "[2/4] Installing silently..." -ForegroundColor Yellow
$process = Start-Process -FilePath $InstallerPath -ArgumentList "/SILENT" -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Write-Host "  Installer exit code: $($process.ExitCode)" -ForegroundColor Red
    Write-Host "  If this fails, try /VERYSILENT instead" -ForegroundColor Red
}
Write-Host "  Install complete" -ForegroundColor Green

# Step 3: Find Honeygain executable
Write-Host "[3/4] Locating Honeygain..." -ForegroundColor Yellow
$hgExe = $null
$searchPaths = @(
    "$env:LOCALAPPDATA\Honeygain\Honeygain.exe",
    "$env:LOCALAPPDATA\Programs\Honeygain\Honeygain.exe",
    "C:\Program Files\Honeygain\Honeygain.exe",
    "C:\Program Files (x86)\Honeygain\Honeygain.exe"
)
foreach ($path in $searchPaths) {
    if (Test-Path $path) { $hgExe = $path; break }
}

if (-not $hgExe) {
    # Search registry
    $regPath = Get-ItemProperty -Path "HKCU:\Software\Honeygain UAB\Honeygain" -ErrorAction SilentlyContinue
    if ($regPath -and $regPath.InstallPath) {
        $candidate = Join-Path $regPath.InstallPath "Honeygain.exe"
        if (Test-Path $candidate) { $hgExe = $candidate }
    }
}

if (-not $hgExe) {
    # Last resort: search everywhere
    $hgExe = Get-ChildItem -Path "$env:LOCALAPPDATA" -Recurse -Filter "Honeygain.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}

if ($hgExe) {
    Write-Host "  Found: $hgExe" -ForegroundColor Green
} else {
    Write-Host "  NOT FOUND - check manually after install" -ForegroundColor Red
    Write-Host "  Try: Get-ChildItem -Path `$env:LOCALAPPDATA -Recurse -Filter 'Honeygain.exe'" -ForegroundColor Yellow
}

# Step 4: Create auto-start task (runs at boot, in background, never stops)
Write-Host "[4/4] Creating auto-start task..." -ForegroundColor Yellow

# Remove old task if exists
schtasks /Delete /TN "$TaskName" /F 2>$null

if ($hgExe) {
    # Create scheduled task that starts at user logon, runs in background
    $action = New-ScheduledTaskAction -Execute $hgExe -Argument "-headless"
    $trigger = New-ScheduledTaskTrigger -AtLogon
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Days 365)
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    Write-Host "  Task '$TaskName' created - starts at login" -ForegroundColor Green
} else {
    Write-Host "  Skipped (exe not found) - run this script again after install completes" -ForegroundColor Yellow
}

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Device:    $DeviceName" -ForegroundColor White
Write-Host " Email:     $HoneygainEmail" -ForegroundColor White
Write-Host " Installer: $InstallerPath" -ForegroundColor White
Write-Host ""
Write-Host " NEXT STEPS:" -ForegroundColor Yellow
Write-Host " 1. Open Honeygain from Start Menu or system tray" -ForegroundColor White
Write-Host " 2. Login with: $HoneygainEmail" -ForegroundColor White
Write-Host " 3. Enable 'Share unused bandwidth'" -ForegroundColor White
Write-Host " 4. The task scheduler will auto-start it on every boot" -ForegroundColor White
Write-Host ""
Write-Host " To start manually now:" -ForegroundColor Yellow
if ($hgExe) { Write-Host "   Start-Process '$hgExe' -ArgumentList '-headless'" -ForegroundColor Gray }
Write-Host ""
Write-Host " To check status:" -ForegroundColor Yellow
Write-Host "   schtasks /Query /TN '$TaskName'" -ForegroundColor Gray
Write-Host "   Get-Process Honeygain" -ForegroundColor Gray
