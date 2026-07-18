# DeadAir Output Language (Spoken-Word Translation) — Design Spec

Dictate in English, get the transcript injected in a selected target language
(v1: Spanish). Approved 2026-07-18.

## Problem

DeadAir always injects English. The user wants to speak English and have the
cleaned text land at the cursor in another language — starting with Spanish —
without leaving the local-first envelope (no cloud translation APIs).

Whisper's native `task=translate` only translates X→English, so it cannot do
English→Spanish. The Ollama cleanup stage (`qwen2.5:7b`, multilingual, strong
EN→ES) is the natural place: translation becomes part of the existing single
LLM call. No new models, processes, or sidecar changes.

## Decisions (from brainstorming)

- **Direction:** speak English → inject target language. v1 target: Spanish.
- **Faithful/Polished survive:** cleanup mode stays user-selectable and
  meaningful while translating. Translation composes with the mode, it does
  not replace it.
- **Terms:** custom-dictionary terms and technical jargon (commands, names,
  file paths) stay in English inside the translated sentence.
- **Fallback:** Ollama down/timeout → inject the raw English transcript plus a
  warning toast (words never lost).
- **One LLM call:** clean + translate in a single `qwen2.5:7b` round-trip —
  no added latency over today's cleanup. Two-stage clean→translate and
  dedicated translation models were rejected (latency / YAGNI).

## Changes

### 1. Config (`AppConfig`)

- `CleanupConfig` gains `public string OutputLanguage { get; set; } = "English";`
  (serializes `cleanup.outputLanguage`). `"English"` = translation off —
  existing configs and default behavior are unchanged. Free-form string: any
  language name works because it is substituted into a prompt template; the UI
  seeds only English + Spanish in v1.
- `PromptsConfig` gains `public string TranslationTemplate { get; set; }` with
  a `{language}` token, default (single string, wording final at implementation
  but covering): after applying the cleanup rules above, render the transcript
  in {language}; in Faithful mode translate literally preserving register and
  tone; in Polished mode produce natural, fluent {language}; keep technical
  terms, names, commands, file paths, and listed dictionary terms in their
  original language; output ONLY the {language} text with no preamble.
- Comparison of `OutputLanguage` to English is case-insensitive
  (`"english"`/`"English"` both mean off).

### 2. Prompt composition (`PromptBuilder.Build`)

Current: base prompt (mode) + optional dictionary suffix. New order:

1. base prompt — Faithful or Polished, unchanged;
2. if `cfg.Cleanup.OutputLanguage` is not English: `Prompts.TranslationTemplate`
   with `{language}` replaced;
3. dictionary suffix, unchanged ("preserve exactly" now also reads as
   don't-translate, reinforced by the template's dictionary clause).

`Build` keeps its `(CleanupMode, AppConfig)` signature — language comes from
the config it already receives.

### 3. Skip-guard (`OllamaClient.CleanAsync`)

The `transcript.Length < SkipGuardChars` early-return applies **only when
translation is off**. When translating, short utterances still go to the LLM —
otherwise "yes thanks" would inject as English. **Exception:** an empty or
whitespace-only transcript never reaches the LLM regardless of translation (a
null `final` payload arrives as `""`; POSTing an empty prompt would just burn
the timeout). *(Amended 2026-07-18 after adversarial plan review.)*

### 4. Failure path / toast (`Orchestrator`)

The existing cleanup-failure → inject-raw + toast path is reused. Toast wording
becomes translation-aware: when translation is on, `"translation skipped:
{reason}"`; otherwise today's `"cleanup skipped: {reason}"` stays. The toast
deliberately makes no "injected" claim — it fires before injection runs, and a
failed insert already raises its own press-Ctrl+V toast. *(Amended 2026-07-18
after adversarial plan review: the earlier "translation skipped — injected
English" copy claimed completion prematurely.)*

### 5. UI

- **Settings window:** "Output language" ComboBox (English, Spanish) placed
  with the cleanup-mode control. Applies live via the existing settings-apply
  path, no restart. *(Amended 2026-07-18 after adversarial plan review: the
  save path used to send the sidecar a `config` command unconditionally, and
  the sidecar recreates its ASR engine on every one — so "no sidecar
  reconfigure" required a fix, not just a claim. The save handler now skips
  the sidecar send when the serialized `ConfigCommand` — which carries only
  ASR-relevant fields — is unchanged, so language-only saves no longer bounce
  the engine.)*
- **Tray menu:** "Translate → Spanish" checkable item under the Polished
  toggle. Checked ⇄ `OutputLanguage = "Spanish"`, unchecked ⇄ `"English"`.
  If config holds some other language (hand-edited), the item shows that name
  and toggles between it and English.
- Settings and tray stay in sync through the existing config-store/apply flow.

## Not changing

Sidecar, Whisper engines, VAD, partials, injection. The live pill keeps showing
the English interim transcript — it is a preview; the authoritative injected
text arrives translated. Whisper `task` stays `transcribe`.

## Error handling

Pipeline semantics otherwise unchanged: Ollama unreachable, timeout, or HTTP
error → raw English transcript injected + translation-aware toast. Empty
transcript behavior unchanged. No translation-quality verification pass in v1
(quality = qwen2.5:7b's EN→ES).

## Testing

- `PromptBuilderTests`: translation directive present when `OutputLanguage` is
  Spanish and absent when English (both casings); mode still selects the base
  prompt; ordering base → translation → dictionary; `{language}` substituted.
- `OllamaClient` tests: skip-guard bypass disabled when translating (short
  transcript still POSTs); enabled when English (existing behavior).
- `OrchestratorTests`: cleanup-failure toast says translation was skipped when
  translation on.
- Config tests: `OutputLanguage` default "English", round-trips.
- Manual: dictate English → Spanish lands at cursor; Faithful vs Polished
  Spanish spot-check; dictionary term survives untranslated; Ollama stopped →
  English + toast.

## Non-goals

Speech-in-Spanish → English (Whisper `task=translate` — separate feature);
multi-language UI beyond English+Spanish (config already supports any name);
translation verification/back-translation; per-app language profiles; changing
the live pill's interim language.
