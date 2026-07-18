# Output Language (Spoken-Word Translation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dictate in English, get the cleaned transcript injected in a selected target language (v1: Spanish) — translation folded into the existing single Ollama cleanup call.

**Architecture:** A new `cleanup.outputLanguage` config value ("English" = off) drives three small changes on the existing pipeline: `PromptBuilder` appends a translation directive to the Faithful/Polished system prompt, `OllamaClient` stops honoring the skip-guard while translating, and the `Orchestrator` failure toast becomes translation-aware. UI = one Settings ComboBox + one tray checkbox. Sidecar, Whisper, VAD, partials, injection: untouched.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Spec: `docs/superpowers/specs/2026-07-18-output-language-translation-design.md`.

## Global Constraints

- Branch: `feat/output-language`. Working dir for all dotnet commands: `host/`.
- `"English"` (any casing, surrounding whitespace ignored) and empty/whitespace `outputLanguage` mean translation OFF — behavior must be byte-identical to today, with one spec-amended exception: an empty/whitespace-only *transcript* now short-circuits before the LLM in all modes (see Task 3's empty-guard; previously a ≥skip-guard-length all-whitespace transcript made a pointless LLM call and toasted "empty LLM output").
- Config keys serialize camelCase automatically (`ConfigStore.Options`); do not add attributes for naming.
- Words never lost: every failure path still injects *something*.
- Never modify: `sidecar/**`, existing prompt text (`Prompts.Faithful` / `Prompts.Polished`), existing test assertions (only add).
- Before every commit: `dotnet build -c Release` and `dotnet test` (both from `host/`) must pass — zero warnings-as-errors changes, zero failed tests.
- Commit messages: Conventional Commits, subject ≤ 50 chars, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. The directing session commits and appends that trailer to every commit; workers do not run git. The `git commit -m` blocks in tasks show the subject line only.

---

### Task 1: Config — `OutputLanguage`, `TranslationActive`, `TranslationTemplate`

**Files:**
- Modify: `host/DeadAir.Core/Config/AppConfig.cs` (CleanupConfig ~line 56, PromptsConfig ~line 62)
- Test: `host/DeadAir.Core.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (later tasks rely on these exact members):
  - `CleanupConfig.OutputLanguage : string` (default `"English"`)
  - `CleanupConfig.TranslationActive : bool` (get-only, `[JsonIgnore]`)
  - `PromptsConfig.TranslationTemplate : string` (contains `{language}` and `{style}` tokens)

- [ ] **Step 1: Write the failing tests** — append to `ConfigStoreTests`:

```csharp
    [Fact]
    public void OutputLanguage_DefaultsToEnglish_TranslationOff()
    {
        var cfg = new AppConfig();
        Assert.Equal("English", cfg.Cleanup.OutputLanguage);
        Assert.False(cfg.Cleanup.TranslationActive);
    }

    [Theory]
    [InlineData("English", false)]
    [InlineData("english", false)]
    [InlineData("  ENGLISH  ", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("Spanish", true)]
    [InlineData("spanish", true)]
    [InlineData(" French ", true)]
    public void TranslationActive_ReflectsOutputLanguage(string? lang, bool active)
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = lang!;
        Assert.Equal(active, cfg.Cleanup.TranslationActive);
    }

    [Fact]
    public void OutputLanguage_RoundTrips_AndTranslationActiveNotSerialized()
    {
        var path = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json");
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        ConfigStore.Save(cfg, path);
        var rawJson = File.ReadAllText(path);
        Assert.Contains("\"outputLanguage\": \"Spanish\"", rawJson);
        Assert.DoesNotContain("translationActive", rawJson);
        Assert.Contains("translationTemplate", rawJson);
        var loaded = ConfigStore.Load(path);
        Assert.Equal("Spanish", loaded.Cleanup.OutputLanguage);
        Assert.True(loaded.Cleanup.TranslationActive);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: FAIL — `'CleanupConfig' does not contain a definition for 'OutputLanguage'` (compile error counts as the failing state).

- [ ] **Step 3: Implement** — in `AppConfig.cs` replace `CleanupConfig` with:

```csharp
public sealed class CleanupConfig
{
    public CleanupMode Mode { get; set; } = CleanupMode.Faithful;
    public int SkipGuardChars { get; set; } = 50;
    // "English" (any casing) or blank = translation off. Free-form otherwise —
    // the value is substituted into Prompts.TranslationTemplate, so any
    // language name the LLM knows works ("Spanish", "French", ...).
    public string OutputLanguage { get; set; } = "English";

    [JsonIgnore]
    public bool TranslationActive
    {
        get
        {
            var lang = OutputLanguage?.Trim();
            return !string.IsNullOrEmpty(lang) &&
                !string.Equals(lang, "English", StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

and add to `PromptsConfig` (after `Polished`):

```csharp
    public string TranslationTemplate { get; set; } =
        "After applying the rules above, render the transcript in {language}. " +
        "The translation must be {style}. Keep technical terms, product names, " +
        "commands, file paths, and any listed dictionary terms in their original " +
        "language. Output ONLY the {language} text with no preamble.";
```

(`using System.Text.Json.Serialization;` already present at the top of the file.)

- [ ] **Step 4: Run tests to verify they pass**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: PASS (all, including the 2 pre-existing tests).

- [ ] **Step 5: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: build succeeds, full suite green.

```bash
git add host/DeadAir.Core/Config/AppConfig.cs host/DeadAir.Core.Tests/ConfigStoreTests.cs
git commit -m "feat(config): outputLanguage + prompt template"
```

---

### Task 2: PromptBuilder — append translation directive

**Files:**
- Modify: `host/DeadAir.Core/Cleanup/PromptBuilder.cs` (whole `Build` method)
- Test: `host/DeadAir.Core.Tests/PromptBuilderTests.cs`

**Interfaces:**
- Consumes (Task 1): `cfg.Cleanup.TranslationActive`, `cfg.Cleanup.OutputLanguage`, `cfg.Prompts.TranslationTemplate`.
- Produces: `PromptBuilder.Build(CleanupMode, AppConfig)` — signature unchanged; when translating, returned prompt is base + `"\n"` + filled template (+ existing dictionary suffix last).

- [ ] **Step 1: Write the failing tests** — append to `PromptBuilderTests`:

```csharp
    [Fact]
    public void EnglishOutput_AnyCasing_NoTranslationDirective()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "english";
        Assert.Equal(cfg.Prompts.Faithful,
            PromptBuilder.Build(CleanupMode.Faithful, cfg));
    }

    [Fact]
    public void SpanishOutput_AppendsFilledDirective_AfterBasePrompt()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var p = PromptBuilder.Build(CleanupMode.Faithful, cfg);
        Assert.StartsWith(cfg.Prompts.Faithful, p);
        Assert.Contains("render the transcript in Spanish", p);
        Assert.Contains("ONLY the Spanish text", p);
        Assert.DoesNotContain("{language}", p);
        Assert.DoesNotContain("{style}", p);
    }

    [Fact]
    public void TranslationStyle_TracksCleanupMode()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        Assert.Contains("literal", PromptBuilder.Build(CleanupMode.Faithful, cfg));
        Assert.Contains("natural and fluent",
            PromptBuilder.Build(CleanupMode.Polished, cfg));
    }

    [Fact]
    public void SpanishOutput_DictionarySuffix_StaysLast()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = " Spanish ";
        cfg.Dictionary.Add("DeadMind");
        var p = PromptBuilder.Build(CleanupMode.Polished, cfg);
        Assert.Contains("render the transcript in Spanish", p);
        Assert.True(p.IndexOf("render the transcript") < p.IndexOf("DeadMind"),
            "dictionary suffix must come after the translation directive");
        Assert.EndsWith("DeadMind.", p);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~PromptBuilderTests"`
