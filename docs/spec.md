# DeadAir — Design Spec

A fully-local clone of Wispr Flow (voice dictation) for Windows 11 on an all-AMD
box. Hold a hotkey, speak, and cleaned/formatted text is inserted at the cursor
in any app — with speech recognition and LLM cleanup running entirely on-device.

- **Status:** Approved design → implementation planning
- **Date:** 2026-07-05
- **Target hardware:** Windows 11, AMD Radeon RX 6800 XT (16 GB VRAM, gfx1030 /
  RDNA2), no CUDA. CPU-only degrade path required.
- **Build note:** implementation to be done with the `claude-fable-5` model
  (user switches session model at the implementation transition).

> **2026-07-18 update — multi-vendor runtime.** The design below targets an
> all-AMD box, which is what it was built and verified on. Since then it is worth
> recording that the ASR boundary is vendor-neutral by construction: the GPU
> engine spawns any `whisper-server.exe` and talks HTTP, so the *same* Vulkan
> binary also runs on **Nvidia** and **Intel** GPUs (with **CUDA**/**SYCL**
> builds as optional per-vendor fast-paths), and the CPU engine runs on any
> x86-64 CPU (AMD or Intel). No code changed to enable this. Setup per vendor
> lives in the README's "GPU backends" section. The Nvidia/Intel-GPU paths are
> documented, not hardware-tested here.

---

## 1. Goals & non-goals

### MVP scope: "Core + light polish"
1. Global hold-to-talk hotkey → record while held.
2. On-device speech-to-text (Whisper family).
3. On-device LLM cleanup via Ollama (two user-switchable modes).
4. Auto-insert the result at the cursor in any foreground app.
5. System-tray icon.
6. Settings surface (model, hotkey, cleanup mode, ASR engine, mic, dictionary).
7. User-editable custom-word dictionary.

### Non-goals (explicitly deferred to later phases)
- Streaming / live partial results (Phase 1).
- Per-app context / tone matching by reading the active app (Phase 2).
- Voice commands / Command Mode (Phase 3).
- Cloud sync, accounts, multi-device, telephony, mobile.
- Typing into elevated/admin windows (requires a signed UIAccess build; out of
  MVP — documented as a known limitation with a clipboard fallback).

### Success criteria
- Hold hotkey, speak a sentence, release → cleaned text appears at the cursor in
  Notepad, VS Code, and a browser text field.
- End-to-end "release key → text appears" ≈ 1–3 s for a normal utterance on the
  GPU path.
- If the GPU/Vulkan path fails, the app auto-degrades to CPU ASR and still works.
- If Ollama is unavailable, the raw transcript is still inserted (words never
  lost).
- Switching Faithful ↔ Polished changes cleanup behavior without a restart.

---

## 2. Architecture

Two cooperating processes plus a warm ASR server:

```
┌─ DeadAir.App.exe (C#/.NET 8 WPF host) ──────────┐  stdio JSON-lines  ┌─ asr_sidecar.py ────────────┐
│ GlobalHotkey (WH_KEYBOARD_LL)                     │◀──────────────────▶│ mic capture (sounddevice)   │
│ SidecarManager (spawn/monitor/restart, IPC)       │  start/stop/config  │ Silero VAD                  │
│ OllamaClient (HTTP localhost:11434)               │  ready/final/error  │ ASR engine selector:        │
│ TextInjector (clipboard-paste + SendInput)        │                     │  ├ GpuEngine → whisper.cpp   │
│ Dictionary · Config · Orchestrator                │                     │  │   whisper-server (Vulkan) │
│ Tray + Settings (WPF UI)                           │                     │  └ CpuEngine → faster-whisper│
└───────────────────────────────────────────────────┘                     └─────────────────────────────┘
        │ HTTP                                                                       │ spawns (GPU path only)
        ▼                                                                            ▼
┌─ Ollama server ─────────┐                                              ┌─ whisper-server (Vulkan) ─┐
│ localhost:11434         │                                              │ local HTTP, model warm    │
│ gemma3:12b (Vulkan)     │                                              │ large-v3-turbo GGML       │
└─────────────────────────┘                                              └───────────────────────────┘
```

### Process responsibilities
- **C# WPF host** owns everything Win32-native and the user-facing app: the
  low-level hotkey hook, tray, settings UI, config, text injection, dictionary,
  the Ollama HTTP call, and orchestration of the flow.
- **Python sidecar** owns everything ASR-native: mic capture, VAD, and speech
  recognition. It is a **long-running process kept alive with the model warm** —
  never spawned per utterance (that would destroy latency). The host signals it;
  it returns transcripts.
- **whisper-server** (GPU path only) is a whisper.cpp server subprocess the
  sidecar spawns, keeping the Whisper model resident in VRAM. The sidecar POSTs
  captured audio to it over local HTTP.

### Why this split
The core value (ASR + LLM tooling) is Python-native; the Windows-integration
half is a thin Win32 layer. Putting ASR in a Python sidecar and the app shell in
C#/WPF lets the user leverage their WPF strength for a polished native EXE while
keeping ASR on its native ecosystem. The only cost is one IPC boundary, which is
narrow and well-defined (below).

### Why the sidecar owns the mic
ASR lives in Python, so mic capture and VAD live there too. The host merely
sends `start` on key-down and `stop` on key-up. This avoids streaming raw PCM
across the process boundary; only small JSON control/result messages cross it.

---

## 3. IPC protocol (host ↔ sidecar)

Transport: **newline-delimited JSON** over the sidecar's stdin (host→sidecar)
and stdout (sidecar→host). stderr is captured to the host log. One JSON object
per line.

### Host → sidecar
| Message | Meaning |
|---|---|
| `{"cmd":"config","engine":"auto\|gpu\|cpu","model":"large-v3-turbo","cpu_model":"small","mic":"<id\|default>","dictionary":["…"],"gpu_server_exe":"<path>","gpu_model_path":"<path>","gpu_port":8910,"partials":true,"partial_interval_ms":600,"partial_min_ms":700,"partial_window_s":30}` | Sent once at startup and on settings change. `dictionary` seeds Whisper `initial_prompt`; the `gpu_*` paths are resolved by the host before sending. |
| `{"cmd":"start"}` | Key-down: begin capturing + VAD. |
| `{"cmd":"stop"}` | Key-up: finalize capture, run ASR, return `final`. |
| `{"cmd":"cancel"}` | Discard the in-progress utterance (e.g. hotkey released too fast / user abort). |
| `{"cmd":"shutdown"}` | Graceful exit. |

### Sidecar → host
| Message | Meaning |
|---|---|
| `{"event":"ready","engine":"gpu\|cpu","model":"…"}` | Sidecar loaded, model warm, which engine won. |
| `{"event":"degraded","engine":"cpu","reason":"vulkan-init-failed"}` | GPU path failed; fell back to CPU. Host toasts once. |
| `{"event":"recording"}` | Capture started (optional UI cue). |
| `{"event":"final","text":"…","ms":1234}` | Transcript + wall-clock ASR time. |
| `{"event":"empty"}` | VAD found no speech / transcript empty. |
| `{"event":"error","where":"asr\|mic\|server","message":"…"}` | Recoverable error for this utterance. |
| `{"event":"waveform","samples":[…]}` | ~40 Hz while recording. Downsampled PCM min/max envelope for the pill oscilloscope. |
| `{"event":"partial","text":"…","seq":N}` | GPU-only interim transcript for the pill preview. Best-effort; never injected. |

The host bounds each utterance with a per-utterance timeout (Orchestrator,
60 s): if no `final`/`empty`/`error` arrives after `stop`, it abandons the
utterance (toast "ASR timed out") and returns to Idle — the sidecar is NOT
restarted for a mere timeout. SidecarManager restarts the sidecar only when
the process actually exits (capped backoff; 5 consecutive failures →
`Faulted`).

---

## 4. Component detail

### 4.1 C# host

**GlobalHotkey** — Installs a `WH_KEYBOARD_LL` low-level keyboard hook via
P/Invoke. `RegisterHotKey` is insufficient because it only fires on key-down and
cannot report key-up, which hold-to-talk requires. Raises `HotkeyDown` /
`HotkeyUp` events for the configured key. The hook callback must be trivial and
the hook thread must pump messages promptly (Windows silently removes a hook
whose callback times out; a stalled hook thread freezes the keyboard). Debounce
auto-repeat key-down.

**SidecarManager** — Spawns `python asr_sidecar.py` (or a packaged sidecar exe),
wires stdin/stdout JSON-lines, forwards commands, parses events, monitors
liveness, and restarts on crash (capped retry with backoff). Surfaces sidecar
state to the tray. Sends `config` on startup/restart; on settings saves it is sent only when an
ASR-relevant field (engine, models, mic, dictionary, GPU settings, partials)
actually changed — host-only saves (cleanup mode, output language, Ollama,
pill) skip the send so the ASR engine is not torn down and recreated.

**OllamaClient** — `HttpClient` to `POST /api/generate` on
`http://localhost:11434`, `stream:true`. Uses the active mode's system prompt,
`temperature:0.1`, `num_ctx:8192`. **Skip-guard:** transcripts shorter than
`skipGuardChars` (default 50) bypass the LLM entirely (small models mangle tiny
inputs) and are injected raw. On timeout / connection failure, return the raw
transcript unchanged and signal "cleanup skipped". *(Planned, Phase 4: detect
at startup whether the configured model is pulled and prompt/auto-pull — today
a missing model surfaces as "cleanup skipped" raw passthrough.)*

**Output language (added 2026-07-18):** when `cleanup.outputLanguage` is a
language other than English, the system prompt gains a translation directive
(`prompts.translationTemplate`, `{language}`/`{style}` tokens — style tracks
Faithful=literal / Polished=natural) and the skip-guard is disabled so short
utterances still translate. Dictionary/technical terms stay untranslated. On
LLM failure the raw English transcript is injected with a "translation
skipped" toast (the toast makes no injection claim — it fires before the
injector runs).

**TextInjector** — Dual strategy, in order:
1. **Clipboard-paste (default):** save current clipboard → set transcript →
   synthesize Ctrl+V (Shift+Insert profile for terminals: Phase 4, the
   `inject.pasteHotkey` key is reserved for it) → restore prior clipboard
   after a short delay. Fast, format-safe. This is Wispr's primary desktop
   mechanism.
2. **Unicode `SendInput` (fallback):** send characters as `KEYEVENTF_UNICODE`
   events; **split code points above the BMP (emoji, rare CJK) into high+low
   UTF-16 surrogate pairs** sent as two INPUT events. Works over RDP and where
   clipboard is blocked; layout-independent.

   Implemented with **direct ctypes/P-Invoke `SendInput`, not
   pynput/pyautogui/`System.Windows.Forms.SendKeys`** — those wrappers mishandle
   surrogate pairs and non-QWERTY layouts.
3. **Always leave the final text on the clipboard** so a failed insert never
   loses dictation. If the foreground window is elevated (UIPI blocks a
   non-elevated app from typing/pasting into it), both methods fail silently →
   toast "text on clipboard — press Ctrl+V".

**Dictionary** — User-editable list of terms (names, jargon, paths). Fed to (a)
the sidecar `config.dictionary` → Whisper `initial_prompt` to bias recognition,
and (b) appended to the LLM system prompt as "preserve these terms exactly".

**Config** — JSON at `%APPDATA%\DeadAir\config.json` (schema §6). Loaded at
startup, hot-reloaded on settings save. Structured logging to
`%APPDATA%\DeadAir\logs\`.

**Tray + Settings (WPF)** — Tray icon reflects state (idle / recording /
transcribing / cleaning / injecting / error) and offers: quick mode toggle
(Faithful/Polished), open settings, quit. Settings window: hotkey capture, ASR
engine (auto/GPU/CPU), ASR model, Ollama model + URL, default cleanup mode,
mic device, dictionary editor. *(Mode-toggle hotkey and inject-method override:
reserved config keys, Phase 4 — see §6 Notes.)*
Suggested tray/UI libs: **H.NotifyIcon** (tray), native WPF for the settings
window.

**Orchestrator** — The state machine tying it together:
`Idle —HotkeyDown→ Recording —HotkeyUp→ Transcribing —final→ Cleaning —cleaned→
Injecting → Idle`, with branches for `empty`, `error`, `degraded`, and
`cleanup-skipped`. Owns the per-utterance timeout and toast/tray updates.

### 4.2 Python sidecar

**Mic capture** — `sounddevice`, 16 kHz mono PCM, ring buffer, started on
`start`, stopped on `stop`. Honors the configured mic device.

**VAD** — **Silero VAD** trims non-speech so only speech frames reach ASR
(~4× fewer errors than webrtcvad at equal false-positive rate; drives the
responsive feel). Emits `empty` if no speech is found.

**ASR engine selector** — One `transcribe(pcm) -> (text, ms)` interface, two
implementations, chosen by `config.engine` (auto tries GPU then CPU):
- **GpuEngine** — spawns/owns a **whisper.cpp `whisper-server`** built with the
  **Vulkan** backend (`-DGGML_VULKAN=1`), model `large-v3-turbo` GGML (~6 GB,
  trivial in 16 GB VRAM), and POSTs audio to its local HTTP `/inference`.
  Rationale: prebuilt Vulkan binaries exist (see §7) and the server keeps the
  model warm in VRAM. *Alternative considered:* `pywhispercpp` in-process — nicer
  but requires building the bindings with Vulkan; `whisper-server` avoids that
  build friction. On init failure (no Vulkan device, binary missing), raise so
  the selector can fall back and emit `degraded`.
- **CpuEngine** — `faster-whisper` in-process, model `small`,
  `compute_type="int8"`, `vad_filter` on. The guaranteed-works baseline and the
  auto-fallback. (faster-whisper/CTranslate2 has **no** AMD-GPU path on Windows —
  it is deliberately the *CPU* engine here, never the GPU one.)

**IPC loop** — Reads commands from stdin, drives capture/VAD/ASR, writes events
to stdout, keeps models warm between utterances.

---

## 5. Cleanup modes

Two stored system prompts, switchable at runtime (tray + hotkey), default
**Faithful**. Both run at `temperature 0.1`, `num_ctx 8192`, output-only.

**Faithful (default) — disfluency-only, verbatim-preserving:**
```
You clean raw speech-to-text transcripts. Remove filler words (um, uh, like, you
know). Fix punctuation, capitalization, and light grammar. If the speaker
self-corrects, keep only the corrected version. Preserve the speaker's meaning,
wording, and tone. Do NOT add, infer, summarize, reword, or answer anything.
Preserve technical terms, names, commands, and file paths exactly. Output ONLY
the cleaned transcript with no preamble.
```

**Polished — light rephrasing allowed:**
```
You clean and lightly polish raw speech-to-text transcripts. Remove filler words,
fix punctuation/capitalization/grammar, keep only self-corrected versions, and
smooth awkward or run-on phrasing into clear, natural sentences. Preserve the
speaker's meaning, intent, and tone — do NOT add new information, summarize, or
answer anything. Preserve technical terms, names, commands, and file paths
exactly. Output ONLY the polished transcript with no preamble.
```

The custom dictionary is appended to whichever prompt is active:
`Preserve these terms exactly, correcting near-misspellings to them: <list>.`

Optionally use Ollama's structured-output `format` (JSON schema) for strict
tasks later; not required for MVP.

---

## 6. Config schema

`%APPDATA%\DeadAir\config.json` (matches `AppConfig` as serialized — camelCase
keys, enum values PascalCase):
```json
{
  "hotkey": { "key": "RControl", "mode": "hold" },
  "modeToggleHotkey": "Ctrl+Alt+M",
  "asr": {
    "engine": "auto", "gpuModel": "large-v3-turbo", "cpuModel": "small",
    "gpuServerExe": "..\\..\\tools\\whisper\\whisper-server.exe",
    "gpuModelPath": "..\\..\\models\\ggml-large-v3-turbo.bin",
    "gpuPort": 8910,
    "partials": true, "partialIntervalMs": 600, "partialMinMs": 700,
    "partialWindowSeconds": 30
  },
  "ollama": { "url": "http://localhost:11434", "model": "gemma3:12b", "numCtx": 8192, "temperature": 0.1, "timeoutSeconds": 20, "keepAlive": "30m" },
  "cleanup": { "mode": "Faithful", "skipGuardChars": 50, "outputLanguage": "English" },
  "prompts": { "faithful": "<full text in §5>", "polished": "<full text in §5>", "translationTemplate": "<{language}/{style} directive — defaults in AppConfig.PromptsConfig>" },
  "dictionary": ["DeadMind", "gfx1030", "faster-whisper"],
  "mic": "default",
  "inject": { "method": "auto", "pasteHotkey": "Ctrl+V", "restoreClipboardDelayMs": 150 },
  "sidecar": { "python": "..\\..\\sidecar\\.venv\\Scripts\\python.exe", "args": "-m asr_sidecar", "workingDir": "..\\..\\sidecar" }
}
```

Notes:
- `hotkey.key` is a SINGLE key name from VkMap: `RControl`, `LControl`,
  `RAlt`, `LAlt`, `RShift`, `LShift`, `CapsLock`, `Scroll`, `Pause`,
  `F13`–`F24`. Combos are not supported. An unknown name falls back to
  `RControl` with a toast.
- Relative paths (`gpuServerExe`, `gpuModelPath`, `sidecar.*`) are resolved by
  walking UP from the exe directory until the path exists (`ResolveAsset` /
  `SidecarPathResolver`), so Debug-build depth doesn't matter.
- **Reserved, not yet implemented** (accepted and persisted, but never read):
  `modeToggleHotkey`, `hotkey.mode` (hold is the only mode),
  `inject.method`, `inject.pasteHotkey` (injection is always
  clipboard-paste-then-SendInput-fallback). Planned for Phase 4.

---

## 7. Setup & dependencies (AMD-specific)

**De-risk spike BEFORE writing app code (~0.5 day):**
1. Run **Const-me/Whisper** (WhisperDesktop, DX11 live-mic GUI) to confirm the
   6800 XT transcribes — validates the card with zero build effort.
2. Install **Ollama ≥ v0.12.11** (v0.12.1–0.12.10 have a gfx1030 crash,
   `0xc0000005`), `ollama pull gemma3:12b`, run it, confirm `ollama ps` shows
   100% GPU (ROCm; falls back to Ollama's bundled Vulkan if ROCm won't load).
   Do **not** install the `likelovewant/ollama-for-amd` fork — mainline covers
   gfx1030.
3. Get a **Vulkan whisper.cpp** binary (least friction first):
   `lemonade-sdk/whisper.cpp-amd` (Vulkan runtime bundled) → or
   `jerryshell/whisper.cpp-windows-vulkan-bin` → or build with `-DGGML_VULKAN=1`
   + the free Vulkan SDK. Transcribe a test wav to confirm.

**Runtime deps:**
- .NET 8 SDK; NuGet: H.NotifyIcon (tray). Win32 via P/Invoke (no extra pkg).
- Python 3.11+; `sounddevice`, `silero-vad` (or torch hub), `faster-whisper`,
  `requests`/`httpx`.
- Ollama service running; `gemma3:12b` pulled.
- whisper.cpp Vulkan binary + `large-v3-turbo` GGML model on disk.

**Read before starting:** [`drajb/whisper-local`](https://github.com/drajb/whisper-local)
(MIT, Windows-first, closest existing clone — ships SendInput injection, optional
Ollama polish, voice-command config, documented AMD setup). Its ASR is
faster-whisper (the broken-on-Windows ROCm path) — swap in whisper.cpp-Vulkan —
but its injection code, prompt patterns, and config model port straight over.
Reference skeleton: [`cjpais/Handy`](https://github.com/cjpais/Handy) (Rust).

**2026-07-18 update — non-AMD hardware.** The §7 spike above is AMD-specific by
history, but the runtime is not AMD-locked. For **Nvidia** or **Intel** GPUs,
supply the matching `whisper-server.exe` instead of the AMD one — Vulkan covers
every vendor (all features), and CUDA (`-DGGML_CUDA=1`, Nvidia) or SYCL
(`-DGGML_SYCL=1`, Intel oneAPI) are optional faster builds. On multi-GPU
machines, `GGML_VK_VISIBLE_DEVICES` (Vulkan) / `CUDA_VISIBLE_DEVICES` (CUDA)
select the device; the sidecar inherits host env. See the README "GPU backends"
section for links. These non-AMD paths are documented from whisper.cpp's
backend-independent interface, not hardware-tested here.

---

## 8. Error-handling matrix

| Failure | Behavior |
|---|---|
| Ollama down / timeout | Inject **raw transcript**; toast "cleanup skipped" ("translation skipped" when translating). |
| GPU/Vulkan init fails | Sidecar auto-selects CPU engine; emits `degraded`; host toasts once. |
| Sidecar crash | SidecarManager restarts (backoff); current utterance lost (audio not persisted); tray shows error. |
| whisper-server dies | GpuEngine restarts it or falls to CPU + `degraded`. |
| Foreground window elevated (UIPI) | Inject fails silently → text left on clipboard + toast "press Ctrl+V". |
| Empty/no-speech transcript | Inject nothing; brief tray flash. |
| Transcript < skipGuardChars | Bypass LLM; inject raw. |
| Transcript < skipGuardChars, translating | LLM still called (guard bypassed) so the utterance is translated. |
| Ollama model not pulled | Today: cleanup fails → raw transcript injected + "cleanup skipped" ("translation skipped" when translating) toast. Planned (Phase 4): detect at startup, prompt/auto-pull. |
| Emoji / supra-BMP chars via SendInput | Split into UTF-16 surrogate pairs (two INPUT events). |

---

## 9. Testing plan

- **Sidecar unit:** VAD gating (speech vs silence clips), engine-selection +
  fallback logic, IPC protocol round-trips, dictionary → `initial_prompt` build.
- **Host unit:** OllamaClient with mocked HTTP (incl. timeout → raw passthrough),
  TextInjector surrogate-pair splitting, skip-guard boundary, config
  load/save/hot-reload, dictionary merge into prompt, mode-toggle state.
- **Integration:** feed a canned wav to the sidecar (bypass mic) → assert
  transcript, then full host cleanup+inject into a scratch text field; run for
  both engines and both cleanup modes.
- **Latency harness:** log per-stage ms (key-up → ASR final → cleanup → injected)
  to validate the ~1–3 s budget.
- **Manual smoke:** hold-key dictation into Notepad, VS Code, a browser field,
  and a terminal (Shift+Insert path); test the elevated-window fallback.

---

## 10. Phasing

- **Phase 0 — MVP (this spec):** ~2–3 weeks part-time. Ship the full loop above.
- **Phase 1 — Streaming/partials (SHIPPED v0.2.0/v0.2.1):** the Live Pill —
  scrolling PCM oscilloscope + GPU-only self-correcting interim transcript
  (preview-only; the key-up final decode stays authoritative). Native
  streaming ASR / confirmed-prefix decoding remain design-doc non-goals.
- **Phase 2 — Per-app context:** `GetForegroundWindow → PID → process name` into
  an app-rules file biasing tone; keep context on-device. ~1 week.
- **Phase 3 — Voice commands / Command Mode:** command grammar + highlight-then-
  speak-to-rewrite. ~1–2 weeks.
- **Phase 4 — Polish:** snippets, whisper-mode tuning, auto-learn from
  corrections, model auto-download UI, optional signed UIAccess build for
  elevated-window typing. Ongoing.

---

## 11. Resolved decisions (from brainstorming)
- Language: **C# WPF host + Python ASR sidecar** (not pure Python, not Electron).
- Cleanup: **two switchable modes** (Faithful default, Polished).
- GPU: **auto-fallback to CPU** (both engines behind one interface).
- Trigger: **hold-to-talk** (low-level keyboard hook).
- Scope: **Core + light polish** (tray, settings, dictionary in MVP).

## 12. Open items to confirm at review
- Project name ("DeadAir" placeholder) and location
  (`H:\DeadMind V.3\DeadAir\` with `host/` + `sidecar/`).
- Default hotkey (spec assumes a held combo; Wispr's default is Ctrl+Win).
- Whether to package the Python sidecar as a bundled exe (PyInstaller) or ship
  it as a venv for v0.

---

## 13. Reference repositories
- `https://github.com/drajb/whisper-local` — closest existing clone; read first.
- `https://github.com/cjpais/Handy` — best-architected skeleton (Rust).
- `https://github.com/Const-me/Whisper` — DX11 engine to validate the card.
- `https://github.com/lemonade-sdk/whisper.cpp-amd` — prebuilt Vulkan whisper.cpp.
- `https://github.com/ggml-org/whisper.cpp` — upstream (build `-DGGML_VULKAN=1`, `whisper-server`).
- `https://github.com/KoljaB/RealtimeSTT` — capture+VAD+transcription reference.
- Full research report: session temp artifact `tasks/wodtojdyc.output` (not
  durable — copy into `docs/research-report.md` if you want it kept with the repo).
