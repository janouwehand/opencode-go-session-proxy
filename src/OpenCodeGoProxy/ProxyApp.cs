using System.Net;
using Microsoft.Extensions.Logging.EventLog;
using OpenCodeGoProxy.Logging;

namespace OpenCodeGoProxy;

public static class ProxyUpstreamPaths
{
    public const string ChatCompletions = "openai/v1/chat/completions";
    public const string Responses = "openai/v1/responses";
    public const string Models = "v1/models";
}

/// <summary>
/// Builds the proxy application. Kept separate from Program so tests can construct
/// the exact same pipeline with a substituted configuration and upstream.
/// </summary>
public static class ProxyApp
{
    public const string ChatCompletionsPath = "/v1/chat/completions";
    public const string ResponsesPath = "/v1/responses";
    public const string ModelsPath = "/v1/models";
    public const string HealthPath = "/healthz";

    public static WebApplication Build(WebApplicationBuilder builder, bool runningAsWindowsService = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = ProxyOptions.FromConfiguration(builder.Configuration, runningAsWindowsService);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SessionResolver>();
        builder.Services.AddSingleton<ProxyForwarder>();

        builder.Services
            .AddHttpClient(ProxyForwarder.ClientName, client =>
            {
                // Streaming responses may stay open for a long time; cancellation is driven by the client.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ProxyForwarder.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            });

        if (options.LogDirectory is not null)
        {
            builder.Logging.AddProvider(new FileLoggerProvider(options.LogDirectory));
        }

        builder.Logging.AddFilter("OpenCodeGoProxy", options.ProxyLogLevel);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // A Windows Service also logs to the Event Log; keep it to Warning+ to avoid
        // writing several Information entries per proxied request.
        if (OperatingSystem.IsWindows())
        {
            builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        }

        builder.WebHost.UseUrls($"http://{options.BindHost}:{options.Port}");
        builder.WebHost.ConfigureKestrel(kestrel =>
            // Bodies are streamed, never buffered; raise the default 30 MB cap for large prompts.
            kestrel.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

        var app = builder.Build();
        var forwarder = app.Services.GetRequiredService<ProxyForwarder>();

        app.MapGet(HealthPath, () => Results.Json(new { status = "ok" }));

        app.MapPost(ChatCompletionsPath, (HttpContext context) =>
            forwarder.HandleAsync(context, ProxyUpstreamPaths.ChatCompletions));

        app.MapPost(ResponsesPath, (HttpContext context) =>
            forwarder.HandleAsync(context, ProxyUpstreamPaths.Responses));

        app.MapGet(ModelsPath, (HttpContext context) =>
            forwarder.HandleAsync(context, ProxyUpstreamPaths.Models, requiresAuthentication: false));

        // Everything else is rejected locally; nothing is forwarded to the upstream.
        app.MapFallback(UnsupportedPathAsync);

        app.Lifetime.ApplicationStarted.Register(() =>
            app.Logger.LogInformation(
                "Proxy started on http://{Host}:{Port} -> {Upstream} (session mode: {SessionMode}, session store: {SessionStore}, log directory: {LogDirectory})",
                options.BindHost,
                options.Port,
                options.UpstreamBaseUrl,
                options.SessionMode,
                options.SessionStorePath,
                options.LogDirectory ?? "(console only)"));

        if (!string.Equals(options.SessionMode, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            app.Logger.LogWarning(
                "Proxy:SessionMode '{SessionMode}' is not supported in the MVP; using Auto.",
                options.SessionMode);
        }

        return app;
    }

    private static Task UnsupportedPathAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new
            {
                message =
                    $"Unsupported path '{context.Request.Path}'. Supported endpoints: " +
                    $"POST {ChatCompletionsPath}, POST {ResponsesPath}, GET {ModelsPath}, GET {HealthPath}.",
                type = "proxy_error",
            },
        }));
    }
}
