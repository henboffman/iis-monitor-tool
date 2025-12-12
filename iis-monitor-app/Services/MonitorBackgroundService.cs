namespace iis_monitor_app.Services;

public class MonitorBackgroundService : BackgroundService
{
    private readonly IISMonitorService _iisMonitor;
    private readonly ILogger<MonitorBackgroundService> _logger;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public MonitorBackgroundService(
        IISMonitorService iisMonitor,
        ILogger<MonitorBackgroundService> logger,
        IConfiguration config)
    {
        _iisMonitor = iisMonitor;
        _logger = logger;
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IIS Monitor background service starting");

        var intervalSeconds = _config.GetValue<int>("IISMonitor:RefreshIntervalSeconds", 30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSiteHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitor background service");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task CheckSiteHealthAsync()
    {
        var sites = _iisMonitor.GetAllSites();

        foreach (var site in sites.Where(s => s.State == "Started"))
        {
            var binding = site.Bindings.FirstOrDefault(b => b.Protocol.StartsWith("http"));
            if (binding == null) continue;

            var baseUrl = BuildUrl(binding);
            if (string.IsNullOrEmpty(baseUrl)) continue;

            // Check root site
            await CheckEndpointAsync(baseUrl, (isResponding, responseTime, statusCode) =>
                _iisMonitor.UpdateSiteStatus(site.Name, isResponding, responseTime, statusCode),
                site.Name);

            // Check all applications (subsites)
            foreach (var app in site.Applications)
            {
                var appUrl = baseUrl.TrimEnd('/') + app.Path;
                await CheckEndpointAsync(appUrl, (isResponding, responseTime, statusCode) =>
                    _iisMonitor.UpdateAppStatus(site.Name, app.Path, isResponding, responseTime, statusCode),
                    $"{site.Name}{app.Path}");
            }
        }
    }

    private async Task CheckEndpointAsync(string url, Action<bool, long, int?> updateStatus, string name)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url);
            sw.Stop();

            updateStatus(response.IsSuccessStatusCode, sw.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Health check failed for {Name}: {Error}", name, ex.Message);
            updateStatus(false, 0, null);
        }
    }

    private string? BuildUrl(BindingInfo binding)
    {
        // Parse binding info (format: IP:Port:Host or *:Port:Host)
        var parts = binding.BindingInformation.Split(':');
        if (parts.Length < 2) return null;

        var port = parts[1];
        var host = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : "localhost";

        var scheme = binding.Protocol;
        var portSuffix = (scheme == "http" && port == "80") || (scheme == "https" && port == "443")
            ? ""
            : $":{port}";

        return $"{scheme}://{host}{portSuffix}/";
    }

    public override void Dispose()
    {
        _httpClient?.Dispose();
        base.Dispose();
    }
}
