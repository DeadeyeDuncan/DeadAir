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

    // Windows Winsock retransmits SYN after an RST, so a connection-refused to
    // loopback takes ~2s per address before failing — not milliseconds. Cap the
    // connect phase so a stopped Ollama drops to raw passthrough near-instantly.
    // Loopback connects complete in <5ms; 250ms is 50x headroom (local/LAN only —
    // a WAN Ollama URL would need this raised).
    internal const int ConnectTimeoutMs = 250;

    public OllamaClient(AppConfig cfg, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            { ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs) })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(cfg.Ollama.TimeoutSeconds);
    }

    /// <summary>Config URL with a trailing slash trimmed and literal "localhost"
    /// rewritten to 127.0.0.1. Windows resolves "localhost" to ::1 first, but Ollama
    /// binds 127.0.0.1 only — the refused ::1 attempt costs a ~2s Winsock SYN-retry
    /// on every fresh connection, and would eat the whole ConnectTimeout budget
    /// before the IPv4 address is ever tried.</summary>
    private string BaseUrl()
    {
        var url = _cfg.Ollama.Url.TrimEnd('/');
        var uri = new Uri(url);
        if (uri.Host != "localhost") return url;
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/');
    }

    public async Task<CleanupResult> CleanAsync(string transcript,
        CleanupMode mode, CancellationToken ct = default)
    {
        // Empty/whitespace transcript: never worth an LLM call, translating or
        // not (a null "final" payload reaches here as ""). Same reason string
        // as the skip-guard so the Orchestrator's failure toast stays silent.
        if (string.IsNullOrWhiteSpace(transcript))
            return new CleanupResult(transcript, true, "below skip guard");
        // Skip-guard only applies when the output language is English: a short
        // utterance still needs the LLM to translate it ("yes thanks" must not
        // inject as English when Spanish is selected).
        if (!_cfg.Cleanup.TranslationActive &&
            transcript.Length < _cfg.Cleanup.SkipGuardChars)
            return new CleanupResult(transcript, true, "below skip guard");
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _cfg.Ollama.Model,
                system = PromptBuilder.Build(mode, _cfg),
                prompt = transcript,
                // qwen3 streams <think> blocks into the response unless disabled.
                // Sent unconditionally: non-thinking models ignore it (verified
                // on Ollama v0.31.1, 2026-07-20).
                think = false,
                stream = true,
                keep_alive = _cfg.Ollama.KeepAlive,
                options = new
                {
                    temperature = _cfg.Ollama.Temperature,
                    num_ctx = _cfg.Ollama.NumCtx,
                },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                BaseUrl() + "/api/generate")
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
            // Timeout (TaskCanceledException) and HTTP failures still return raw transcript—words never lost.
            // Base exception: a connect-timeout surfaces as TaskCanceledException whose
            // "operation was canceled" message reads as user cancellation in the toast.
            return new CleanupResult(transcript, true, ex.GetBaseException().Message);
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
                think = false, // match CleanAsync — see comment there
                stream = false,
                keep_alive = _cfg.Ollama.KeepAlive,
                // Must match CleanAsync's load-affecting options: a warm-up at
                // Ollama's default num_ctx loads a runner CleanAsync can't
                // reuse, so the first dictation would pay the reload anyway.
                options = new { num_ctx = _cfg.Ollama.NumCtx },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                BaseUrl() + "/api/generate")
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
