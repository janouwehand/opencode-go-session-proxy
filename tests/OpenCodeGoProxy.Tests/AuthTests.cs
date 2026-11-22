using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class AuthTests
{
    private static readonly Dictionary<string, string?> NoApiKey = new()
    {
        ["OPENCODE_GO_API_KEY"] = null,
    };

    [Fact]
    public async Task CallerAuthorization_IsForwarded()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream, NoApiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = TestRequests.ChatBody(),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-key-123");

        using var response = await proxy.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("Bearer caller-key-123", captured.Headers.Authorization.ToString());
    }

    [Fact]
    public async Task ConfiguredApiKey_OverridesCallerCredential()
    {
        const string proxyKey = "oc_sk_proxy_configured_key";
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(
            upstream,
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = proxyKey });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = TestRequests.ChatBody(),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-placeholder");

        using var response = await proxy.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal($"Bearer {proxyKey}", captured.Headers.Authorization.ToString());
        Assert.DoesNotContain(proxyKey, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingApiKey_ReturnsLocalUnauthorized_AndDoesNotCallUpstream()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream, NoApiKey);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(upstream.Requests);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("OPENCODE_GO_API_KEY", message);
        Assert.Contains("Authorization", message);
    }

    [Fact]
    public async Task CallerXApiKey_IsForwarded_WhenNoConfiguredKeyExists()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream, NoApiKey);

        using var response = await TestRequests.PostChatAsync(
            proxy,
            [new KeyValuePair<string, string>("x-api-key", "caller-x-api-key")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("caller-x-api-key", captured.Headers["x-api-key"].ToString());
        Assert.False(captured.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ConfiguredApiKey_ReplacesCallerXApiKey()
    {
        const string proxyKey = "oc_sk_proxy_configured_key";
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(
            upstream,
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = proxyKey });

        using var response = await TestRequests.PostChatAsync(
            proxy,
            [new KeyValuePair<string, string>("x-api-key", "caller-placeholder")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal($"Bearer {proxyKey}", captured.Headers.Authorization.ToString());
        Assert.False(captured.Headers.ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task UpstreamSeesProxyUserAgent_NotTheCallers()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = TestRequests.ChatBody(),
        };
        request.Headers.UserAgent.ParseAdd("SomeThirdPartyClient/9.9");

        using var response = await proxy.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal(ProxyForwarder.UserAgent, captured.Headers.UserAgent.ToString());
    }
}
