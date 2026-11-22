using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace OpenCodeGoProxy;

/// <summary>
/// Forwards an HTTP request to the fixed upstream endpoint and streams the response back
/// without buffering or rewriting the body.
/// </summary>
public sealed class ProxyForwarder
{
    public const string ClientName = "opencode-go-upstream";

    /// <summary>Self-identifying User-Agent. We do not pretend to be the official OpenCode client.</summary>
    public const string UserAgent = "OpenCodeGoProxy/1.0";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    private static readonly HashSet<string> SkippedRequestHeaders = new(HopByHopHeaders, StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Authorization", "x-api-key", "User-Agent", SessionResolver.SessionHeader,
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly SessionResolver _sessions;
    private readonly ProxyOptions _options;
    private readonly ILogger<ProxyForwarder> _logger;

    public ProxyForwarder(
        IHttpClientFactory clientFactory,
        SessionResolver sessions,
        ProxyOptions options,
        ILogger<ProxyForwarder> logger)
    {
        _clientFactory = clientFactory;
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context, string upstreamPath, bool requiresAuthentication = true)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var cancellationToken = context.RequestAborted;
        var request = context.Request;

        var session = _sessions.Resolve(request.Headers);
        _logger.LogInformation("{Method} {Path}", request.Method, request.Path.Value);
        _logger.LogInformation("Session source: {SessionSource}", session.SourceDescription);

        try
        {
            // The upstream model listing is public, so it stays usable without a key locally.
            if (requiresAuthentication && _options.ApiKey is null && !HasClientCredential(request))
            {
                _logger.LogWarning(
                    "Missing API key: OPENCODE_GO_API_KEY is not configured and the client sent no Authorization header.");
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "No API key available. Set OPENCODE_GO_API_KEY on the proxy, or send an Authorization: Bearer header or an x-api-key header.",
                    cancellationToken);
                return;
            }

            using var upstreamRequest = CreateUpstreamRequest(context, upstreamPath, session);
            var client = _clientFactory.CreateClient(ClientName);

            using var upstreamResponse = await client
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var statusCode = (int)upstreamResponse.StatusCode;
            context.Response.StatusCode = statusCode;
            CopyResponseHeaders(upstreamResponse, context.Response);

            _logger.LogInformation("Upstream status: {StatusCode}", statusCode);
            LogUpstreamStatusGuidance(statusCode);

            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
            // Push headers to the client immediately; Kestrel otherwise holds them until the first body write.
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                await using var upstreamBody = await upstreamResponse.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (true)
                {
                    var read = await upstreamBody
                        .ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await context.Response.Body
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    await context.Response.Body
                        .FlushAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Client cancelled the request; upstream request aborted.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Upstream connection failed: {Error}", ex.Message);
            await WriteErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                "Cannot reach the upstream OpenCode Go endpoint. Check network connectivity and Proxy:UpstreamBaseUrl.",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected proxy failure while forwarding {Path}.", request.Path.Value);
            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "The proxy failed while forwarding the request.",
                cancellationToken);
        }
        finally
        {
            _logger.LogInformation(
                "Duration: {Duration}ms",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private HttpRequestMessage CreateUpstreamRequest(
        HttpContext context,
        string upstreamPath,
        SessionResolution session)
    {
        var request = context.Request;
        var message = new HttpRequestMessage(new HttpMethod(request.Method), BuildUpstreamUri(upstreamPath, request.QueryString.Value));

        if (HttpMethods.IsPost(request.Method) || request.ContentLength is > 0)
        {
            var content = new StreamContent(request.Body);
            if (request.ContentLength is { } contentLength)
            {
                content.Headers.ContentLength = contentLength;
            }

            message.Content = content;
        }

        foreach (var header in request.Headers)
        {
            if (SkippedRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!message.Headers.TryAddWithoutValidation(header.Key, values))
            {
                message.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        message.Headers.TryAddWithoutValidation(SessionResolver.SessionHeader, session.SessionId);

        if (_options.ApiKey is { Length: > 0 } apiKey)
        {
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        else if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            message.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
        }
        else if (request.Headers.TryGetValue("x-api-key", out var apiKeyHeader))
        {
            message.Headers.TryAddWithoutValidation("x-api-key", apiKeyHeader.ToArray());
        }

        return message;
    }

    private static bool HasClientCredential(HttpRequest request) =>
        request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("x-api-key");

    /// <summary>
    /// Builds the upstream URI from the trusted base URL plus a fixed relative path.
    /// Callers cannot influence the destination (no arbitrary URL forwarding / SSRF).
    /// </summary>
    private Uri BuildUpstreamUri(string upstreamPath, string? queryString)
    {
        var builder = new StringBuilder(_options.UpstreamBaseUrl);
        if (builder[^1] != '/')
        {
            builder.Append('/');
        }

        builder.Append(upstreamPath);
        if (!string.IsNullOrEmpty(queryString))
        {
            builder.Append(queryString);
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpResponse response)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                response.Headers[header.Key] = new StringValues(header.Value.ToArray());
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                response.Headers[header.Key] = new StringValues(header.Value.ToArray());
            }
        }

        response.Headers.Remove("Transfer-Encoding");
        response.Headers.Remove("Connection");
    }

    private void LogUpstreamStatusGuidance(int statusCode)
    {
        switch (statusCode)
        {
            case StatusCodes.Status400BadRequest:
                _logger.LogWarning(
                    "Upstream rejected the request (400). The upstream error body is forwarded unchanged.");
                break;
            case StatusCodes.Status401Unauthorized:
                _logger.LogWarning(
                    "Upstream authentication failed (401). Check OPENCODE_GO_API_KEY or the forwarded credential.");
                break;
            case StatusCodes.Status403Forbidden:
                _logger.LogWarning(
                    "Upstream denied the request (403). Check OpenCode Go entitlement for this API key.");
                break;
            case StatusCodes.Status429TooManyRequests:
                _logger.LogWarning("Upstream rate limit reached (429). The request was not retried.");
                break;
            case >= 500:
                _logger.LogWarning("Upstream returned a server error ({StatusCode}).", statusCode);
                break;
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        try
        {
            context.Response.Clear();
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            var payload = JsonSerializer.Serialize(new
            {
                error = new { message, type = "proxy_error" },
            });
            await context.Response.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client already gone; nothing left to report to.
        }
        catch (IOException)
        {
            // Connection dropped while writing the error; ignore.
        }
    }
}
