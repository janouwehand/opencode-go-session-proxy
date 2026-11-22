using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenCodeGoProxy.Tests;

internal sealed class CapturedRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string QueryString { get; init; }

    public required IHeaderDictionary Headers { get; init; }

    public required byte[] Body { get; init; }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public string Session => Headers["x-opencode-session"].ToString();
}

/// <summary>
/// Minimal OpenAI-compatible upstream stand-in used by the proxy tests.
/// Captures every request and lets individual tests override the response.
/// </summary>
internal sealed class MockUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<CapturedRequest> _requests = [];
    private readonly SemaphoreSlim _received = new(0);

    private MockUpstream(WebApplication app) => _app = app;

    public string ServerUrl { get; private set; } = string.Empty;

    /// <summary>Same shape as the real upstream base: server root + /inference/go.</summary>
    public string BaseUrl => ServerUrl + "/inference/go";

    public Func<HttpContext, Task> Handler { get; set; } = DefaultHandlerAsync;

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public CapturedRequest LastRequest => Requests[^1];

    public static async Task<MockUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("PROXY_TEST_LOGS") == "1")
        {
            builder.Logging.AddSimpleConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
        }

        var app = builder.Build();
        var mock = new MockUpstream(app);
        app.Run(mock.HandleAsync);

        await app.StartAsync();
        mock.ServerUrl = app.Urls.First();
        return mock;
    }

    public async Task<CapturedRequest> WaitForRequestAsync(int count = 1, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var requests = Requests;
            if (requests.Count >= count)
            {
                return requests[count - 1];
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _received.WaitAsync(remaining))
            {
                throw new TimeoutException(
                    $"Expected at least {count} upstream request(s), received {requests.Count}.");
            }
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        if ((context.Request.ContentLength is > 0) ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            await context.Request.Body.CopyToAsync(buffer);
        }

        var headers = new HeaderDictionary();
        foreach (var header in context.Request.Headers)
        {
            headers[header.Key] = header.Value;
        }

        var captured = new CapturedRequest
        {
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            QueryString = context.Request.QueryString.Value ?? string.Empty,
            Headers = headers,
            Body = buffer.ToArray(),
        };

        lock (_gate)
        {
            _requests.Add(captured);
        }

        _received.Release();

        await Handler(context);
    }

    private static async Task DefaultHandlerAsync(HttpContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"ok\":true}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _received.Dispose();
    }
}

internal static class ProxyServerDefaults
{
    public const string ApiKey = "test-suite-api-key";
}

/// <summary>Runs the real proxy app on an ephemeral localhost port.</summary>
internal sealed class ProxyServer : IAsyncDisposable
{
    private ProxyServer(WebApplication app, HttpClient client, string baseUrl, string sessionDirectory, bool ownsDirectory)
    {
        App = app;
        Client = client;
        BaseUrl = baseUrl;
        SessionDirectory = sessionDirectory;
        OwnsDirectory = ownsDirectory;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public string BaseUrl { get; }

    public string SessionDirectory { get; }

    private bool OwnsDirectory { get; }

    public static Task<ProxyServer> StartAsync(
        MockUpstream upstream,
        IDictionary<string, string?>? settings = null,
        string? sessionDirectory = null) =>
        StartAsync(upstream.BaseUrl, settings, sessionDirectory);

    public static Task<ProxyServer> StartAsync(
        string upstreamBaseUrl,
        IDictionary<string, string?>? settings = null,
        string? sessionDirectory = null)
    {
        var ownsDirectory = sessionDirectory is null;
        var directory = sessionDirectory ?? TempDirectory.Create();
        return StartCoreAsync(upstreamBaseUrl, settings, directory, ownsDirectory);
    }

    private static async Task<ProxyServer> StartCoreAsync(
        string upstreamBaseUrl,
        IDictionary<string, string?>? settings,
        string directory,
        bool ownsDirectory)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("PROXY_TEST_LOGS") == "1")
        {
            builder.Logging.AddSimpleConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
        }

        var configuration = new Dictionary<string, string?>
        {
            ["Proxy:Host"] = "127.0.0.1",
            ["Proxy:Port"] = "0",
            ["Proxy:UpstreamBaseUrl"] = upstreamBaseUrl,
            ["Proxy:SessionStorePath"] = directory,
            ["Proxy:Logging"] = "Quiet",
            // Explicitly shadow any developer-machine environment variables with a test-only key.
            ["OPENCODE_GO_API_KEY"] = ProxyServerDefaults.ApiKey,
            ["OPENCODE_GO_SESSION_ID"] = null,
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                configuration[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);

        var app = ProxyApp.Build(builder);
        await app.StartAsync();

        var baseUrl = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return new ProxyServer(app, client, baseUrl, directory, ownsDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();

        if (OwnsDirectory)
        {
            TempDirectory.TryDelete(SessionDirectory);
        }
    }
}

internal static class TempDirectory
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "opencodego-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}

internal static class TestRequests
{
    public const string ChatJson = "{\"model\":\"deepseek-v4.1-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    public static StringContent ChatBody(string json = ChatJson) =>
        new(json, Encoding.UTF8, "application/json");

    public static Task<HttpResponseMessage> PostChatAsync(
        ProxyServer proxy,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        string path = "/v1/chat/completions",
        string json = ChatJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = ChatBody(json),
        };

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return proxy.Client.SendAsync(request);
    }
}
