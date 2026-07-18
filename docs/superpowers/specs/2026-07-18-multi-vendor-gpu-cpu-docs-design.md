# Multi-vendor GPU + CPU support (docs pass) — design

**Date:** 2026-07-18
**Status:** approved, ready for plan
**Type:** documentation + non-functional comment/wording change. **No behavior change.**

## Problem

DeadAir's GPU path was built and documented as "AMD / Vulkan" (developed on an
RX 6800 XT). A user with an **Nvidia** or **Intel** GPU — or an **Intel CPU** —
reading the README or `docs/spec.md` has no signal that the app works for them,
even though it already does. The request: make multi-vendor support explicit so
someone who downloads it from GitHub on non-AMD hardware knows they get all the
features.

## Key finding: the app is already vendor-neutral

The support already exists in code; only the docs lag. Two structural reasons:

1. **GPU engine is backend-agnostic by construction.** `GpuEngine`
   (`sidecar/asr_sidecar/engines/gpu_whispercpp.py`) spawns whatever
   `whisper-server.exe` the config points at (`-m model --host --port`) and
   talks HTTP to it. It never inspects or depends on the compile-time backend of
   that binary. The word "Vulkan" appears only in comments and error strings,
   never in logic. Therefore:
   - The **same Vulkan** `whisper-server.exe` an AMD user supplies runs
     unchanged on **Nvidia** and **Intel Arc / Iris Xe** GPUs (Vulkan is
     cross-vendor). All features — GPU ASR *and* the GPU-only live partials —
     work; nothing is code-gated to AMD.
   - A **CUDA** (Nvidia) or **SYCL** (Intel) `whisper-server.exe` would *also*
     just work, because the spawn contract is identical. These are only *faster*
     on their vendor's hardware, not more capable.

2. **CPU engine is vendor-neutral.** The CPU path is `faster-whisper` /
   CTranslate2 (`sidecar/asr_sidecar/engines/cpu_fasterwhisper.py`) with
   `device="cpu"`, int8. It has no AMD/Intel branch and runs on any modern
   x86-64 CPU, auto-selecting AVX2/AVX-512 at runtime. **Intel CPUs are already
   exactly as supported as AMD** — this is the guaranteed-works baseline and the
   auto-fallback engine.

3. **Host fallback toast is already vendor-neutral.** `Orchestrator.cs` emits
   `"GPU unavailable ({reason}) — using CPU"`; the reason string is passed
   through from the sidecar. No host-side wording assumes a vendor.

## Scope

**In scope — documentation + cosmetic comment edits only:**

1. **README.md**
   - GPU-engine section: reframe "AMD / Vulkan" → "any Vulkan-capable GPU
     (AMD, Nvidia, Intel)." Add that the Vulkan `whisper-server.exe` gives *all*
     features on any of them; note the optional per-vendor fast-path builds
     (CUDA for Nvidia, SYCL for Intel) for maximum throughput.
   - Requirements section: add Nvidia and Intel to the GPU-path line.
   - CPU section: state plainly it runs on **any modern x86-64 CPU — AMD or
     Intel** (currently described neutrally but never names Intel).
2. **docs/spec.md**
   - Rename §7 "Setup & dependencies (AMD-specific)" to a vendor-neutral title;
     add short per-vendor pointers: Vulkan binary (all), plus optional CUDA
     (Nvidia) and SYCL (Intel) build/download links, alongside the existing
     AMD/Vulkan link.
   - Line 3 header ("all-AMD") and the §5 GpuEngine rationale: generalize to
     "cross-vendor Vulkan; CUDA/SYCL optional per vendor." Keep the RX 6800 XT
     named as the one *verified* card ("developed / tested on"), not as a
     requirement.
   - Add a **multi-GPU note**: on iGPU+dGPU machines the Vulkan build honors
     `GGML_VK_VISIBLE_DEVICES` and the CUDA build honors `CUDA_VISIBLE_DEVICES`;
     the sidecar inherits host env, so the user can select a device with no code
     change.
3. **Code comments / docstrings only** — `gpu_whispercpp.py` lines ~17, ~24,
   ~120: generalize "Vulkan build" → "GPU-backend build"; "AMD driver
   device-lost / TDR" → "driver device-lost / TDR"; "no Vulkan device?" → "no
   compatible GPU device?". **No logic, signatures, or messages that tests
   assert on change** (the `degraded` reason strings and test fixtures are
   untouched).

**Explicitly out of scope (YAGNI):**

- No new config keys; no `gpuBackend` field; no backend detection; no CUDA/SYCL
  code path; no device-picker UI. The supplied `whisper-server.exe` *is* the
  backend selector — adding code would duplicate what the process boundary
  already abstracts.
- No change to the CPU engine, the IPC protocol, or any host logic.

## Honesty caveat (must appear in the docs)

The development machine has **only an AMD RX 6800 XT** — no Nvidia or Intel GPU.
Therefore:

- **Vulkan-on-AMD** and the **CPU path** (incl. on Intel CPUs) are genuinely
  exercised (existing pytest suite runs the CPU engine on whatever x86-64 CPU
  runs CI/dev).
- **Nvidia (Vulkan/CUDA)** and **Intel GPU (Vulkan/SYCL)** paths are documented
  from whisper.cpp's published, backend-independent CLI/HTTP contract — **not
  hardware-tested here.** The docs will say so plainly so a non-AMD user treats
  the fast-path guidance as best-effort, not verified-on-their-hardware.

## Testing

Docs + comment edits only, so correctness is verified by *absence of behavior
change*:

- `cd host && dotnet build -c Release` — succeeds.
- `cd host && dotnet test` — xUnit suite green (esp. `OrchestratorTests`, which
  asserts the `degraded` → "CPU" toast; its `reason` strings are untouched).
- `cd sidecar && .venv\Scripts\python -m pytest` — pytest green (esp.
  `test_gpu_engine`, `test_gpu_runtime_fallback`, `test_engine_select`; no
  asserted string or path is altered).
- Manual read-through of rendered README/spec for accuracy and for the presence
  of the honesty caveat.

## Success criteria

- README and spec name AMD, Nvidia, and Intel explicitly for the GPU path, and
  AMD + Intel for the CPU path.
- Per-vendor fast-path builds (CUDA, SYCL) are linked as *optional*, with Vulkan
  as the low-friction default that already delivers every feature.
- The "not hardware-tested on non-AMD" caveat is present.
- No source logic changed; both test suites and the Release build stay green.
