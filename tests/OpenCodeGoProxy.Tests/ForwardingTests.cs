using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class ForwardingTests
{
    [Fact]
    public async Task JsonPayload_IsForwardedByteForByte()
    {
        const string payload = """
            {
              "model":  "deepseek-v4.1-flash",
                  "messages": [ {"role":"user","content":"héllo ✓ 日本"} ],
              "temperature": 0.70,
              "stream": false
            }
            """;

        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy, json: payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(payload), captured.Body);
        Assert.StartsWith("application/json", captured.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task LargePayload_IsForwardedCompletely()
    {
        var payload = "{\"model\":\"deepseek-v4.1-flash\",\"input\":\"" + new string('x', 2_000_000) + "\"}";
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy, json: payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal(payload.Length, Encoding.UTF8.GetByteCount(captured.BodyText));
        Assert.Equal(payload, captured.BodyText);
    }

    [Fact]
    public async Task ChatCompletions_IsForwardedToOpenAiPath()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/inference/go/openai/v1/chat/completions", captured.Path);
    }

    [Fact]
    public async Task Responses_IsForwardedWithoutProtocolTranslation()
    {
        const string payload = "{\"model\":\"deepseek-v4.1-flash\",\"input\":\"hello\",\"stream\":false}";
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy, path: "/v1/responses", json: payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("/inference/go/openai/v1/responses", captured.Path);
        Assert.Equal(payload, captured.BodyText);
    }

    [Fact]
    public async Task Models_IsForwardedToGoModelsPath_WithoutRequiringAnApiKey()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(
            upstream,
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = null });

        using var response = await proxy.Client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("GET", captured.Method);
        Assert.Equal("/inference/go/v1/models", captured.Path);
        Assert.False(captured.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task QueryString_IsPreserved()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(
            proxy,
            path: "/v1/chat/completions?stream=true&api-version=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("?stream=true&api-version=2", captured.QueryString);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task UpstreamErrorResponses_ArePreservedUnchanged(int statusCode)
    {
        await using var upstream = await MockUpstream.StartAsync();
        var errorBody = $"{{\"type\":\"error\",\"error\":{{\"type\":\"UpstreamError\",\"message\":\"status {statusCode}\"}}}}";
        upstream.Handler = async context =>
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers["x-opencode-log-id"] = "log-123";
            await context.Response.WriteAsync(errorBody);
        };

        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.Equal(errorBody, await response.Content.ReadAsStringAsync());
        Assert.Equal("log-123", response.Headers.GetValues("x-opencode-log-id").Single());
    }

    [Fact]
    public async Task UpstreamErrorBody_IsNotRewrittenEvenWhenItIsNotJson()
    {
        await using var upstream = await MockUpstream.StartAsync();
        upstream.Handler = async context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("upstream said no");
        };

        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("upstream said no", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("POST", "/v1/completions")]
    [InlineData("POST", "/v1/embeddings")]
    [InlineData("GET", "/v1/chat/completions")]
    [InlineData("POST", "/v1/models")]
    [InlineData("POST", "/admin/reload")]
    [InlineData("GET", "/")]
    public async Task UnsupportedPaths_AreRejectedLocally(string method, string path)
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = TestRequests.ChatBody();
        }

        using var response = await proxy.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task HealthEndpoint_RespondsWithoutCallingUpstream()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await proxy.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task UpstreamConnectionFailure_ReturnsBadGateway()
    {
        await using var upstream = await MockUpstream.StartAsync();
        var upstreamUrl = upstream.BaseUrl;
        await upstream.DisposeAsync();

        // Point a fresh proxy at the now-closed port to simulate an unreachable upstream.
        await using var proxy = await ProxyServer.StartAsync(upstreamUrl);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("proxy_error", await response.Content.ReadAsStringAsync());
    }
}
