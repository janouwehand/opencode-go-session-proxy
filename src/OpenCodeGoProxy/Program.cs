using OpenCodeGoProxy;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // appsettings.json is copied next to the executable (workspace root -> build output -> publish folder).
    ContentRootPath = AppContext.BaseDirectory,
});

WebApplication app;
try
{
    app = ProxyApp.Build(builder);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

app.Run();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
