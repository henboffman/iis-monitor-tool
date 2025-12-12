# Install as Sub-Application under Default Web Site
# Run this on the target Windows server

param(
    [string]$AppName = "iis-monitor",
    [string]$AppPoolName = "IISMonitorPool",
    [string]$PhysicalPath = "C:\inetpub\wwwroot\iis-monitor"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Installing IIS Monitor as Sub-Application ===" -ForegroundColor Cyan

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator" -ForegroundColor Red
    exit 1
}

Import-Module WebAdministration

# Create physical directory
Write-Host "Creating directory: $PhysicalPath" -ForegroundColor Yellow
if (-not (Test-Path $PhysicalPath)) {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

# Copy published files
$publishPath = Join-Path $PSScriptRoot "..\publish"
if (Test-Path $publishPath) {
    Write-Host "Copying published files..." -ForegroundColor Yellow
    Copy-Item -Path "$publishPath\*" -Destination $PhysicalPath -Recurse -Force
}

# Create App Pool
Write-Host "Creating App Pool: $AppPoolName" -ForegroundColor Yellow
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""

# Create Application under Default Web Site
Write-Host "Creating application: /$AppName" -ForegroundColor Yellow
$existingApp = Get-WebApplication -Site "Default Web Site" -Name $AppName -ErrorAction SilentlyContinue
if ($existingApp) {
    Remove-WebApplication -Site "Default Web Site" -Name $AppName
}

New-WebApplication -Site "Default Web Site" `
    -Name $AppName `
    -PhysicalPath $PhysicalPath `
    -ApplicationPool $AppPoolName

Write-Host ""
Write-Host "=== Installation Complete ===" -ForegroundColor Green
Write-Host "Access the app at: http://localhost/$AppName" -ForegroundColor Cyan
