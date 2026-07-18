# Multi-vendor GPU + CPU support (docs pass) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make DeadAir's existing multi-vendor support explicit in docs — Nvidia and Intel GPUs (via the cross-vendor Vulkan `whisper-server.exe`, plus optional CUDA/SYCL fast-paths) and Intel CPUs (already the baseline) — with zero behavior change.

**Architecture:** The GPU engine spawns any `whisper-server.exe` and talks HTTP, so it is backend-agnostic; the CPU engine is vendor-neutral `faster-whisper`. Nothing is code-gated to AMD. This plan therefore changes only prose (README living docs), a dated addendum to the historical spec, and cosmetic code comments — no logic, signatures, config, or asserted strings.

**Tech Stack:** Markdown (README, spec); Python docstrings/comments (`asr_sidecar`). Verification via existing `dotnet build`/`dotnet test` (xUnit) and `pytest`.

## Global Constraints

- **No behavior change.** No source logic, method signatures, config keys, IPC fields, or test-asserted strings may be altered. (`degraded` reason strings and all test fixtures stay verbatim.)
- **No new config / detection / backend code (YAGNI).** The supplied `whisper-server.exe` is the backend selector.
- **Preserve history in `docs/spec.md`.** Original 2026-07-05 AMD framing (line 3, §1 target hardware, §5 rationale, §7 heading) stays verbatim; multi-vendor content is added only as clearly-dated "2026-07-18 update" annotations.
- **Honesty caveat is mandatory.** Docs must state that Nvidia (Vulkan/CUDA) and Intel-GPU (Vulkan/SYCL) paths are documented from whisper.cpp's backend-independent interface, **not hardware-tested here** (dev box is AMD RX 6800 XT only). AMD-Vulkan and the CPU path (incl. Intel CPUs) are genuinely exercised.
- **Link-first style.** Match the existing spec §7 / resources style: point to upstream build flags, don't write a full build tutorial.
- **Verified card wording:** the RX 6800 XT is named as *developed/verified on*, never as a requirement.

---

## File Structure

- `README.md` — living user-facing doc; carries the full multi-vendor reframe (GPU bullet, CPU bullet, Requirements, a new "GPU backends" subsection).
- `docs/spec.md` — historical design artifact; receives two dated additive annotations only (top note + §7 tail note).
- `sidecar/asr_sidecar/engines/gpu_whispercpp.py` — cosmetic de-AMD-ifying of three comment/docstring spots; no code.

---

## Task 1: README multi-vendor reframe

**Files:**
- Modify: `README.md` (Features GPU bullet ~24-27; CPU bullet ~28-29; Requirements ~117-126; add new "GPU backends" subsection)

**Interfaces:**
- Consumes: nothing.
- Produces: a `## GPU backends (AMD · Nvidia · Intel)` subsection anchor that the spec §7 annotation (Task 2) and the Requirements note both point to.

- [ ] **Step 1: Reframe the Features → GPU bullet**

Replace (currently ~lines 25-27):

```
  - **GPU** — a whisper.cpp `whisper-server` subprocess built with the Vulkan
    backend, keeping a `large-v3-turbo` GGML model resident in VRAM (works on AMD
    with no CUDA/ROCm requirement).
```

with:

```
  - **GPU** — a whisper.cpp `whisper-server` subprocess built with the Vulkan
    backend, keeping a `large-v3-turbo` GGML model resident in VRAM. Vulkan is
    cross-vendor, so one binary covers **AMD, Nvidia, and Intel** GPUs with no
    CUDA/ROCm requirement — and all features (GPU ASR + the GPU-only live pill)
    work on any of them. For maximum per-vendor throughput you may instead supply
    a **CUDA** (Nvidia) or **SYCL** (Intel) build — see [GPU backends](#gpu-backends--amd--nvidia--intel) below.
```

- [ ] **Step 2: Extend the Features → CPU bullet to name Intel**

Replace (currently ~lines 28-29):

