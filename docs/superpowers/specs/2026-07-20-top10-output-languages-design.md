# DeadAir Top-10 Output Languages + Model Upgrade — Design Spec

Extend output-language translation from English/Spanish to the ten most-spoken
languages, and upgrade the cleanup/translation model so all of them actually
work. Approved 2026-07-20.

## Problem

The output-language feature (PR #5) seeds only English + Spanish in the UI,
though the backend is already free-form — `OutputLanguage` is substituted
straight into `Prompts.TranslationTemplate`, so any language the LLM knows
works today by hand-editing config.

Two real gaps:

1. **Model coverage.** `qwen2.5:7b`'s official language list has no Hindi,
   Bengali, or Urdu — three of the top-10 most-spoken languages. Seeding them
   in the dropdown against this model would overpromise.
2. **Tray UX.** The tray has a single "Translate → {lang}" on/off toggle; the
   language itself can only change in Settings. With 11 possible targets the
   toggle doesn't scale.

## Decisions (from brainstorming)

- **Language set: top-10 by speaker count** added to the existing two:
  Mandarin Chinese, Hindi, Arabic, French, Bengali, Portuguese, Russian,
  Urdu, Indonesian, German. Dropdown total: 12 entries (English = off,
  Spanish, plus these ten).
- **Model: `qwen3:8b`** (5.2 GB pull, ~6.6 GB resident). 119-language
  coverage including the full top-10. Requires `think:false` in the
  `/api/generate` body (qwen3 otherwise emits `<think>` blocks into the
  concatenated stream); verified accepted-and-ignored by non-thinking models
  on the installed Ollama v0.31.1, so the client sends it unconditionally.
  - *(Amended 2026-07-20 after rollout measurement.)* `gemma3:12b` was the
    original pick but was reversed by hardware evidence: it needs ~11–12 GB
    resident, and this desktop's real free VRAM beside whisper-server, dwm,
    and overlays is ~11 GB (less mid-game) — measured 81% CPU offload and
    4 tok/s ≈ 13 s per utterance. qwen3:8b fits the actual budget; its
    earlier thinking-mode objection dissolved when `think:false` proved safe
    unconditionally on this runtime.
  - Staying on `qwen2.5:7b` rejected: the Indic gap above.
- **Tray: submenu, radio-style.** "Translate →" parent opens a language list
  (Off/English + the 11 languages); exactly one child checked.
- **No config auto-migration.** The shipped default for `Ollama.Model`
  changes, but an existing `config.json` still holds `qwen2.5:7b` — the user
  flips the Settings field once after pulling the model. Migration code
  rejected (YAGNI, effectively one install).

## Changes

### 1. Language catalog (new, `DeadAir.Core`)

A single ordered source of truth so Settings XAML and the tray menu stop
duplicating language names:

```csharp
public static class LanguageCatalog
{
    // "English" first = translation off; then by speaker count.
    public static readonly IReadOnlyList<string> Languages = [
        "English", "Spanish", "Mandarin Chinese", "Hindi", "Arabic",
        "French", "Bengali", "Portuguese", "Russian", "Urdu",
        "Indonesian", "German"];
}
```

Both UIs build their items from this list at construction. The hardcoded
English/Spanish `ComboBoxItem`s leave the XAML.

### 2. Config (`AppConfig`)

- `OllamaConfig.Model` default: `"qwen2.5:7b"` → `"qwen3:8b"` *(amended
  2026-07-20; was `gemma3:12b`)*.
- `OllamaClient` request bodies (`CleanAsync` and `WarmUpAsync`) gain
  top-level `think: false` — sent unconditionally (verified ignored by
  non-thinking models on Ollama v0.31.1).
- `CleanupConfig.OutputLanguage` unchanged — still free-form, still
  case-insensitive English = off. Hand-edited languages outside the catalog
  keep working everywhere.

### 3. Settings window

- `OutputLanguageBox` populated from `LanguageCatalog.Languages` in code
  (constructor), not XAML literals.
- The existing hand-edited-language preservation logic (unknown value gets
  appended as an extra item and selected) is unchanged and now guards the
  catalog list instead of the two literals.

### 4. Tray submenu (`App.xaml.cs`)

- The checkable "Translate → {lang}" item becomes a parent `MenuItem`
  ("Translate → {current}" when active, "Translate → Off" when English) with
  one child per catalog entry: "Off (English)" then the 11 languages.
- Radio behavior: clicking a child sets
  `_config.Cleanup.OutputLanguage = thatLanguage` ("English" for Off) on the
  live config object — same transient semantics as today's toggle: applies to
  the next utterance immediately, persists only when Settings next saves.
  Exactly one child checked; parent header updates on every change.
- A hand-edited language not in the catalog appears as an extra child
  (mirrors the Settings combo pattern).
- `OnSettingsSaved` re-syncs: checked child + parent header follow the saved
  `OutputLanguage`.
- The `_translateLanguage` last-used-language field goes away — the submenu
  always lists every target, so there is nothing to remember.

### 5. Prompts

No change. `TranslationTemplate` is already `{language}`-parameterized and
mode-aware; the dictionary keep-terms clause is language-agnostic.

### 6. Model rollout (ops, not code)

1. `ollama pull qwen3:8b` (5.2 GB download) *(amended 2026-07-20)*.
2. Flip Settings → "Ollama model" to `qwen3:8b` once.
3. Existing warm-up + `keep_alive` logic applies unchanged to the new model.

## Not changing

Sidecar, Whisper engines, VAD, partials, injection mechanics, prompt
templates, skip-guard semantics, failure/toast paths. `OutputLanguage` and
`Ollama.Model` are host-only fields — the settings-save ASR-reconfigure guard
(from PR #5) already ensures neither bounces the ASR engine.

## Known risk (accepted)

The deferred non-modal-Settings stale-overwrite race (tray change + open
Settings window + save overwrites with stale value) gains more entry points:
11 submenu children instead of 1 toggle. Same defect class, same severity,
shared with the Polished toggle; stays backlogged per the PR #5 decision.

## Error handling

Unchanged. Ollama down/timeout → raw English injected + "translation skipped:
{reason}" toast, for every target language. Empty-transcript and skip-guard
behavior untouched.

## Testing

- `LanguageCatalog` tests: English first, Spanish second, count 12, no
  duplicates (order is contract — both UIs render it verbatim).
- `PromptBuilderTests`: parametrized over several new languages — directive
  present with `{language}` substituted (e.g. "Mandarin Chinese", "Arabic");
  English still means no directive.
- Config tests: `Ollama.Model` default is `gemma3:12b`; `OutputLanguage`
  round-trips a multi-word value ("Mandarin Chinese").
- Tray submenu construction: extract any branchy logic (child list assembly
  incl. the extra-child case, checked-state resolution) into pure helpers so
  it tests headless; the WPF wiring itself is smoke-only.
- Manual smoke: dictate → Hindi (Devanagari), Arabic (RTL), Mandarin (CJK)
  land correctly at the cursor — this verifies the injection path is clean
  for non-Latin and RTL scripts, the one genuine unknown; dictionary term
  survives untranslated in a non-Latin sentence; tray switch applies on next
  utterance without Settings; Ollama stopped → English + toast; latency
  spot-check on qwen3:8b (`ollama ps` shows 100% GPU — run with the game
  closed; a running game starves VRAM and CPU-splits any model).

## Non-goals

Config auto-migration; per-language model routing; speech-in-X → English;
translation-quality verification; localizing DeadAir's own UI; RTL layout in
the pill preview (interim text stays as-is); Bengali/Urdu quality tuning
beyond what gemma3:12b provides.
