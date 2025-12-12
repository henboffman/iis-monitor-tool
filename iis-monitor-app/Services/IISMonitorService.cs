using Microsoft.Web.Administration;
using System.Collections.Concurrent;

namespace iis_monitor_app.Services;

public class IISMonitorService
{
    private readonly ILogger<IISMonitorService> _logger;
    private readonly ConcurrentDictionary<string, SiteStatus> _siteStatuses = new();
    private readonly ConcurrentDictionary<string, AppStatus> _appStatuses = new();
    private readonly ConcurrentDictionary<string, List<StatusHistoryEntry>> _siteHistory = new();
    private readonly ConcurrentDictionary<string, List<StatusHistoryEntry>> _appHistory = new();
    private const int MaxHistoryEntries = 100; // Keep last 100 checks per site/app

    public IISMonitorService(ILogger<IISMonitorService> logger)
    {
        _logger = logger;
    }

    public List<SiteInfo> GetAllSites()
    {
        var sites = new List<SiteInfo>();

        try
        {
            using var serverManager = new ServerManager();

            foreach (var site in serverManager.Sites)
            {
                var siteInfo = new SiteInfo
                {
                    Id = site.Id,
                    Name = site.Name,
                    State = site.State.ToString(),
                    Bindings = site.Bindings.Select(b => new BindingInfo
                    {
                        Protocol = b.Protocol,
                        BindingInformation = b.BindingInformation,
                        Host = b.Host
                    }).ToList(),
                    PhysicalPath = site.Applications["/"]?.VirtualDirectories["/"]?.PhysicalPath ?? "N/A",
                    AppPoolName = site.Applications["/"]?.ApplicationPoolName ?? "N/A",
                    Applications = new List<ApplicationInfo>()
                };

                // Get all applications (subsites) under this site
                foreach (var app in site.Applications)
                {
                    if (app.Path == "/") continue; // Skip root app

                    var appKey = $"{site.Name}:{app.Path}";
                    var appInfo = new ApplicationInfo
                    {
                        Path = app.Path,
                        SiteName = site.Name,
                        PhysicalPath = app.VirtualDirectories["/"]?.PhysicalPath ?? "N/A",
                        AppPoolName = app.ApplicationPoolName,
                        EnabledProtocols = app.EnabledProtocols
                    };

                    // Get status from cache
                    if (_appStatuses.TryGetValue(appKey, out var appStatus))
                    {
                        appInfo.LastChecked = appStatus.LastChecked;
                        appInfo.IsResponding = appStatus.IsResponding;
                        appInfo.ResponseTimeMs = appStatus.ResponseTimeMs;
                        appInfo.HttpStatusCode = appStatus.HttpStatusCode;
                        appInfo.ErrorMessage = appStatus.ErrorMessage;
                    }

                    siteInfo.Applications.Add(appInfo);
                }

                // Get status from cache
                if (_siteStatuses.TryGetValue(site.Name, out var status))
                {
                    siteInfo.LastChecked = status.LastChecked;
                    siteInfo.IsResponding = status.IsResponding;
                    siteInfo.ResponseTimeMs = status.ResponseTimeMs;
                }

                sites.Add(siteInfo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IIS sites");
        }

        return sites;
    }

    public List<AppPoolInfo> GetAllAppPools()
    {
        var pools = new List<AppPoolInfo>();

        try
        {
            using var serverManager = new ServerManager();

            foreach (var pool in serverManager.ApplicationPools)
            {
                var poolInfo = new AppPoolInfo
                {
                    Name = pool.Name,
                    State = pool.State.ToString(),
                    ManagedRuntimeVersion = pool.ManagedRuntimeVersion ?? "No Managed Code",
                    ManagedPipelineMode = pool.ManagedPipelineMode.ToString(),
                    Enable32BitAppOnWin64 = pool.Enable32BitAppOnWin64,
                    StartMode = pool.StartMode.ToString(),
                    AutoStart = pool.AutoStart,
                    ProcessModel = new ProcessModelInfo
                    {
                        IdentityType = pool.ProcessModel.IdentityType.ToString(),
                        IdleTimeout = pool.ProcessModel.IdleTimeout,
                        MaxProcesses = pool.ProcessModel.MaxProcesses
                    },
                    Recycling = new RecyclingInfo
                    {
                        RegularTimeInterval = pool.Recycling.PeriodicRestart.Time,
                        PrivateMemory = pool.Recycling.PeriodicRestart.PrivateMemory,
                        Requests = pool.Recycling.PeriodicRestart.Requests
                    }
                };

                // Get worker process info if running
                if (pool.State == ObjectState.Started)
                {
                    try
                    {
                        foreach (var wp in pool.WorkerProcesses)
                        {
                            poolInfo.WorkerProcesses.Add(new WorkerProcessInfo
                            {
                                ProcessId = wp.ProcessId,
                                State = wp.State.ToString(),
                                StartTime = GetProcessStartTime(wp.ProcessId)
                            });
                        }
                    }
                    catch { }
                }

                pools.Add(poolInfo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting app pools");
        }

        return pools;
    }

    public Dictionary<string, List<ConfigDifference>> CompareConfigurations()
    {
        var differences = new Dictionary<string, List<ConfigDifference>>();

        try
        {
            var pools = GetAllAppPools();
            var basePool = pools.FirstOrDefault();
            if (basePool == null) return differences;

            foreach (var pool in pools.Skip(1))
            {
                var diffs = new List<ConfigDifference>();

                if (pool.ManagedRuntimeVersion != basePool.ManagedRuntimeVersion)
                    diffs.Add(new ConfigDifference("Runtime Version", basePool.ManagedRuntimeVersion, pool.ManagedRuntimeVersion));

                if (pool.ManagedPipelineMode != basePool.ManagedPipelineMode)
                    diffs.Add(new ConfigDifference("Pipeline Mode", basePool.ManagedPipelineMode, pool.ManagedPipelineMode));

                if (pool.Enable32BitAppOnWin64 != basePool.Enable32BitAppOnWin64)
                    diffs.Add(new ConfigDifference("32-bit Mode", basePool.Enable32BitAppOnWin64.ToString(), pool.Enable32BitAppOnWin64.ToString()));

                if (pool.ProcessModel.IdentityType != basePool.ProcessModel.IdentityType)
                    diffs.Add(new ConfigDifference("Identity Type", basePool.ProcessModel.IdentityType, pool.ProcessModel.IdentityType));

                if (pool.ProcessModel.IdleTimeout != basePool.ProcessModel.IdleTimeout)
                    diffs.Add(new ConfigDifference("Idle Timeout", basePool.ProcessModel.IdleTimeout.ToString(), pool.ProcessModel.IdleTimeout.ToString()));

                if (diffs.Count > 0)
                    differences[pool.Name] = diffs;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing configurations");
        }

        return differences;
    }

    public void UpdateSiteStatus(string siteName, bool isResponding, long responseTimeMs, int? httpStatusCode = null, string? errorMessage = null)
    {
        var timestamp = DateTime.UtcNow;

        _siteStatuses[siteName] = new SiteStatus
        {
            IsResponding = isResponding,
            ResponseTimeMs = responseTimeMs,
            HttpStatusCode = httpStatusCode,
            ErrorMessage = errorMessage,
            LastChecked = timestamp
        };

        // Add to history
        AddToHistory(_siteHistory, siteName, new StatusHistoryEntry
        {
            Timestamp = timestamp,
            IsResponding = isResponding,
            ResponseTimeMs = responseTimeMs,
            HttpStatusCode = httpStatusCode,
            ErrorMessage = errorMessage
        });
    }

    public void UpdateAppStatus(string siteName, string appPath, bool isResponding, long responseTimeMs, int? httpStatusCode = null, string? errorMessage = null)
    {
        var appKey = $"{siteName}:{appPath}";
        var timestamp = DateTime.UtcNow;

        _appStatuses[appKey] = new AppStatus
        {
            IsResponding = isResponding,
            ResponseTimeMs = responseTimeMs,
            HttpStatusCode = httpStatusCode,
            ErrorMessage = errorMessage,
            LastChecked = timestamp
        };

        // Add to history
        AddToHistory(_appHistory, appKey, new StatusHistoryEntry
        {
            Timestamp = timestamp,
            IsResponding = isResponding,
            ResponseTimeMs = responseTimeMs,
            HttpStatusCode = httpStatusCode,
            ErrorMessage = errorMessage
        });
    }

    private void AddToHistory(ConcurrentDictionary<string, List<StatusHistoryEntry>> history, string key, StatusHistoryEntry entry)
    {
        history.AddOrUpdate(key,
            _ => new List<StatusHistoryEntry> { entry },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(entry);
                    // Keep only the last N entries
                    if (list.Count > MaxHistoryEntries)
                    {
                        list.RemoveAt(0);
                    }
                }
                return list;
            });
    }

    public List<StatusHistoryEntry> GetSiteHistory(string siteName)
    {
        return _siteHistory.TryGetValue(siteName, out var history)
            ? history.OrderByDescending(h => h.Timestamp).ToList()
            : new List<StatusHistoryEntry>();
    }

    public List<StatusHistoryEntry> GetAppHistory(string siteName, string appPath)
    {
        var appKey = $"{siteName}:{appPath}";
        return _appHistory.TryGetValue(appKey, out var history)
            ? history.OrderByDescending(h => h.Timestamp).ToList()
            : new List<StatusHistoryEntry>();
    }

    public SiteInfo? GetSiteByName(string siteName)
    {
        return GetAllSites().FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
    }

    public ApplicationInfo? GetApplication(string siteName, string appPath)
    {
        return GetAllApplications().FirstOrDefault(a =>
            a.SiteName.Equals(siteName, StringComparison.OrdinalIgnoreCase) &&
            a.Path.Equals(appPath, StringComparison.OrdinalIgnoreCase));
    }

    public HealthSummary GetSiteHealthSummary(string siteName)
    {
        var history = GetSiteHistory(siteName);
        return CalculateHealthSummary(history);
    }

    public HealthSummary GetAppHealthSummary(string siteName, string appPath)
    {
        var history = GetAppHistory(siteName, appPath);
        return CalculateHealthSummary(history);
    }

    private HealthSummary CalculateHealthSummary(List<StatusHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return new HealthSummary();
        }

        var last24Hours = history.Where(h => h.Timestamp > DateTime.UtcNow.AddHours(-24)).ToList();
        var successCount = last24Hours.Count(h => h.IsResponding);
        var totalCount = last24Hours.Count;

        return new HealthSummary
        {
            UptimePercentage = totalCount > 0 ? (double)successCount / totalCount * 100 : 0,
            TotalChecks = totalCount,
            SuccessfulChecks = successCount,
            FailedChecks = totalCount - successCount,
            AverageResponseTimeMs = last24Hours.Where(h => h.IsResponding && h.ResponseTimeMs > 0).Select(h => h.ResponseTimeMs).DefaultIfEmpty(0).Average(),
            LastDowntime = history.Where(h => !h.IsResponding).OrderByDescending(h => h.Timestamp).FirstOrDefault()?.Timestamp,
            RecentErrors = history.Where(h => !h.IsResponding).Take(10).ToList()
        };
    }

    public List<ApplicationInfo> GetAllApplications()
    {
        var apps = new List<ApplicationInfo>();
        var sites = GetAllSites();

        foreach (var site in sites)
        {
            foreach (var app in site.Applications)
            {
                // Copy site binding info to app for URL building
                app.SiteBindings = site.Bindings;
                app.SiteState = site.State;
                apps.Add(app);
            }
        }

        return apps;
    }

    private DateTime? GetProcessStartTime(int processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }
}

// Data models
public class SiteInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public List<BindingInfo> Bindings { get; set; } = new();
    public string PhysicalPath { get; set; } = string.Empty;
    public string AppPoolName { get; set; } = string.Empty;
    public List<ApplicationInfo> Applications { get; set; } = new();
    public DateTime? LastChecked { get; set; }
    public bool IsResponding { get; set; }
    public long ResponseTimeMs { get; set; }
}

public class BindingInfo
{
    public string Protocol { get; set; } = string.Empty;
    public string BindingInformation { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
}

public class ApplicationInfo
{
    public string Path { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string SiteState { get; set; } = string.Empty;
    public string PhysicalPath { get; set; } = string.Empty;
    public string AppPoolName { get; set; } = string.Empty;
    public string EnabledProtocols { get; set; } = string.Empty;
    public List<BindingInfo> SiteBindings { get; set; } = new();
    public DateTime? LastChecked { get; set; }
    public bool IsResponding { get; set; }
    public long ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AppPoolInfo
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ManagedRuntimeVersion { get; set; } = string.Empty;
    public string ManagedPipelineMode { get; set; } = string.Empty;
    public bool Enable32BitAppOnWin64 { get; set; }
    public string StartMode { get; set; } = string.Empty;
    public bool AutoStart { get; set; }
    public ProcessModelInfo ProcessModel { get; set; } = new();
    public RecyclingInfo Recycling { get; set; } = new();
    public List<WorkerProcessInfo> WorkerProcesses { get; set; } = new();
}

public class ProcessModelInfo
{
    public string IdentityType { get; set; } = string.Empty;
    public TimeSpan IdleTimeout { get; set; }
    public long MaxProcesses { get; set; }
}

public class RecyclingInfo
{
    public TimeSpan RegularTimeInterval { get; set; }
    public long PrivateMemory { get; set; }
    public long Requests { get; set; }
}

public class WorkerProcessInfo
{
    public int ProcessId { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }
}

public class SiteStatus
{
    public bool IsResponding { get; set; }
    public long ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastChecked { get; set; }
}

public class AppStatus
{
    public bool IsResponding { get; set; }
    public long ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastChecked { get; set; }
}

public class StatusHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public bool IsResponding { get; set; }
    public long ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class HealthSummary
{
    public double UptimePercentage { get; set; }
    public int TotalChecks { get; set; }
    public int SuccessfulChecks { get; set; }
    public int FailedChecks { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public DateTime? LastDowntime { get; set; }
    public List<StatusHistoryEntry> RecentErrors { get; set; } = new();
}

public class ConfigDifference
{
    public string Setting { get; set; }
    public string BaseValue { get; set; }
    public string CurrentValue { get; set; }

    public ConfigDifference(string setting, string baseValue, string currentValue)
    {
        Setting = setting;
        BaseValue = baseValue;
        CurrentValue = currentValue;
    }
}
