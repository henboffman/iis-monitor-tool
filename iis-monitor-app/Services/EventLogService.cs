using System.Diagnostics;

namespace iis_monitor_app.Services;

public class EventLogService
{
    private readonly ILogger<EventLogService> _logger;
    private readonly IConfiguration _config;
    private List<EventLogEntry> _cachedEntries = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public EventLogService(ILogger<EventLogService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public List<AppEventInfo> GetRecentEvents(int hoursBack = 24)
    {
        var events = new List<AppEventInfo>();

        // Use cache if valid
        if (DateTime.UtcNow - _lastCacheUpdate < _cacheExpiry && _cachedEntries.Count > 0)
        {
            return ConvertToAppEvents(_cachedEntries, hoursBack);
        }

        try
        {
            var sources = _config.GetSection("IISMonitor:EventLogSources").Get<string[]>()
                ?? new[] { "ASP.NET", ".NET Runtime", "Application Error", "IIS-W3SVC-WP" };

            var cutoff = DateTime.Now.AddHours(-hoursBack);

            // Read Application event log
            using var eventLog = new EventLog("Application");
            var entries = new List<EventLogEntry>();

            foreach (EventLogEntry entry in eventLog.Entries)
            {
                if (entry.TimeGenerated < cutoff) continue;
                if (entry.EntryType != EventLogEntryType.Error &&
                    entry.EntryType != EventLogEntryType.Warning) continue;

                // Filter by source
                if (sources.Any(s => entry.Source.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    entry.Source.Contains("w3wp", StringComparison.OrdinalIgnoreCase) ||
                    entry.Source.Contains("ASP", StringComparison.OrdinalIgnoreCase) ||
                    entry.Source.Contains(".NET", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }

            _cachedEntries = entries;
            _lastCacheUpdate = DateTime.UtcNow;
            events = ConvertToAppEvents(entries, hoursBack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading event log");
        }

        return events.OrderByDescending(e => e.TimeGenerated).Take(500).ToList();
    }

    private List<AppEventInfo> ConvertToAppEvents(List<EventLogEntry> entries, int hoursBack)
    {
        var cutoff = DateTime.Now.AddHours(-hoursBack);
        return entries
            .Where(e => e.TimeGenerated >= cutoff)
            .Select(e => new AppEventInfo
            {
                Source = e.Source,
                EventType = e.EntryType.ToString(),
                TimeGenerated = e.TimeGenerated,
                Message = TruncateMessage(e.Message, 500),
                InstanceId = e.InstanceId,
                Category = e.Category
            })
            .ToList();
    }

    public EventSummary GetEventSummary(int hoursBack = 24)
    {
        var events = GetRecentEvents(hoursBack);

        return new EventSummary
        {
            TotalErrors = events.Count(e => e.EventType == "Error"),
            TotalWarnings = events.Count(e => e.EventType == "Warning"),
            ErrorsBySource = events
                .Where(e => e.EventType == "Error")
                .GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => g.Count()),
            WarningsBySource = events
                .Where(e => e.EventType == "Warning")
                .GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => g.Count()),
            RecentEvents = events.Take(20).ToList()
        };
    }

    private string TruncateMessage(string message, int maxLength)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        return message.Length <= maxLength ? message : message[..maxLength] + "...";
    }
}

public class AppEventInfo
{
    public string Source { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime TimeGenerated { get; set; }
    public string Message { get; set; } = string.Empty;
    public long InstanceId { get; set; }
    public string Category { get; set; } = string.Empty;
}

public class EventSummary
{
    public int TotalErrors { get; set; }
    public int TotalWarnings { get; set; }
    public Dictionary<string, int> ErrorsBySource { get; set; } = new();
    public Dictionary<string, int> WarningsBySource { get; set; } = new();
    public List<AppEventInfo> RecentEvents { get; set; } = new();
}
