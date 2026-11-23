using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using OpenCodeGoProxy;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // appsettings.json is copied next to the executable (workspace root -> build output -> publish folder).
    ContentRootPath = AppContext.BaseDirectory,
});

var runningAsService = WindowsServiceHelpers.IsWindowsService();
builder.Services.AddWindowsService(options => options.ServiceName = "OpenCodeGoProxy");

WebApplication app;
try
{
    app = ProxyApp.Build(builder, runningAsService);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    if (runningAsService)
    {
        WriteServiceStartupError(builder, ex.Message);
    }

    return 1;
}

app.Run();
return 0;

/// <summary>
/// A Windows Service has no console, so startup failures are reported to the Event Log and,
/// best effort, to <c>startup-error.log</c> in the configured (or default) log directory.
/// Diagnostics must never mask the original configuration error.
/// </summary>
static void WriteServiceStartupError(WebApplicationBuilder builder, string message)
{
    try
    {
        EventLog.WriteEntry("OpenCodeGoProxy", message, EventLogEntryType.Error);
    }
    catch
    {
        // The service source may not be registered; ignore.
    }

    try
    {
        var logDirectory = builder.Configuration["Proxy:LogDirectory"]?.Trim();
        if (string.IsNullOrEmpty(logDirectory))
        {
            logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OpenCodeGoProxy",
                "logs");
        }

        Directory.CreateDirectory(logDirectory);
        File.AppendAllText(
            Path.Combine(logDirectory, "startup-error.log"),
            $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} Z  {message}{Environment.NewLine}");
    }
    catch
    {
        // Ignore; the Event Log entry above is the primary diagnostic.
    }
}

/// <summary>Entry point marker for WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
