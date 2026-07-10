using System.Net;
using System.Text;
using System.Text.Json;
using DeadAir.Core.Cleanup;
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        try
        {
            return Task.FromResult(respond(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}

file sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest;
    public string? LastBody;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? null : await request.Content.ReadAsStringAsync(ct);
        return response;
    }
}

/// <summary>Content whose stream never produces a line and never completes on its own —
/// simulates a stalled/hanging Ollama body so the read must be bounded by the caller's
/// timeout rather than hanging forever (HttpClient.Timeout does not apply to body reads
/// once ResponseHeadersRead completes the SendAsync call).</summary>
file sealed class HangingStreamContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.Delay(Timeout.Infinite);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new HangingStream());

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
        CreateContentReadStreamAsync();

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct); // never returns unless cancelled
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class OllamaClientTests
{
    private static AppConfig Cfg() => new();

    [Fact]
    public async Task ShortTranscript_SkipsLlm()
    {
        var handler = new StubHandler(_ => throw new Exception("must not call"));
        var client = new OllamaClient(Cfg(), handler);
        var r = await client.CleanAsync("hi there", CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal("hi there", r.Text);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Success_AccumulatesStreamedResponse()
    {
        var ndjson =
            "{\"response\":\"Hello\",\"done\":false}\n" +
            "{\"response\":\" world.\",\"done\":true}\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ndjson, Encoding.UTF8) });
        var client = new OllamaClient(Cfg(), handler);
        var longText = new string('x', 60);
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        Assert.False(r.Skipped);
        Assert.Equal("Hello world.", r.Text);
    }

    [Fact]
    public async Task HttpFailure_ReturnsRawTranscript()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var client = new OllamaClient(Cfg(), handler);
        var longText = new string('x', 60);
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal(longText, r.Text);
        Assert.Contains("down", r.Reason);
    }

    [Fact]
    public async Task HttpTimeout_ReturnsRawTranscript()
    {
        // HttpClient timeout surfaces as TaskCanceledException with the caller's ct un-cancelled
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        var client = new OllamaClient(Cfg(), handler);
        var longText = new string('x', 60);
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal(longText, r.Text);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var client = new OllamaClient(Cfg(), handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var longText = new string('x', 60);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CleanAsync(longText, CleanupMode.Faithful, cts.Token));
    }

    [Fact]
    public async Task HangingBodyStream_BoundedByOllamaTimeout_ReturnsRawTranscript()
    {
        // ResponseHeadersRead completes SendAsync as soon as headers arrive, so
        // HttpClient.Timeout does NOT bound the subsequent body read. A stalled body
        // (network hiccup, model wedged) must still be bounded by _cfg.Ollama.TimeoutSeconds,
        // not hang forever.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new HangingStreamContent() });
        var cfg = Cfg();
        cfg.Ollama.TimeoutSeconds = 1; // real seconds — test must complete quickly regardless
        var client = new OllamaClient(cfg, handler);
        var longText = new string('x', 60);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        sw.Stop();

        Assert.True(r.Skipped);
        Assert.Equal(longText, r.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"expected the stalled body read to be bounded by the ~1s timeout, took {sw.Elapsed}");
    }

    [Fact]
    public async Task CleanAsync_PostsExpectedBodyShape()
    {
        var ndjson = "{\"response\":\"ok\",\"done\":true}\n";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ndjson, Encoding.UTF8) });
        var cfg = Cfg();
        var client = new OllamaClient(cfg, handler);
        var longText = new string('x', 60);
        await client.CleanAsync(longText, CleanupMode.Faithful);

        Assert.EndsWith("/api/generate",
            handler.LastRequest!.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal(cfg.Ollama.Model, root.GetProperty("model").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("system").GetString()));
        Assert.Equal(longText, root.GetProperty("prompt").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.1, root.GetProperty("options")
            .GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(8192, root.GetProperty("options")
            .GetProperty("num_ctx").GetInt32());
        Assert.Equal("30m", root.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task WarmUp_PostsEmptyPromptWithKeepAlive_ReturnsTrue()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{}", Encoding.UTF8) });
        var client = new OllamaClient(Cfg(), handler);

        Assert.True(await client.WarmUpAsync());
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("", doc.RootElement.GetProperty("prompt").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("30m", doc.RootElement.GetProperty("keep_alive").GetString());
        // The warm-up must load the runner with the SAME context size CleanAsync
        // uses — otherwise Ollama reloads the model on the first real cleanup and
        // the warm-up doesn't actually fix the first-dictation lag.
        Assert.Equal(8192, doc.RootElement.GetProperty("options")
            .GetProperty("num_ctx").GetInt32());
        Assert.EndsWith("/api/generate",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WarmUp_ConnectionFailure_ReturnsFalseWithoutThrowing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var client = new OllamaClient(Cfg(), handler);
        Assert.False(await client.WarmUpAsync());
    }

    [Fact]
    public async Task LocalhostUrl_IsRewrittenTo127001()
    {
        // On Windows "localhost" resolves to ::1 first, but Ollama binds 127.0.0.1
        // only — every fresh connection eats a ~2s Winsock SYN-retry on the refused
        // ::1 attempt (~4s total when Ollama is down). The client must talk IPv4
        // loopback directly regardless of how the config spells it.
        var ndjson = "{\"response\":\"ok\",\"done\":true}\n";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ndjson, Encoding.UTF8) });
        var cfg = Cfg();
        cfg.Ollama.Url = "http://localhost:11434";
        var client = new OllamaClient(cfg, handler);

        await client.CleanAsync(new string('x', 60), CleanupMode.Faithful);
        Assert.Equal("127.0.0.1", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal(11434, handler.LastRequest!.RequestUri!.Port);

        await client.WarmUpAsync();
        Assert.Equal("127.0.0.1", handler.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task OllamaDown_FailsFastToRawTranscript()
    {
        // Regression: with Ollama stopped, a refused loopback connect costs ~2s per
        // address in Winsock SYN retries (~4s via "localhost" dual-stack), delaying
        // the words-never-lost passthrough. The default handler's ConnectTimeout must
        // cut that to sub-second. Uses a real socket against a port we just freed.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var cfg = Cfg();
        cfg.Ollama.Url = $"http://localhost:{deadPort}";
        var client = new OllamaClient(cfg); // default handler — the one under test
        var longText = new string('x', 60);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        sw.Stop();

        Assert.True(r.Skipped);
        Assert.Equal(longText, r.Text);
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"expected fail-fast passthrough well under 1500ms, took {sw.ElapsedMilliseconds}ms");
    }
}
