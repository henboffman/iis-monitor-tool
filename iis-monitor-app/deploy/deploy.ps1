# IIS Monitor App Deployment Script
# Run this on the target Windows server

param(
    [string]$SiteName = "IISMonitor",
    [string]$AppPoolName = "IISMonitorPool",
    [string]$Port = "8080",
    [string]$PhysicalPath = "C:\inetpub\iis-monitor-app"
)

$ErrorActionPreference = "Stop"

Write-Host "=== IIS Monitor Deployment ===" -ForegroundColor Cyan

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator" -ForegroundColor Red
    exit 1
}

# Import IIS module
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (-not (Get-Module WebAdministration)) {
    Write-Host "ERROR: IIS PowerShell module not found. Is IIS installed?" -ForegroundColor Red
    exit 1
}

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
} else {
    Write-Host "WARNING: No publish folder found. Run 'dotnet publish' first." -ForegroundColor Yellow
}

# Create App Pool
Write-Host "Creating App Pool: $AppPoolName" -ForegroundColor Yellow
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}

# Configure App Pool
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""  # No managed code (runs via aspNetCore module)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "startMode" -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.idleTimeout" -Value "00:00:00"

# Create Website
Write-Host "Creating Website: $SiteName on port $Port" -ForegroundColor Yellow
if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {
    Write-Host "Removing existing site..." -ForegroundColor Yellow
    Remove-Website -Name $SiteName
}

New-Website -Name $SiteName `
    -PhysicalPath $PhysicalPath `
    -ApplicationPool $AppPoolName `
    -Port $Port `
    -Force | Out-Null

# Start the site
Start-Website -Name $SiteName
Start-WebAppPool -Name $AppPoolName

Write-Host ""
Write-Host "=== Deployment Complete ===" -ForegroundColor Green
Write-Host "Site URL: http://localhost:$Port" -ForegroundColor Cyan
Write-Host ""
Write-Host "Note: The app needs to run with admin privileges to read IIS config and Event Logs." -ForegroundColor Yellow
Write-Host "Consider setting the App Pool identity to a user with appropriate permissions." -ForegroundColor Yellow
