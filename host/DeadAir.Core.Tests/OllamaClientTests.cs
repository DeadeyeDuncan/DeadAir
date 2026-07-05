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
        return Task.FromResult(respond(request));
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
}
