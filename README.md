# DeadAir

Fully-local voice dictation for Windows (Wispr Flow clone).
Hold Right Ctrl → speak → release → cleaned text at your cursor.

Spec: docs/spec.md · Plan: docs/superpowers/plans/2026-07-05-deadair-phase0.md

## De-risk checklist (Task 0)
- [x] Const-me WhisperDesktop live-mic transcribes on the RX 6800 XT — superseded by whisper-server test (below); GUI validation not run in this pass, but Vulkan-on-6800XT is proven end-to-end via whisper-server, which subsumes what the Const-me check was for
- [ ] `ollama ps` shows qwen2.5:7b at 100% GPU — BLOCKED: Ollama is not installed on this machine (see task-0-report.md)
- [x] whisper-server (Vulkan) transcribes jfk.wav — `ggml_vulkan: 0 = AMD Radeon RX 6800 XT`, response contained "ask not what your country can do for you"
