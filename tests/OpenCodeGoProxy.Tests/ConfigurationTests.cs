using System.Net;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("example.com")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonLoopbackHost_IsRejected(string host)
    {
        var configuration = Build(("Proxy:Host", host));

        var exception = Assert.Throws<InvalidOperationException>(() => ProxyOptions.FromConfiguration(configuration));
        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public void LoopbackHost_IsAccepted(string host, string expectedBindHost)
    {
        var options = ProxyOptions.FromConfiguration(Build(("Proxy:Host", host)));

        Assert.Equal(expectedBindHost, options.BindHost);
    }

    [Theory]
    [InlineData("http://example.com/inference/go")]
    [InlineData("ftp://opencode.ai/inference/go")]
    [InlineData("not-a-url")]
    public void UntrustedUpstream_IsRejected(string upstream)
    {
        Assert.Throws<InvalidOperationException>(
            () => ProxyOptions.FromConfiguration(Build(("Proxy:UpstreamBaseUrl", upstream))));
    }

    [Fact]
    public void LoopbackHttpUpstream_IsAllowedForTests()
    {
        var options = ProxyOptions.FromConfiguration(
            Build(("Proxy:UpstreamBaseUrl", "http://127.0.0.1:9999/inference/go")));

        Assert.Equal("http://127.0.0.1:9999/inference/go", options.UpstreamBaseUrl);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void InvalidPort_IsRejected(string port)
    {
        Assert.Throws<InvalidOperationException>(
            () => ProxyOptions.FromConfiguration(Build(("Proxy:Port", port))));
    }

    [Fact]
    public void Defaults_MatchTheDocumentedConfiguration()
    {
        var options = ProxyOptions.FromConfiguration(Build());

        Assert.Equal("127.0.0.1", options.BindHost);
        Assert.Equal(8765, options.Port);
        Assert.Equal("https://opencode.ai/inference/go", options.UpstreamBaseUrl);
        Assert.Equal("Auto", options.SessionMode);
    }

    [Fact]
    public void ApiKey_FallsBackToConfiguration()
    {
        var options = ProxyOptions.FromConfiguration(Build(("Proxy:ApiKey", "  config-key  ")));

        Assert.Equal("config-key", options.ApiKey);
    }

    [Fact]
    public void ApiKey_PrefersEnvironmentVariable()
    {
        var options = ProxyOptions.FromConfiguration(
            Build(("OPENCODE_GO_API_KEY", "env-key"), ("Proxy:ApiKey", "config-key")));

        Assert.Equal("env-key", options.ApiKey);
    }

    [Fact]
    public void InteractiveMode_UsesLocalAppDataAndConsoleOnlyLogging()
    {
        var options = ProxyOptions.FromConfiguration(Build());

        Assert.Equal(ProxyOptions.DefaultSessionStorePath(), options.SessionStorePath);
        Assert.Null(options.LogDirectory);
    }

    [Fact]
    public void WindowsServiceDefaults_UseMachineWideDataDirectory()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenCodeGoProxy");

        var options = ProxyOptions.FromConfiguration(Build(), runningAsWindowsService: true);

        Assert.Equal(dataDirectory, options.SessionStorePath);
        Assert.Equal(Path.Combine(dataDirectory, "logs"), options.LogDirectory);
        Assert.EndsWith(Path.Combine("OpenCodeGoProxy", "logs"), options.LogDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitLogDirectory_WinsOverServiceDefault()
    {
        var options = ProxyOptions.FromConfiguration(
            Build(("Proxy:LogDirectory", "  C:\\proxy-logs  ")),
            runningAsWindowsService: true);

        Assert.Equal("C:\\proxy-logs", options.LogDirectory);
    }

    [Fact]
    public void ExplicitSessionStorePath_WinsOverServiceDefault()
    {
        var options = ProxyOptions.FromConfiguration(
            Build(("Proxy:SessionStorePath", "C:\\proxy-sessions")),
            runningAsWindowsService: true);

        Assert.Equal("C:\\proxy-sessions", options.SessionStorePath);
    }

    [Fact]
    public async Task RealServer_BindsOnlyToLoopback()
    {
        await using var proxy = await ProxyServer.StartAsync("http://127.0.0.1:1");

        Assert.StartsWith("http://127.0.0.1:", proxy.BaseUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", proxy.BaseUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("[::]", proxy.BaseUrl, StringComparison.Ordinal);

        using var client = new HttpClient();
        using var response = await client.GetAsync(proxy.BaseUrl + "/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IConfiguration Build(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
