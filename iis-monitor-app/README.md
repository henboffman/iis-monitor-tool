# IIS Monitor App

A Blazor Server application that monitors IIS websites and application pools, displaying health status, event log errors/warnings, and configuration differences.

## Features

- **Dashboard Overview**: Quick view of all sites, app pools, and recent errors
- **Sites View**: All IIS sites with bindings, physical paths, and sub-applications
- **App Pools View**: Detailed app pool configuration, worker processes, and uptime
- **Event Log**: Errors and warnings from Windows Event Log (ASP.NET, .NET Runtime, IIS)
- **Config Comparison**: Compare app pool configurations to identify inconsistencies
- **Health Monitoring**: Background service that checks site responsiveness

## Requirements

- Windows Server with IIS installed
- .NET 8.0 Runtime
- Administrator privileges (for reading IIS config and Event Logs)

## Quick Start

### 1. Build the App

On your development machine:

```powershell
cd deploy
.\publish.ps1
```

Or manually:

```bash
dotnet publish -c Release -o publish
```

### 2. Deploy to Server

Copy the `publish` folder to your Windows server, then run:

**Option A: Standalone Site (recommended)**
```powershell
# Run as Administrator
.\deploy\deploy.ps1 -Port 8080
```
Access at: `http://localhost:8080`

**Option B: Sub-application under Default Web Site**
```powershell
# Run as Administrator
.\deploy\install-as-subsite.ps1 -AppName "iis-monitor"
```
Access at: `http://localhost/iis-monitor`

## Configuration

Edit `appsettings.json` to customize:

```json
{
  "IISMonitor": {
    "RefreshIntervalSeconds": 30,
    "EventLogHoursToSearch": 24,
    "EventLogSources": ["ASP.NET", ".NET Runtime", "Application Error", "IIS-W3SVC-WP"]
  }
}
```

## Permissions

The app needs elevated permissions to:
- Read IIS configuration (Microsoft.Web.Administration)
- Read Windows Event Logs
- Query worker process information

**Recommended**: Run the app pool under an identity with:
- Local Administrator group membership, OR
- Read access to IIS configuration and Event Log

## Structure

```
iis-monitor-app/
├── Components/
│   ├── Layout/
│   │   └── MainLayout.razor
│   └── Pages/
│       ├── Home.razor         # Dashboard
│       ├── Sites.razor        # Sites list
│       ├── AppPools.razor     # App pools detail
│       ├── Events.razor       # Event log viewer
│       └── Config.razor       # Config comparison
├── Services/
│   ├── IISMonitorService.cs   # IIS API integration
│   ├── EventLogService.cs     # Event log reader
│   └── MonitorBackgroundService.cs
├── deploy/
│   ├── deploy.ps1             # Standalone deployment
│   ├── install-as-subsite.ps1 # Sub-app deployment
│   └── publish.ps1            # Build script
└── wwwroot/
    └── css/app.css
```

## Troubleshooting

### "Access Denied" errors
- Ensure the app pool identity has admin rights
- Check that the app is running with elevated privileges

### Event Log is empty
- Verify the Event Log sources in appsettings.json
- Check Windows Event Log permissions

### Sites show as "Down"
- Health checks require sites to be accessible from localhost
- Check firewall rules and binding configurations
