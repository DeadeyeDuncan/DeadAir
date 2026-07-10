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
                keep_alive = _cfg.Ollama.KeepAlive,
                options = new
                {
                    temperature = _cfg.Ollama.Temperature,
                    num_ctx = _cfg.Ollama.NumCtx,
                },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                _cfg.Ollama.Url.TrimEnd('/') + "/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            // HttpClient.Timeout does not bound the body read once ResponseHeadersRead
            // completes SendAsync — a stalled stream would otherwise hang forever. Bound
            // it explicitly with a linked token. NOTE: a linked-timeout cancellation has
            // ct.IsCancellationRequested == false, so it falls through to the general
            // catch below (raw-transcript passthrough) — the caller-cancellation catch
            // clause's semantics are unchanged.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(_cfg.Ollama.TimeoutSeconds));

            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, linked.Token);
            resp.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var reader = new StreamReader(
                await resp.Content.ReadAsStreamAsync(linked.Token));
            while (await reader.ReadLineAsync(linked.Token) is { } line)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — propagate; do not convert to a Skipped result
        }
        catch (Exception ex)
        {
            // Timeout (TaskCanceledException) and HTTP failures still return raw transcript—words never lost
            return new CleanupResult(transcript, true, ex.Message);
        }
    }

    /// <summary>Preload the model (Ollama's empty-prompt idiom) so the first
    /// dictation doesn't pay the cold VRAM load. Opportunistic: returns false
    /// on any failure, never throws.</summary>
    public async Task<bool> WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _cfg.Ollama.Model,
                prompt = "",
                stream = false,
                keep_alive = _cfg.Ollama.KeepAlive,
                // Must match CleanAsync's load-affecting options: a warm-up at
                // Ollama's default num_ctx loads a runner CleanAsync can't
                // reuse, so the first dictation would pay the reload anyway.
                options = new { num_ctx = _cfg.Ollama.NumCtx },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                _cfg.Ollama.Url.TrimEnd('/') + "/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
