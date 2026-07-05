using System.Net;
using System.Text;
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
}
