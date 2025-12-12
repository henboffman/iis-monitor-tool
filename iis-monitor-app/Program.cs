using iis_monitor_app.Components;
using iis_monitor_app.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IISMonitorService>();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddSingleton<ScheduledTaskService>();
builder.Services.AddHostedService<MonitorBackgroundService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Always enforce HTTPS
app.UseHsts();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Health endpoint for self-monitoring
app.MapGet("/api/health", () => Results.Ok(new { Status = "healthy", Timestamp = DateTime.UtcNow }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
