using System.Net;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class SessionTests
{
    private static readonly KeyValuePair<string, string>[] NoHeaders = [];

    [Fact]
    public async Task MissingSessionHeader_IsAdded()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(proxy);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Matches("^ses_[0-9a-f]{32}$", captured.Session);
    }

    [Fact]
    public async Task ExistingSessionHeader_IsPreserved()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var response = await TestRequests.PostChatAsync(
            proxy,
            [new KeyValuePair<string, string>("x-opencode-session", "ses_client_supplied_value")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("ses_client_supplied_value", captured.Session);
    }

    [Fact]
    public async Task ConversationId_ProducesStableSession()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using (await TestRequests.PostChatAsync(proxy, [Header("x-conversation-id", "conversation-abc")]))
        {
        }

        using (await TestRequests.PostChatAsync(proxy, [Header("x-conversation-id", "conversation-abc")]))
        {
        }

        using (await TestRequests.PostChatAsync(proxy, [Header("x-conversation-id", "conversation-xyz")]))
        {
        }

        var sessions = upstream.Requests.Select(request => request.Session).ToArray();
        Assert.Equal(3, sessions.Length);
        Assert.Matches("^ses_[0-9a-f]{32}$", sessions[0]);
        Assert.Equal(sessions[0], sessions[1]);
        Assert.NotEqual(sessions[0], sessions[2]);
    }

    [Fact]
    public async Task ChatIdHeader_ProducesStableSession()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using (await TestRequests.PostChatAsync(proxy, [Header("x-chat-id", "chat-42")]))
        {
        }

        using (await TestRequests.PostChatAsync(proxy, [Header("x-chat-id", "chat-42")]))
        {
        }

        var sessions = upstream.Requests.Select(request => request.Session).ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal(sessions[0], sessions[1]);
    }

    [Fact]
    public async Task ConversationHeaderTakesPrecedenceOverFallback_ButNotOverClientSession()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(upstream);

        using (await TestRequests.PostChatAsync(proxy, [Header("x-conversation-id", "conversation-with-client-session"),
                   Header("x-opencode-session", "ses_explicit")]))
        {
        }

        var captured = await upstream.WaitForRequestAsync();
        Assert.Equal("ses_explicit", captured.Session);
    }

    [Fact]
    public async Task FallbackSession_SurvivesProxyRestart()
    {
        await using var upstream = await MockUpstream.StartAsync();
        var sessionDirectory = TempDirectory.Create();

        try
        {
            using (await TestRequests.PostChatAsync(await ProxyServer.StartAsync(upstream, sessionDirectory: sessionDirectory)))
            {
            }

            using (await TestRequests.PostChatAsync(await ProxyServer.StartAsync(upstream, sessionDirectory: sessionDirectory)))
            {
            }

            var sessions = upstream.Requests.Select(request => request.Session).ToArray();
            Assert.Equal(2, sessions.Length);
            Assert.Matches("^ses_[0-9a-f]{32}$", sessions[0]);
            Assert.Equal(sessions[0], sessions[1]);
            Assert.True(File.Exists(Path.Combine(sessionDirectory, "session.json")));
        }
        finally
        {
            TempDirectory.TryDelete(sessionDirectory);
        }
    }

    [Fact]
    public async Task ConfiguredSessionId_IsUsedEverywhere()
    {
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(
            upstream,
            new Dictionary<string, string?> { ["OPENCODE_GO_SESSION_ID"] = "ses_configured_session" });

        using (await TestRequests.PostChatAsync(proxy))
        {
        }

        using (await TestRequests.PostChatAsync(proxy))
        {
        }

        Assert.All(upstream.Requests, request => Assert.Equal("ses_configured_session", request.Session));
    }

    [Fact]
    public async Task SessionStore_DoesNotContainApiKey()
    {
        const string apiKey = "oc_sk_test_secret_do_not_store";
        await using var upstream = await MockUpstream.StartAsync();
        await using var proxy = await ProxyServer.StartAsync(
            upstream,
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = apiKey });

        using (await TestRequests.PostChatAsync(proxy))
        {
        }

        var storeFile = Path.Combine(proxy.SessionDirectory, "session.json");
        Assert.True(File.Exists(storeFile));
        Assert.DoesNotContain(apiKey, await File.ReadAllTextAsync(storeFile));
    }

    private static KeyValuePair<string, string> Header(string name, string value) => new(name, value);
}
