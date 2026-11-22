using System.Net;
using Microsoft.Extensions.Logging;

namespace OpenCodeGoProxy;

/// <summary>
/// Local proxy configuration. Everything is read from the trusted local configuration
/// (appsettings.json, environment variables, command line). Nothing is read from the caller.
/// </summary>
public sealed class ProxyOptions
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 8765;
    public const string DefaultUpstreamBaseUrl = "https://opencode.ai/inference/go";

    public required string Host { get; init; }

    /// <summary>Host the server actually binds to. <c>localhost</c> is normalised to 127.0.0.1.</summary>
    public required string BindHost { get; init; }

    public required int Port { get; init; }

    /// <summary>Fixed upstream base URL, without a trailing slash (for example <c>https://opencode.ai/inference/go</c>).</summary>
    public required string UpstreamBaseUrl { get; init; }

    public required Uri UpstreamBaseUri { get; init; }

    public required string SessionMode { get; init; }

    public required string LoggingMode { get; init; }

    public required LogLevel ProxyLogLevel { get; init; }

    /// <summary>Explicitly configured session ID (OPENCODE_GO_SESSION_ID or Proxy:SessionId).</summary>
    public string? SessionId { get; init; }

    /// <summary>Directory where the persistent fallback session is stored.</summary>
    public required string SessionStorePath { get; init; }

    /// <summary>Upstream API key (OPENCODE_GO_API_KEY). Never logged or returned to callers.</summary>
    public string? ApiKey { get; init; }

    public static ProxyOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var host = (configuration["Proxy:Host"] ?? DefaultHost).Trim();
        if (host.Length == 0)
        {
            throw new InvalidOperationException(
                $"Proxy:Host is empty. The proxy must bind to a loopback address such as {DefaultHost}.");
        }

        if (!IsLoopbackHost(host))
        {
            throw new InvalidOperationException(
                $"Proxy:Host '{host}' is not a loopback address. The proxy only binds to localhost " +
                $"({DefaultHost}, ::1 or localhost) and must not be exposed on the LAN.");
        }

        var bindHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? DefaultHost : host;

        var port = DefaultPort;
        var portText = configuration["Proxy:Port"];
        if (!string.IsNullOrWhiteSpace(portText) &&
            (!int.TryParse(portText, out port) || port is < 0 or > 65535))
        {
            throw new InvalidOperationException($"Proxy:Port '{portText}' is not a valid TCP port (0-65535).");
        }

        var upstreamText = (configuration["Proxy:UpstreamBaseUrl"] ?? DefaultUpstreamBaseUrl).Trim().TrimEnd('/');
        if (!Uri.TryCreate(upstreamText, UriKind.Absolute, out var upstreamUri) ||
            (upstreamUri.Scheme != Uri.UriSchemeHttps && upstreamUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"Proxy:UpstreamBaseUrl '{upstreamText}' must be an absolute http or https URL.");
        }

        if (upstreamUri.Scheme == Uri.UriSchemeHttp && !upstreamUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Proxy:UpstreamBaseUrl must use https. Plain http is only allowed for loopback test endpoints.");
        }

        if (upstreamUri.Query.Length > 0 || upstreamUri.Fragment.Length > 0)
        {
            throw new InvalidOperationException("Proxy:UpstreamBaseUrl must not contain a query string or fragment.");
        }

        var loggingMode = (configuration["Proxy:Logging"] ?? "Minimal").Trim();
        var logLevel = loggingMode.ToLowerInvariant() switch
        {
            "minimal" or "info" or "information" => LogLevel.Information,
            "verbose" or "debug" => LogLevel.Debug,
            "quiet" or "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information,
        };

        var apiKey = Trimmed(configuration["OPENCODE_GO_API_KEY"]);
        var sessionId = Trimmed(configuration["OPENCODE_GO_SESSION_ID"]) ?? Trimmed(configuration["Proxy:SessionId"]);
        var storePath = Trimmed(configuration["Proxy:SessionStorePath"]) ?? DefaultSessionStorePath();

        return new ProxyOptions
        {
            Host = host,
            BindHost = bindHost,
            Port = port,
            UpstreamBaseUrl = upstreamText,
            UpstreamBaseUri = upstreamUri,
            SessionMode = (configuration["Proxy:SessionMode"] ?? "Auto").Trim(),
            LoggingMode = loggingMode,
            ProxyLogLevel = logLevel,
            SessionId = sessionId,
            SessionStorePath = storePath,
            ApiKey = apiKey,
        };
    }

    public static string DefaultSessionStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCodeGoProxy");

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
