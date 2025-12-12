# Build and Publish Script
# Run this on your development machine

param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "..\publish"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot ".."

Write-Host "=== Building IIS Monitor App ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow

Push-Location $projectPath

try {
    # Clean previous publish
    if (Test-Path $OutputPath) {
        Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
        Remove-Item -Path $OutputPath -Recurse -Force
    }

    # Restore packages
    Write-Host "Restoring packages..." -ForegroundColor Yellow
    dotnet restore

    # Publish
    Write-Host "Publishing..." -ForegroundColor Yellow
    dotnet publish -c $Configuration -o $OutputPath

    Write-Host ""
    Write-Host "=== Build Complete ===" -ForegroundColor Green
    Write-Host "Output: $OutputPath" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "1. Copy the 'publish' folder to your Windows server" -ForegroundColor White
    Write-Host "2. Run deploy.ps1 on the server as Administrator" -ForegroundColor White
}
finally {
    Pop-Location
}