Expected: FAIL — 3 new tests fail (no directive appended); `EnglishOutput_...` passes trivially (that's fine, it pins the off-behavior).

- [ ] **Step 3: Implement** — replace the `Build` method body:

```csharp
    public static string Build(CleanupMode mode, AppConfig cfg)
    {
        var prompt = mode == CleanupMode.Faithful
            ? cfg.Prompts.Faithful : cfg.Prompts.Polished;
        if (cfg.Cleanup.TranslationActive)
        {
            var style = mode == CleanupMode.Faithful
                ? "literal, preserving the speaker's register and tone"
                : "natural and fluent";
            prompt += "\n" + cfg.Prompts.TranslationTemplate
                .Replace("{language}", cfg.Cleanup.OutputLanguage!.Trim())
                .Replace("{style}", style);
        }
        if (cfg.Dictionary.Count == 0) return prompt;
        return prompt +
            "\nPreserve these terms exactly, correcting near-misspellings " +
            "to them: " + string.Join(", ", cfg.Dictionary) + ".";
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~PromptBuilderTests"`
Expected: PASS (6 total: 2 pre-existing + 4 new).

- [ ] **Step 5: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green.

```bash
git add host/DeadAir.Core/Cleanup/PromptBuilder.cs host/DeadAir.Core.Tests/PromptBuilderTests.cs
git commit -m "feat(cleanup): translation directive in prompt"
```

---

### Task 3: OllamaClient — skip-guard off while translating

**Files:**
- Modify: `host/DeadAir.Core/Cleanup/OllamaClient.cs` (the guard at ~line 53)
- Test: `host/DeadAir.Core.Tests/OllamaClientTests.cs`

**Interfaces:**
- Consumes (Task 1): `cfg.Cleanup.TranslationActive`. Consumes (Task 2): translated system prompt arrives via existing `PromptBuilder.Build` call.
- Produces: unchanged `CleanAsync` signature; short transcripts now reach the LLM when translating.

- [ ] **Step 1: Write the failing tests** — append to `OllamaClientTests` (reuse the file-local `StubHandler` / `CapturingHandler` exactly as the existing tests do):

```csharp
    [Fact]
    public async Task EmptyTranscript_Translating_SkipsLlm()
    {
        // A null "final" payload reaches CleanAsync as "" (Orchestrator uses
        // e.Text ?? ""). Never worth an LLM call, translating or not — and the
        // "below skip guard" reason keeps the Orchestrator's toast suppressed.
        var handler = new StubHandler(_ => throw new Exception("must not call"));
        var cfg = Cfg();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var client = new OllamaClient(cfg, handler);
        var r = await client.CleanAsync("", CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal("below skip guard", r.Reason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ShortTranscript_Translating_StillCallsLlm()
    {
        var ndjson = "{\"response\":\"hola\",\"done\":true}\n";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ndjson, Encoding.UTF8) });
        var cfg = Cfg();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var client = new OllamaClient(cfg, handler);
        var r = await client.CleanAsync("hi there", CleanupMode.Faithful);
        Assert.False(r.Skipped);
        Assert.Equal("hola", r.Text);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Contains("render the transcript in Spanish",
            doc.RootElement.GetProperty("system").GetString());
    }

    [Fact]
    public async Task ShortTranscript_EnglishOutput_StillSkipsLlm()
    {
        var handler = new StubHandler(_ => throw new Exception("must not call"));
        var cfg = Cfg();
        cfg.Cleanup.OutputLanguage = "english";
        var client = new OllamaClient(cfg, handler);
        var r = await client.CleanAsync("hi there", CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal("hi there", r.Text);
        Assert.Equal(0, handler.Calls);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~OllamaClientTests"`
Expected: `ShortTranscript_Translating_StillCallsLlm` FAILS (`r.Skipped` is true, reason "below skip guard"); the other two new tests pass already (they pin today's off/empty behavior).

- [ ] **Step 3: Implement** — replace the guard at the top of `CleanAsync`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~OllamaClientTests"`
Expected: PASS (14 total: 11 pre-existing + 3 new).

- [ ] **Step 5: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green.

```bash
git add host/DeadAir.Core/Cleanup/OllamaClient.cs host/DeadAir.Core.Tests/OllamaClientTests.cs
git commit -m "feat(cleanup): skip-guard off while translating"
```

---

### Task 4: Orchestrator — translation-aware failure toast

**Files:**
- Modify: `host/DeadAir.Core/Orchestrator.cs` (`HandleFinalAsync`, the toast at ~lines 124-125)
- Test: `host/DeadAir.Core.Tests/OrchestratorTests.cs`

**Interfaces:**
- Consumes (Task 1): `config.Cleanup.TranslationActive` (primary-constructor parameter `config` is already in scope).
- Produces: toast copy contract — translating: `"translation skipped: {Reason}"`; not translating: existing `"cleanup skipped: {Reason}"` unchanged. (Deliberately no "injected English" claim: the toast fires before `InjectAsync` runs, and a failed insert already gets its own Ctrl+V toast.)

- [ ] **Step 1: Write the failing test** — append to `OrchestratorTests` (fakes `FakeSidecar`/`FakeCleaner`/`FakeInjector`/`FakeNotifier` already exist at the top of the file; construct the `Orchestrator` directly to pass a custom config, as `Make` hardcodes `new AppConfig()`):

```csharp
    [Fact]
    public async Task CleanupFailure_WhileTranslating_ToastsTranslationSkipped()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var cl = new FakeCleaner(new CleanupResult("raw words here that are long",
            true, "connection refused"));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = new Orchestrator(new FakeSidecar(), cl, inj, n, cfg);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw words here that are long" });

        Assert.Equal("raw words here that are long", inj.Injected);
        Assert.Contains(n.Toasts, t =>
            t.Contains("translation skipped") && t.Contains("connection refused"));
        Assert.DoesNotContain(n.Toasts, t => t.Contains("cleanup skipped"));
        // The toast fires before injection, so it must not claim the English
        // text already landed.
        Assert.DoesNotContain(n.Toasts, t => t.Contains("injected"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~OrchestratorTests"`
Expected: the new test FAILS (toast says "cleanup skipped"); all pre-existing pass.

- [ ] **Step 3: Implement** — in `HandleFinalAsync` replace:

```csharp
            if (result.Skipped && result.Reason != "below skip guard")
                notifier.Toast($"cleanup skipped: {result.Reason}");
```

with:

```csharp
            if (result.Skipped && result.Reason != "below skip guard")
                notifier.Toast(config.Cleanup.TranslationActive
                    ? $"translation skipped: {result.Reason}"
                    : $"cleanup skipped: {result.Reason}");
```

- [ ] **Step 4: Run test to verify it passes**

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~OrchestratorTests"`
Expected: PASS (all).

- [ ] **Step 5: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green.

```bash
git add host/DeadAir.Core/Orchestrator.cs host/DeadAir.Core.Tests/OrchestratorTests.cs
git commit -m "feat(orchestrator): translation-aware skip toast"
```

---

### Task 5: Settings window — Output language picker

**Files:**
- Modify: `host/DeadAir.App/SettingsWindow.xaml` (after the `ModeBox` block, ~line 38)
- Modify: `host/DeadAir.App/SettingsWindow.xaml.cs` (ctor, `Select`, `OnSave`)

**Interfaces:**
- Consumes (Task 1): `_config.Cleanup.OutputLanguage`.
- Produces: ComboBox `x:Name="OutputLanguageBox"` seeded `English`/`Spanish`; a hand-edited config language (e.g. `"French"`) appears as a third item and survives open→save; `Select` becomes case-insensitive (safe: every existing caller matches exact-case values). Note: a Settings **save** still runs the whole `OnSettingsSaved` path — Task 6b is what keeps a language-only save from bouncing the sidecar's ASR engine.

No unit-test project covers `DeadAir.App` (WPF); this task is verified by compile + the Task 7 gate + manual smoke. Do not add a UI test framework.

- [ ] **Step 1: XAML** — insert between the `ModeBox` ComboBox block and the "Scope skin" TextBlock:

```xml
            <TextBlock FontWeight="Bold" Text="Output language"/>
            <ComboBox x:Name="OutputLanguageBox" Margin="0,4,0,12">
                <ComboBoxItem Content="English"/>
                <ComboBoxItem Content="Spanish"/>
            </ComboBox>
```

- [ ] **Step 2: Code-behind** — in the constructor, after `Select(ModeBox, _config.Cleanup.Mode.ToString());` insert:

```csharp
        var outputLang = string.IsNullOrWhiteSpace(_config.Cleanup.OutputLanguage)
            ? "English" : _config.Cleanup.OutputLanguage.Trim();
        // Hand-edited languages (config is free-form) must survive the
        // settings round-trip, not silently snap back to English.
        if (!OutputLanguageBox.Items.Cast<ComboBoxItem>().Any(i =>
                string.Equals((string)i.Content, outputLang,
                    StringComparison.OrdinalIgnoreCase)))
            OutputLanguageBox.Items.Add(new ComboBoxItem { Content = outputLang });
        Select(OutputLanguageBox, outputLang);
```

Change `Select`'s comparison from `(string)item.Content == value` to:

```csharp
            if (string.Equals((string)item.Content, value,
                    StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = item; return; }
```

In `OnSave`, after the `_config.Cleanup.Mode` line insert:

```csharp
        _config.Cleanup.OutputLanguage = Selected(OutputLanguageBox);
```

Add `using System.Linq;` to the file's usings if the compiler asks for it (`Cast`/`Any`).

- [ ] **Step 3: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green (XAML compile is part of the build).

```bash
git add host/DeadAir.App/SettingsWindow.xaml host/DeadAir.App/SettingsWindow.xaml.cs
git commit -m "feat(ui): output-language picker in settings"
```

---

### Task 6: Tray — "Translate → Spanish" toggle

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs` (fields ~line 22, `BuildMenu` ~line 171, `OnSettingsSaved` ~line 224)

**Interfaces:**
- Consumes (Task 1): `_config.Cleanup.OutputLanguage` / `.TranslationActive`.
- Produces: checkable tray item under "Polished mode". Checked ⇄ `OutputLanguage = <last non-English language>` (default `"Spanish"`), unchecked ⇄ `"English"`. Transient like the Polished toggle: persisted only when Settings is next saved. Settings-save re-syncs header + check state.

- [ ] **Step 1: Fields** — next to `_modeMenuItem` add:

```csharp
    private System.Windows.Controls.MenuItem _translateMenuItem = null!;
    private string _translateLanguage = "Spanish";
```

- [ ] **Step 2: BuildMenu** — after the `_modeMenuItem = mode;` line insert:

```csharp
        // Like the Polished toggle this is transient: it flips the live config
        // object (OllamaClient reads it per-utterance) but persists only when
        // Settings is next saved.
        _translateLanguage = _config.Cleanup.TranslationActive
            ? _config.Cleanup.OutputLanguage.Trim() : "Spanish";
        var translate = new System.Windows.Controls.MenuItem
        {
            Header = $"Translate → {_translateLanguage}",
            IsCheckable = true,
            IsChecked = _config.Cleanup.TranslationActive,
            Style = itemStyle,
        };
        translate.Checked += (_, _) =>
            _config.Cleanup.OutputLanguage = _translateLanguage;
        translate.Unchecked += (_, _) =>
            _config.Cleanup.OutputLanguage = "English";
        _translateMenuItem = translate;
```

and add it to the menu right after `menu.Items.Add(mode);`:

```csharp
        menu.Items.Add(translate);
```

- [ ] **Step 3: OnSettingsSaved** — after the `_modeMenuItem.IsChecked = ...` line insert:

```csharp
            if (_config.Cleanup.TranslationActive)
                _translateLanguage = _config.Cleanup.OutputLanguage.Trim();
            _translateMenuItem.Header = $"Translate → {_translateLanguage}";
            // Setter fires Checked/Unchecked; both handlers re-assign the same
            // value OutputLanguage already holds, so this is idempotent.
            _translateMenuItem.IsChecked = _config.Cleanup.TranslationActive;
```

- [ ] **Step 4: Full gate + commit**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green.

```bash
git add host/DeadAir.App/App.xaml.cs
git commit -m "feat(ui): tray translate toggle"
```

---

### Task 6b: Skip sidecar reconfig when only host-side settings changed

The sidecar recreates its ASR engine on EVERY `config` command (`sidecar/asr_sidecar/__main__.py`, `cmd == "config"` branch: stops partials, closes engine, cancels capture, respawns — on the GPU path that reloads the model into VRAM). Today every Settings save pays that. `ConfigCommand.From` only carries ASR-relevant fields (engine, models, mic, dictionary, gpu paths/port, partials), so a JSON compare at the save call-site lets host-only changes (output language, cleanup mode, Ollama, pill, prompts) apply without bouncing ASR.

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs` (fields ~line 22, `OnStartup` sidecar-launch try ~line 152, `OnSettingsSaved` ~line 233)
- Test: `host/DeadAir.Core.Tests/SidecarCommandTests.cs`

**Interfaces:**
- Consumes: `ConfigCommand.From(AppConfig)` (existing, `DeadAir.Core.Sidecar` — already in App's usings).
- Produces: behavior only — `OnSettingsSaved` calls `_sidecar.SendConfigAsync` solely when the serialized `ConfigCommand` changed since last send.

- [ ] **Step 1: Write the failing test** — append to `SidecarCommandTests` (add `using DeadAir.Core.Config;` and `using System.Text.Json;` to that file if not present):

```csharp
    [Fact]
    public void ConfigCommand_Json_UnaffectedByHostOnlySettings()
    {
        // Pins the contract Task 6b's dedup relies on: host-only settings must
        // not change the sidecar config payload, ASR settings must.
        var cfg = new AppConfig();
        var before = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        cfg.Cleanup.OutputLanguage = "Spanish";
        cfg.Cleanup.Mode = CleanupMode.Polished;
        cfg.Ollama.Model = "someother:3b";
        cfg.Prompts.TranslationTemplate = "changed";
        var after = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        Assert.Equal(before, after);

        cfg.Asr.Engine = "cpu";
        Assert.NotEqual(after, JsonSerializer.Serialize(ConfigCommand.From(cfg)));
    }
```

- [ ] **Step 2: Run test to verify it passes already** (it pins existing `ConfigCommand.From` behavior — if it FAILS, stop: the dedup premise is wrong, report instead of proceeding)

Run (from `host/`): `dotnet test --filter "FullyQualifiedName~SidecarCommandTests"`
Expected: PASS.

- [ ] **Step 3: Implement the dedup** — in `App.xaml.cs`:

Field, next to `_translateLanguage`:

```csharp
    private string _lastSidecarConfigJson = "";
```

In `OnStartup`, replace:

```csharp
        try { await _sidecar.LaunchAsync(); }
        catch (Exception ex)
        { notifier.Toast($"Sidecar failed to start: {ex.Message}"); }
```

with:

```csharp
        try
        {
            await _sidecar.LaunchAsync();
            // LaunchAsync sent the full config; remember what the sidecar has
            // so host-only settings saves don't bounce the ASR engine.
            _lastSidecarConfigJson = System.Text.Json.JsonSerializer.Serialize(
                Core.Sidecar.ConfigCommand.From(_config));
        }
        catch (Exception ex)
        { notifier.Toast($"Sidecar failed to start: {ex.Message}"); }
```

In `OnSettingsSaved`, replace:

```csharp
            await _sidecar.SendConfigAsync(_config); // hot-reload sidecar side
```

with:

```csharp
            // The sidecar tears down + recreates the ASR engine on EVERY config
            // command, so only send one when an ASR-relevant field actually
            // changed (ConfigCommand carries nothing else). A failed initial
            // launch leaves the baseline "" and the next save re-sends.
            var sidecarJson = System.Text.Json.JsonSerializer.Serialize(
                Core.Sidecar.ConfigCommand.From(_config));
            if (sidecarJson != _lastSidecarConfigJson)
            {
                await _sidecar.SendConfigAsync(_config); // hot-reload sidecar side
                _lastSidecarConfigJson = sidecarJson;
            }
```

(Crash-restart is unaffected: `SidecarManager.LaunchAsync` re-sends the full config itself on every respawn.)

- [ ] **Step 4: Full gate**

Run (from `host/`): `dotnet build -c Release` then `dotnet test`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add host/DeadAir.App/App.xaml.cs host/DeadAir.Core.Tests/SidecarCommandTests.cs
git commit -m "feat(ui): skip sidecar reconfig on host-only saves"
```

---

### Task 7: Docs + final verification

**Files:**
- Modify: `README.md` (Features list ~line 63, Settings bullet ~line 195, config table ~line 213)
- Modify: `docs/spec.md` (OllamaClient paragraph ~line 147, config JSON ~line 276, error table ~line 341)

- [ ] **Step 1: README** — Features list, after the "Local LLM cleanup" bullet block, add a bullet:

```markdown
- **Output language (translation)** — optionally have your English dictation
  injected in another language (v1 ships English + Spanish in the UI; the
  config value is free-form). Translation happens in the same single local
  LLM call as cleanup — no second LLM round-trip, nothing leaves your machine.
  (One caveat: short phrases that normally skip the LLM entirely do make the
  call when translating, so they gain that one call's latency.)
  Faithful/Polished still applies (literal vs natural translation), dictionary
  and technical terms stay in English, and the skip-guard is bypassed so short
  phrases translate too. If Ollama is unavailable the raw English is injected
  with a "translation skipped" toast. Toggle from the tray ("Translate →
  Spanish") or pick the language in Settings.
```

In the Settings sentence (Usage section) change "mic device, and edit the custom dictionary." to "mic device, output language, and edit the custom dictionary." In the config table, after the `cleanup` row add:

```markdown
| `cleanup` | `outputLanguage` | `English` | Target language for injected text. `English` = translation off. Free-form (UI lists English + Spanish). |
```

- [ ] **Step 2: spec.md** — in the **OllamaClient** paragraph (§ Host components), after the skip-guard sentence ending "(small models mangle tiny inputs) and are injected raw." add:

```markdown
**Output language (added 2026-07-18):** when `cleanup.outputLanguage` is a
language other than English, the system prompt gains a translation directive
(`prompts.translationTemplate`, `{language}`/`{style}` tokens — style tracks
Faithful=literal / Polished=natural) and the skip-guard is disabled so short
utterances still translate. Dictionary/technical terms stay untranslated. On
LLM failure the raw English transcript is injected with a "translation
skipped" toast (the toast makes no injection claim — it fires before the
injector runs).
```

In the config JSON example change the cleanup line to:

```json
  "cleanup": { "mode": "Faithful", "skipGuardChars": 50, "outputLanguage": "English" },
```

and the prompts line to:

```json
  "prompts": { "faithful": "<full text in §5>", "polished": "<full text in §5>", "translationTemplate": "<{language}/{style} directive — defaults in AppConfig.PromptsConfig>" },
```

In the error-handling table, after the `Transcript < skipGuardChars` row add:

```markdown
| Transcript < skipGuardChars, translating | LLM still called (guard bypassed) so the utterance is translated. |
```

- [ ] **Step 3: Final verification gate**

Run (from `host/`): `dotnet build -c Release` and `dotnet test -c Release`
Expected: green. Sidecar untouched (verify `git status` shows no `sidecar/` changes) — pytest not required.

Manual smoke (user or session with mic): run `DeadAir.App.exe`, then in this order:
1. Settings → Output language = Spanish → dictate "hello how are you today my friend" → Spanish lands at cursor (Faithful: literal).
2. Tray → check "Polished mode" → dictate an awkward run-on → natural, fluent Spanish.
3. Add a dictionary term (e.g. `DeadMind`) in Settings → dictate a sentence using it → term appears untranslated inside the Spanish sentence.
4. **With Spanish still ON**, stop Ollama → dictate anything → raw English lands + "translation skipped" toast (order matters: toggling translation off first would legitimately produce "cleanup skipped" instead).
5. Restart Ollama, tray toggle "Translate → Spanish" OFF → dictate → English out, behavior identical to pre-feature.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/spec.md
git commit -m "docs: output-language translation"
```
