# DeadAir Ollama keep_alive + Warm-Up — Design Spec

Eliminates Polished-mode's intermittent lag. Approved 2026-07-05 (Phase-1 item,
built via the phase-0 subagent pipeline as Task 16).

## Problem

Ollama evicts qwen2.5:7b from VRAM after ~5 idle minutes (its default
`keep_alive`). The next dictation pays a multi-second model reload — the
user-reported "off and on" Polished lag. Separately, the first dictation after
boot pays the full cold load. (Polished's inherent extra token generation vs
Faithful is physics — out of scope.)

## Changes

### 1. `keep_alive` on every cleanup request
- `AppConfig.Ollama` gains `public string KeepAlive { get; set; } = "30m";`
  (serializes as `keepAlive`; user-editable in `%APPDATA%\DeadAir\config.json`).
- `OllamaClient.CleanAsync` request body gains `keep_alive = _cfg.Ollama.KeepAlive`
  alongside the existing model/system/prompt/stream/options fields.
- Rationale for "30m" default: covers a working session's gaps; frees ~5 GB
  VRAM before gaming naturally. `-1` (forever) rejected as default — the model
  would squat VRAM even after DeadAir exits.

### 2. Startup warm-up
- New method on the concrete client (interface `ITranscriptCleaner` unchanged):
  `public async Task<bool> WarmUpAsync(CancellationToken ct = default)` —
  POSTs `{model, prompt: "", stream: false, keep_alive}` to `/api/generate`
  (Ollama's documented preload idiom: empty prompt loads the model without
  generating). Returns true on HTTP success; catches ALL exceptions → false.
  Never throws, never toasts.
- `App.OnStartup` (which constructs the concrete `OllamaClient`) fires it
  once, fire-and-forget: on false/completion, one log line
  (`"ollama warm-up ok"` / `"ollama warm-up failed (will load on first use)"`),
  NO toast — Ollama may legitimately still be starting; dictation-time failures
  are already covered by raw-passthrough.

## Error handling
Unchanged pipeline semantics: cleanup failures still return the raw transcript.
Warm-up is purely opportunistic.

## Testing
- Extend OllamaClient stub tests with a request-capturing handler asserting the
  outgoing POST body shape: `model`, `system`, `prompt`, `"stream":true`,
  `options.temperature`, `options.num_ctx`, and the new `keep_alive` — this
  also closes the Phase-1 backlog item "no test asserts POST body/URL shape".
  Assert URL ends `/api/generate`.
- `WarmUpAsync`: posts empty prompt + `keep_alive`, `stream:false`; returns
  false (not throw) on connection failure; returns true on 200.
- Config: `KeepAlive` default "30m" + round-trips (extend existing ConfigStore
  tests minimally).

## Non-goals
Periodic re-ping; per-mode keep_alive; changing TimeoutSeconds; addressing
Polished's inherent generation time.
