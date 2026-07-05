using System.Text;
using System.Text.Json;
using DeadAir.Core.Config;

namespace DeadAir.Core.Cleanup;

public interface ITranscriptCleaner
{
    Task<CleanupResult> CleanAsync(string transcript, CleanupMode mode,
        CancellationToken ct = default);
}

public sealed record CleanupResult(string Text, bool Skipped, string? Reason);

public sealed class OllamaClient : ITranscriptCleaner
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;

    public OllamaClient(AppConfig cfg, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(cfg.Ollama.TimeoutSeconds);
    }

    public async Task<CleanupResult> CleanAsync(string transcript,
        CleanupMode mode, CancellationToken ct = default)
    {
        if (transcript.Length < _cfg.Cleanup.SkipGuardChars)
            return new CleanupResult(transcript, true, "below skip guard");
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _cfg.Ollama.Model,
                system = PromptBuilder.Build(mode, _cfg),
                prompt = transcript,
                stream = true,
                options = new
                {
                    temperature = _cfg.Ollama.Temperature,
                    num_ctx = _cfg.Ollama.NumCtx,
                },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                _cfg.Ollama.Url.TrimEnd('/') + "/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var reader = new StreamReader(
                await resp.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var r))
                    sb.Append(r.GetString());
            }
            var text = sb.ToString().Trim();
            return text.Length == 0
                ? new CleanupResult(transcript, true, "empty LLM output")
                : new CleanupResult(text, false, null);
        }
        catch (Exception ex)
        {
            return new CleanupResult(transcript, true, ex.Message);
        }
    }
}
