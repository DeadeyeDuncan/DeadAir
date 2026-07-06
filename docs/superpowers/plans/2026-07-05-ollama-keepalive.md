# DeadAir Ollama keep_alive + Warm-Up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill Polished-mode's intermittent lag by keeping qwen2.5:7b resident 30 min per use and preloading it at app startup.

**Architecture:** Two additive changes to existing components: `keep_alive` field flows config → OllamaClient request body; a new opportunistic `WarmUpAsync` on the concrete OllamaClient fires once from App startup. `ITranscriptCleaner` unchanged. Spec: `../specs/2026-07-05-ollama-keepalive-design.md`.

**Tech Stack:** Existing C#/.NET 8 host (DeadAir.Core + DeadAir.App), xUnit.

## Global Constraints

- `KeepAlive` default EXACTLY `"30m"`; config key `keepAlive` (camelCase policy already global).
- Warm-up body EXACTLY `{model, prompt: "", stream: false, keep_alive}`; CleanAsync body gains `keep_alive` alongside existing fields (model/system/prompt/stream:true/options).
- `WarmUpAsync` returns bool, catches ALL exceptions → false, never throws, never toasts; App logs one line either way, no toast.
- Words-never-lost semantics of CleanAsync untouched (incl. the caller-cancellation exception filter).
- Commit message: `feat(host): ollama keep_alive + startup warm-up (Polished lag fix)`.

---

### Task 16: keep_alive + warm-up

**Files:**
- Modify: `host/DeadAir.Core/Config/AppConfig.cs` (OllamaConfig)
- Modify: `host/DeadAir.Core/Cleanup/OllamaClient.cs`
- Modify: `host/DeadAir.App/App.xaml.cs` (warm-up call in OnStartup)
- Test: `host/DeadAir.Core.Tests/OllamaClientTests.cs`, `host/DeadAir.Core.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: existing `OllamaClient(AppConfig, HttpMessageHandler?)`, `_http`, `_cfg`; `file sealed class StubHandler` in OllamaClientTests.cs; App's `_log` + the concrete `cleaner` variable in OnStartup.
- Produces: `OllamaConfig.KeepAlive` (string, default "30m"); `public async Task<bool> WarmUpAsync(CancellationToken ct = default)` on OllamaClient.

- [ ] **Step 1: Write the failing tests**

Add to `host/DeadAir.Core.Tests/OllamaClientTests.cs` (same file as the existing `StubHandler`):

```csharp
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
```

```csharp
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
```

(Add `using System.Text.Json;` to the test file if not present.)

In `host/DeadAir.Core.Tests/ConfigStoreTests.cs`, add one assertion to the existing `Load_MissingFile_ReturnsDefaults` test:

```csharp
        Assert.Equal("30m", cfg.Ollama.KeepAlive);
```

- [ ] **Step 2: Run to verify failure**

Run: `cd "H:\DeadMind V.3\DeadAir\host"; dotnet test --filter "OllamaClientTests|ConfigStoreTests"`
Expected: compile FAIL — `KeepAlive` and `WarmUpAsync` not defined.

- [ ] **Step 3: Implement**

`host/DeadAir.Core/Config/AppConfig.cs` — add to `OllamaConfig`:

```csharp
    public string KeepAlive { get; set; } = "30m";
```

`host/DeadAir.Core/Cleanup/OllamaClient.cs` — in `CleanAsync`'s serialized body, add alongside the existing fields:

```csharp
                keep_alive = _cfg.Ollama.KeepAlive,
```

and add the new method:

```csharp
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
```

`host/DeadAir.App/App.xaml.cs` — in `OnStartup`, immediately after the concrete `var cleaner = new OllamaClient(_config);` line:

```csharp
        _ = Task.Run(async () =>
        {
            var ok = await cleaner.WarmUpAsync();
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} ollama warm-up " +
                (ok ? "ok" : "failed (will load on first use)"));
        });
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "OllamaClientTests|ConfigStoreTests"` — Expected: all pass (8 Ollama + 2 config).
Then full suite: `dotnet test` — Expected: 48/48 (45 + 3 new).

- [ ] **Step 5: Rebuild solution + hash-check deployment (standing lesson)**

Run: `dotnet build DeadAir.slnx`, then compare `Get-FileHash` of
`DeadAir.App\bin\Debug\net8.0-windows\DeadAir.Core.dll` vs
`DeadAir.Core\bin\Debug\net8.0\DeadAir.Core.dll` — MUST match.

- [ ] **Step 6: Commit**

```bash
git add host
git commit -m "feat(host): ollama keep_alive + startup warm-up (Polished lag fix)"
```

---

## Self-review notes

- Spec coverage: keep_alive in CleanAsync body ✓ (Step 3 + body-shape test), KeepAlive config default ✓, WarmUpAsync contract ✓ (all three tests), App fire-and-forget log-no-toast ✓, POST-shape backlog item closed ✓.
- Placeholders: none. Type consistency: `WarmUpAsync` name/signature consistent across test and impl; `KeepAlive` matches `keep_alive` JSON field via explicit anonymous-object naming.
