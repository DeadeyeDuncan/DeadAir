# DeadAir

Fully-local voice dictation for Windows 11 (a Wispr Flow clone). Hold **Right
Ctrl**, speak, release — your words are transcribed on-device (whisper.cpp
Vulkan on AMD / faster-whisper CPU) and cleaned up by a local LLM (Ollama),
then inserted at the cursor in any app. Nothing leaves your machine.

## Requirements
- Windows 11, AMD GPU (Vulkan) or any CPU
- Ollama ≥ 0.12.11 with `qwen2.5:7b` pulled
- Python 3.11+, .NET 8 SDK
- `tools/whisper/whisper-server.exe` (Vulkan build — see docs/spec.md §7)
- `models/ggml-large-v3-turbo.bin`

## Setup
1. `cd sidecar && python -m venv .venv && .venv\Scripts\pip install -r requirements.txt`
2. `ollama pull qwen2.5:7b`
3. `cd host && dotnet build -c Release`
4. Run `DeadAir.App.exe` — a tray icon appears.

## Use
- **Hold Right Ctrl** → speak → release. Cleaned text lands at your cursor.
- Tray menu: toggle Faithful/Polished cleanup, Settings, Exit.
- Custom dictionary (Settings) biases recognition toward your jargon.

## Known limits (v0)
- Can't type into elevated/admin windows (Windows UIPI) — text is left on
  the clipboard; paste manually.
- Hotkey changes need an app restart.
- Utterance-at-a-time (no live streaming yet — Phase 1).

## Architecture
See docs/spec.md. Host (C#/WPF) ↔ Python ASR sidecar over stdio JSON;
Ollama does transcript cleanup. Phased roadmap in spec §10.

## De-risk checklist (Phase 0)
- [x] Const-me WhisperDesktop live-mic transcribes on the RX 6800 XT — superseded by whisper-server test; Vulkan-on-6800XT proven end-to-end
- [x] Ollama installed v0.31.1, qwen2.5:7b 100% GPU
- [x] whisper-server (Vulkan) transcribes jfk.wav — response contained "ask not what your country can do for you"