```
  - **CPU** — `faster-whisper` (`small`, int8) in-process, the guaranteed-works
    baseline and automatic fallback.
```

with:

```
  - **CPU** — `faster-whisper` (`small`, int8) in-process, the guaranteed-works
    baseline and automatic fallback. Runs on any modern x86-64 CPU — **AMD or
    Intel** — auto-using AVX2/AVX-512 where available.
```

- [ ] **Step 3: Update the Requirements section**

Replace (currently ~lines 117-118):

```
- **Windows 11.** GPU path needs a Vulkan-capable GPU (developed on an AMD
  RX 6800 XT); CPU path works on any machine.
```

with:

```
- **Windows 11.** GPU path needs a Vulkan-capable GPU — **AMD, Nvidia, or
  Intel** (developed/verified on an AMD RX 6800 XT; the Nvidia and Intel GPU
  paths are documented from whisper.cpp's backend-independent interface, **not
  hardware-tested here**). CPU path works on any x86-64 machine — AMD or Intel.
```

Replace (currently ~lines 123-126):

```
- **For the GPU engine:** a Vulkan whisper.cpp `whisper-server.exe` in
  `tools/whisper/` and a GGML model at `models/ggml-large-v3-turbo.bin`. Both are
  gitignored — download or build them yourself (see `docs/spec.md` §7 for AMD
  build notes). Without them, `engine=auto` simply falls back to CPU.
```

with:

```
- **For the GPU engine:** a whisper.cpp `whisper-server.exe` in `tools/whisper/`
  and a GGML model at `models/ggml-large-v3-turbo.bin`. Both are gitignored —
  download or build them yourself (see [GPU backends](#gpu-backends--amd--nvidia--intel)
  for which build to grab per vendor). Without them, `engine=auto` simply falls
  back to CPU.
```

- [ ] **Step 4: Add the "GPU backends" subsection**

Insert a new subsection immediately **after** the "## Requirements" section and before "## Setup":

```
## GPU backends (AMD · Nvidia · Intel)

The GPU engine just spawns whatever `whisper-server.exe` you place in
`tools/whisper/` and talks HTTP to it — it does not care which backend that
binary was built with. So the backend is chosen entirely by *which build you
supply*:

| Your GPU | Low-friction (all features) | Optional fast-path |
|---|---|---|
| **AMD** | Vulkan build | — (ROCm on Windows is unreliable) |
| **Nvidia** | Vulkan build | **CUDA** build (`-DGGML_CUDA=1`) |
| **Intel** (Arc / Iris Xe) | Vulkan build | **SYCL** build (`-DGGML_SYCL=1`) |

- **Vulkan is the default recommendation for every vendor** — one cross-vendor
  binary, no toolkit install, and it delivers *all* features (GPU ASR and the
  GPU-only live pill). Get a prebuilt Vulkan binary or build upstream
  [whisper.cpp](https://github.com/ggml-org/whisper.cpp) with `-DGGML_VULKAN=1`.
- **CUDA / SYCL** builds are only *faster* on their vendor's hardware, not more
  capable. Build upstream whisper.cpp with `-DGGML_CUDA=1` (Nvidia) or
  `-DGGML_SYCL=1` (Intel oneAPI), or grab a matching prebuilt release.
- **Multi-GPU machines** (e.g. laptop iGPU + dGPU): the Vulkan build honors the
  `GGML_VK_VISIBLE_DEVICES` env var and the CUDA build honors
  `CUDA_VISIBLE_DEVICES`; the sidecar inherits the host environment, so you can
  pin the right device without any code change.

> **Tested-hardware note:** DeadAir is developed on an AMD RX 6800 XT. The
> AMD-Vulkan and CPU paths are exercised directly; the Nvidia (Vulkan/CUDA) and
> Intel-GPU (Vulkan/SYCL) paths are documented from whisper.cpp's published,
> backend-independent CLI/HTTP contract and have **not** been tested on that
> hardware here. Reports welcome.
```

- [ ] **Step 5: Verify README renders and anchors resolve**

