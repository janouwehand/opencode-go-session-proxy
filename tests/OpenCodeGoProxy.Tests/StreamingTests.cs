using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class StreamingTests
{
    [Fact]
    public async Task SseResponse_IsForwardedIncrementally_WithChunkOrderPreserved()
    {
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemainder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var upstream = await MockUpstream.StartAsync();
        upstream.Handler = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: {\"id\":\"first\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            firstChunkWritten.TrySetResult();
            await releaseRemainder.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: {\"id\":\"second\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        };

        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = TestRequests.ChatBody("{\"model\":\"deepseek-v4.1-flash\",\"stream\":true}"),
        };

        using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var firstPart = await ReadUntilAsync(stream, "\"first\"", TimeSpan.FromSeconds(10));

        Assert.Contains("\"first\"", firstPart);
        Assert.False(
            releaseRemainder.Task.IsCompleted,
            "The proxy buffered the SSE response instead of forwarding it incrementally.");

        releaseRemainder.TrySetResult();
        var remainder = await ReadToEndAsync(stream, TimeSpan.FromSeconds(10));
        var everything = firstPart + remainder;

        Assert.Contains("\"second\"", everything);
        Assert.Contains("[DONE]", everything);
        Assert.True(
            everything.IndexOf("\"first\"", StringComparison.Ordinal) <
            everything.IndexOf("\"second\"", StringComparison.Ordinal),
            "SSE chunks arrived out of order.");
    }

    [Fact]
    public async Task ClientCancellation_AbortsTheUpstreamRequest()
    {
        var upstreamAborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var upstream = await MockUpstream.StartAsync();
        upstream.Handler = async context =>
        {
            context.RequestAborted.Register(() => upstreamAborted.TrySetResult());
            upstreamStarted.TrySetResult();
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: {\"open\":true}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Expected when the proxy aborts the upstream request.
            }
        };

        await using var proxy = await ProxyServer.StartAsync(upstream);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = TestRequests.ChatBody("{\"model\":\"deepseek-v4.1-flash\",\"stream\":true}"),
        };

        using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await upstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        response.Dispose();

        await upstreamAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[512];
        var accumulated = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellation.Token);
            if (read == 0)
            {
                return accumulated.ToString();
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (accumulated.ToString().Contains(marker, StringComparison.Ordinal))
            {
                return accumulated.ToString();
            }
        }
    }

    private static async Task<string> ReadToEndAsync(Stream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        var accumulated = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellation.Token);
            if (read == 0)
            {
                return accumulated.ToString();
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}