Read `README.md` top-to-bottom. Confirm: (a) GPU and CPU bullets name AMD/Nvidia/Intel; (b) the two `#gpu-backends--amd--nvidia--intel` links point at the new heading (GitHub slug: heading lowercased, spaces→`-`, `·` dropped, `(`/`)` dropped → `gpu-backends--amd--nvidia--intel`); (c) the honesty note is present. No build/test impact (docs only).

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "docs(readme): document Nvidia + Intel GPU and Intel CPU support"
```

---

## Task 2: spec.md dated annotations (history preserved)

**Files:**
- Modify: `docs/spec.md` (add a note after the status block ~line 12; add a note at the end of §7)

**Interfaces:**
- Consumes: the README "GPU backends" subsection (linked as the setup source of truth).
- Produces: nothing downstream.

- [ ] **Step 1: Add the top-of-doc dated note**

Insert immediately **after** line 12 (the `**Build note:**` line), as a new paragraph:

```
> **2026-07-18 update — multi-vendor runtime.** The design below targets an
> all-AMD box, which is what it was built and verified on. Since then it is worth
> recording that the ASR boundary is vendor-neutral by construction: the GPU
> engine spawns any `whisper-server.exe` and talks HTTP, so the *same* Vulkan
> binary also runs on **Nvidia** and **Intel** GPUs (with **CUDA**/**SYCL**
> builds as optional per-vendor fast-paths), and the CPU engine runs on any
> x86-64 CPU (AMD or Intel). No code changed to enable this. Setup per vendor
> lives in the README's "GPU backends" section. The Nvidia/Intel-GPU paths are
> documented, not hardware-tested here.
```

- [ ] **Step 2: Add the §7 tail dated note**

Append at the very end of §7 (after the existing runtime-deps / read-before-starting content, before the next `---`/section):

```
**2026-07-18 update — non-AMD hardware.** The §7 spike above is AMD-specific by
history, but the runtime is not AMD-locked. For **Nvidia** or **Intel** GPUs,
supply the matching `whisper-server.exe` instead of the AMD one — Vulkan covers
every vendor (all features), and CUDA (`-DGGML_CUDA=1`, Nvidia) or SYCL
(`-DGGML_SYCL=1`, Intel oneAPI) are optional faster builds. On multi-GPU
machines, `GGML_VK_VISIBLE_DEVICES` (Vulkan) / `CUDA_VISIBLE_DEVICES` (CUDA)
select the device; the sidecar inherits host env. See the README "GPU backends"
section for links. These non-AMD paths are documented from whisper.cpp's
backend-independent interface, not hardware-tested here.
```

- [ ] **Step 3: Verify history is preserved**

Run: `git diff docs/spec.md`
Expected: **only additions** (green `+` lines) — no deletions or modifications to line 3, §1 target hardware, §5 rationale, or the §7 heading. If any existing line shows as changed, revert that part; annotations must be purely additive.

- [ ] **Step 4: Commit**

```bash
git add docs/spec.md
git commit -m "docs(spec): add dated multi-vendor note; preserve AMD-target history"
```

---

## Task 3: De-AMD-ify GPU engine comments (cosmetic, no logic)

**Files:**
- Modify: `sidecar/asr_sidecar/engines/gpu_whispercpp.py` (class docstring ~line 17; inline comment ~line 24; `_wait_ready` error string ~line 120)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. **Only comments/docstrings and one non-asserted user-facing error hint change.**

> Note: the `_wait_ready` message at line ~119-120 is a `GpuEngineError` detail string. Confirm no test asserts on its "Vulkan" text before editing.

- [ ] **Step 1: Confirm no test depends on the strings being changed**

Run: `grep -rniE "no vulkan device|vulkan build|device-lost|AMD driver" sidecar/tests host/DeadAir.Core.Tests`
Expected: **no matches** in test files. (Tests assert on `degraded` `reason` values like `"no vulkan"`, which live in `OrchestratorTests.cs` and are **not** touched by this task.) If any match appears in a test, stop and leave that specific string unchanged.

- [ ] **Step 2: Generalize the class docstring**

Replace (currently ~lines 16-26) the two AMD/Vulkan-specific phrasings inside the `GpuEngine` docstring:

- `"""Client for a whisper.cpp `whisper-server` (Vulkan build) subprocess.` → `"""Client for a whisper.cpp `whisper-server` subprocess (any GPU backend build).`
- `The Vulkan server can crash sporadically at inference time (AMD driver` → `The server can crash sporadically at inference time (driver`

(Leave the rest of the docstring — the self-heal rationale — unchanged.)

- [ ] **Step 3: Generalize the `_wait_ready` startup-failure hint**

Replace (currently ~line 119-120):

```
                raise GpuEngineError(
                    "whisper-server exited during startup "
                    "(no Vulkan device? bad model?)\n" + self.server_log_tail())
```

with:

```
                raise GpuEngineError(
                    "whisper-server exited during startup "
                    "(no compatible GPU device? bad model?)\n" + self.server_log_tail())
```

- [ ] **Step 4: Confirm no logic changed**

Run: `git diff sidecar/asr_sidecar/engines/gpu_whispercpp.py`
Expected: changes limited to comment/docstring text and the one startup-hint string; no changes to imports, control flow, method signatures, spawn args, HTTP calls, or the `transcribe` self-heal path.

- [ ] **Step 5: Run the sidecar test suite**

Run: `cd sidecar && .venv\Scripts\python -m pytest -q`
Expected: PASS — same result as before the edit (esp. `test_gpu_engine`, `test_gpu_runtime_fallback`, `test_engine_select`, `test_main_loop_cleanup`).

- [ ] **Step 6: Commit**

```bash
git add sidecar/asr_sidecar/engines/gpu_whispercpp.py
git commit -m "refactor(sidecar): vendor-neutral wording in GPU engine comments"
```

---

## Task 4: Final verification gate

**Files:** none (verification only).

- [ ] **Step 1: Host build**

Run: `cd host && dotnet build -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Host tests**

Run: `cd host && dotnet test`
Expected: PASS — `OrchestratorTests` incl. `Degraded_ToastsOnlyOnce` (asserts the `"CPU"` toast; its `reason` inputs untouched).

- [ ] **Step 3: Sidecar tests**

Run: `cd sidecar && .venv\Scripts\python -m pytest -q`
Expected: PASS (full suite; `slow`/`integration` may need network/subprocess — run at least the non-slow set green).

- [ ] **Step 4: Grep for any missed AMD-only framing in living docs**

Run: `grep -niE "only.*AMD|AMD.only|works on AMD|AMD-specific" README.md`
Expected: no results implying AMD-exclusivity in README (the "developed/verified on AMD" phrasing is fine and intended).

- [ ] **Step 5: Final read-through against success criteria**

Confirm from the spec's success criteria: README + spec name AMD/Nvidia/Intel (GPU) and AMD/Intel (CPU); CUDA + SYCL linked as optional with Vulkan as default; honesty caveat present in both README and spec; `git diff` shows no source logic changed.

---

## Self-Review

**Spec coverage:**
- README GPU/CPU/Requirements reframe → Task 1. ✓
- New GPU-backends section (CUDA/SYCL links, multi-GPU note) → Task 1 Step 4. ✓
- spec.md dated annotations, history preserved → Task 2. ✓
- Code comment de-AMD-ifying, no behavior change → Task 3. ✓
- Honesty caveat present → Task 1 Step 4 (README note) + Task 2 (both spec notes). ✓
- Suites + build green → Tasks 3 & 4. ✓
- No new config/detection/backend code (YAGNI) → enforced by Global Constraints; no task adds any. ✓

**Placeholder scan:** No TBD/TODO; every edit shows exact before/after text. ✓

**Type consistency:** No types/signatures touched. The anchor slug `#gpu-backends--amd--nvidia--intel` is used identically in Task 1 Steps 1, 3, and referenced by Task 2 (via README pointer). ✓
