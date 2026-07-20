# DeadAir Top-10 Output Languages + Model Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend DeadAir's output-language selection to English/off plus Spanish and the ten most-spoken additional languages, and make `gemma3:12b` the default model for new configurations.

**Architecture:** `DeadAir.Core` gains one ordered `LanguageCatalog` shared by Settings and the tray, plus a pure `TranslationMenuBuilder` that projects a free-form configured language into a headless-testable parent header and radio-child list. Existing cleanup translation remains one parameterized Ollama call; WPF only renders the catalog/model and applies tray selections to the live `AppConfig`, while `ConfigStore.Save` remains confined to Settings save.

**Tech Stack:** C# / .NET 8, WPF, xUnit, H.NotifyIcon.Wpf, Ollama `gemma3:12b`. Authoritative spec: `docs/superpowers/specs/2026-07-20-top10-output-languages-design.md`.

## Global Constraints

- Branch: `feat/top10-languages`. Source-reading baseline: `37e139f` (spec commit rebased onto master `e6447b0`, which includes the merged pill-persistence work); workers start from the current branch HEAD, which also carries this plan.
- The ordered catalog is exactly: `English`, `Spanish`, `Mandarin Chinese`, `Hindi`, `Arabic`, `French`, `Bengali`, `Portuguese`, `Russian`, `Urdu`, `Indonesian`, `German`. `English` is translation off; the remaining entries are rendered in this order.
- `CleanupConfig.OutputLanguage` remains free-form. Blank/whitespace and `English` in any casing remain translation off. A hand-edited value outside the catalog must survive Settings open/save and must appear as a checked extra tray child.
- Change only the new-config default `OllamaConfig.Model` from `qwen2.5:7b` to `gemma3:12b`. Do not add config migration: an existing `config.json` retains its stored model until the user changes it in Settings.
- Tray selection is transient until Settings saves: clicking a language child updates only the live `_config.Cleanup.OutputLanguage`, applies to the next utterance, and must not call `ConfigStore.Save`.
- Exactly one tray child is checked. The parent reads `Translate → Off` for English and `Translate → {language}` otherwise. The first child reads `Off (English)`; the other children use their language names.
- Preserve the accepted non-modal Settings stale-overwrite race. Do not attempt to fix it for either language selection or Polished mode.
- Do not change sidecar, Whisper engines, VAD, partials, injection mechanics, prompt templates, skip-guard semantics, failure/toast paths, ASR reconfigure behavior, ScopeGeometry, pill, or sidecar prompts.
- All test commands target `host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj` and include `--no-restore`. The measured starting baseline is 182 xUnit cases (controller-run post-rebase, 2026-07-20); do not infer a different baseline from task-local filtered totals.
- Workers never run source-control commands. Every task ends ready for the controller to commit.

### Explicit no-code-change map

- **Problem / decisions:** Tasks 1–3 supply the 12-language catalog, the broad-coverage model default, and the tray submenu selected in the design.
- **Prompts:** Task 1 adds only coverage for new multi-word/non-Latin language names. `host/DeadAir.Core/Cleanup/PromptBuilder.cs`, `PromptsConfig.TranslationTemplate`, Faithful/Polished wording, dictionary suffix placement, and the single `/api/generate` call remain unchanged.
- **Model rollout:** Task 4 contains the controller-only `ollama pull gemma3:12b` and one-time Settings change. Existing warm-up and `keep_alive` behavior remain unchanged.
- **Not changing / error handling:** The untouched systems listed above retain the existing raw-English injection plus `translation skipped: {reason}` toast on Ollama failure, along with the empty-transcript and skip-guard behavior.
- **Known risk:** The accepted non-modal Settings stale-overwrite race is documented as a constraint, not fixed.
- **Non-goals:** No config auto-migration, per-language model routing, speech-in-X to English, translation-quality automation, DeadAir UI localization, RTL pill-preview work, or Bengali/Urdu tuning is added.

---

### Task 1: Ordered language catalog and prompt contract coverage

**Files:**
- Create: `host/DeadAir.Core/Config/LanguageCatalog.cs`
- Create: `host/DeadAir.Core.Tests/LanguageCatalogTests.cs`
- Modify: `host/DeadAir.Core.Tests/PromptBuilderTests.cs`
- Test: `host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj`

**Interfaces:**
- Consumes: existing `PromptBuilder.Build(CleanupMode mode, AppConfig cfg) : string` and `CleanupConfig.OutputLanguage : string`.
- Produces: `LanguageCatalog.Languages : IReadOnlyList<string>`, ordered exactly as rendered by both UIs.

- [ ] **Step 1: Write the failing tests**

Create `host/DeadAir.Core.Tests/LanguageCatalogTests.cs` with the complete file:

```csharp
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class LanguageCatalogTests
{
    [Fact]
    public void Languages_EnglishIsFirst()
    {
        Assert.Equal("English", LanguageCatalog.Languages[0]);
    }

    [Fact]
    public void Languages_SpanishIsSecond()
    {
        Assert.Equal("Spanish", LanguageCatalog.Languages[1]);
    }

    [Fact]
    public void Languages_HasExactOrderedTwelveEntries()
    {
        Assert.Equal(new[]
        {
            "English", "Spanish", "Mandarin Chinese", "Hindi", "Arabic",
            "French", "Bengali", "Portuguese", "Russian", "Urdu",
            "Indonesian", "German",
        }, LanguageCatalog.Languages);
        Assert.Equal(12, LanguageCatalog.Languages.Count);
    }

    [Fact]
    public void Languages_HasNoCaseInsensitiveDuplicates()
    {
        Assert.Equal(LanguageCatalog.Languages.Count,
            LanguageCatalog.Languages
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }
}
```

Append this complete test method to `PromptBuilderTests`:

```csharp
    [Theory]
    [InlineData("Mandarin Chinese")]
    [InlineData("Arabic")]
    public void NewLanguageOutput_AppendsFilledDirective(string language)
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = language;

        var prompt = PromptBuilder.Build(CleanupMode.Faithful, cfg);

        Assert.Contains($"render the transcript in {language}", prompt);
        Assert.Contains($"ONLY the {language} text", prompt);
        Assert.DoesNotContain("{language}", prompt);
        Assert.DoesNotContain("{style}", prompt);
    }
```

The existing `EnglishOutput_AnyCasing_NoTranslationDirective` test remains unchanged and continues to pin English-as-off.

- [ ] **Step 2: Run tests to verify they fail**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~LanguageCatalogTests|FullyQualifiedName~PromptBuilderTests"`

Expected: FAIL at compile time with `CS0103: The name 'LanguageCatalog' does not exist in the current context`. The new prompt theory requires no production prompt change because the existing parameterized implementation already supports both values.

- [ ] **Step 3: Write the minimal implementation**

Create `host/DeadAir.Core/Config/LanguageCatalog.cs` with the complete file:

```csharp
namespace DeadAir.Core.Config;

public static class LanguageCatalog
{
    // "English" first = translation off; then by speaker count.
    public static readonly IReadOnlyList<string> Languages =
    [
        "English", "Spanish", "Mandarin Chinese", "Hindi", "Arabic",
        "French", "Bengali", "Portuguese", "Russian", "Urdu",
        "Indonesian", "German",
    ];
}
```

Do not modify `host/DeadAir.Core/Cleanup/PromptBuilder.cs`; its existing `{language}` substitution is the minimal implementation for the added characterization cases.

- [ ] **Step 4: Run tests to verify they pass**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~LanguageCatalogTests|FullyQualifiedName~PromptBuilderTests"`

Expected: PASS with 12 cases total: four catalog facts, the six existing prompt facts, and two new prompt theory cases.

- [ ] **Step 5: Run the full test-project gate**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore`

Expected: `Failed: 0, Passed: 188, Skipped: 0, Total: 188`.

- [ ] **Step 6: Ready for controller commit**

Controller commit scope: the new catalog, its tests, and the new PromptBuilder characterization theory only.

---

### Task 2: `gemma3:12b` default and multi-word config round-trip

**Files:**
- Modify: `host/DeadAir.Core/Config/AppConfig.cs`
- Modify: `host/DeadAir.Core.Tests/ConfigStoreTests.cs`
- Test: `host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj`

**Interfaces:**
- Consumes: existing `ConfigStore.Load(string path) : AppConfig`, `ConfigStore.Save(AppConfig config, string path) : void`, and free-form `CleanupConfig.OutputLanguage : string`.
- Produces: `OllamaConfig.Model : string` with new-instance/default value `"gemma3:12b"`; serialized model values and existing config files remain authoritative when present.

- [ ] **Step 1: Write the failing tests**

Replace `Load_MissingFile_ReturnsDefaults` in `ConfigStoreTests` with this complete method:

```csharp
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigStore.Load(Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json"));
        Assert.Equal("RControl", cfg.Hotkey.Key);
        Assert.Equal(CleanupMode.Faithful, cfg.Cleanup.Mode);
        Assert.Equal(50, cfg.Cleanup.SkipGuardChars);
        Assert.Equal("gemma3:12b", cfg.Ollama.Model);
        Assert.Equal(8192, cfg.Ollama.NumCtx);
        Assert.Equal("30m", cfg.Ollama.KeepAlive);
    }
```

Replace `OutputLanguage_RoundTrips_AndTranslationActiveNotSerialized` with this complete method:

```csharp
    [Fact]
    public void OutputLanguage_RoundTrips_MultiWordValue_AndTranslationActiveNotSerialized()
    {
        var path = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json");
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Mandarin Chinese";
        ConfigStore.Save(cfg, path);

        var rawJson = File.ReadAllText(path);
        Assert.Contains("\"outputLanguage\": \"Mandarin Chinese\"", rawJson);
        Assert.DoesNotContain("translationActive", rawJson);
        Assert.Contains("translationTemplate", rawJson);

        var loaded = ConfigStore.Load(path);
        Assert.Equal("Mandarin Chinese", loaded.Cleanup.OutputLanguage);
        Assert.True(loaded.Cleanup.TranslationActive);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigStoreTests"`

Expected: FAIL in `Load_MissingFile_ReturnsDefaults` with `Expected: gemma3:12b` and `Actual: qwen2.5:7b`. The multi-word round-trip passes already, proving that no migration or serialization change is needed.

- [ ] **Step 3: Write the minimal implementation**

Replace `OllamaConfig` in `host/DeadAir.Core/Config/AppConfig.cs` with this complete class; only the model initializer changes:

```csharp
public sealed class OllamaConfig
{
    // 127.0.0.1, not "localhost": Windows resolves localhost to ::1 first, Ollama
    // binds IPv4 loopback only, and each refused connect costs a ~2s Winsock
    // SYN-retry. OllamaClient also rewrites a configured "localhost" defensively.
    public string Url { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "gemma3:12b";
    public int NumCtx { get; set; } = 8192;
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 20;
    public string KeepAlive { get; set; } = "30m";
}
```

Do not touch `ConfigStore`: missing files receive the initializer, while deserialized existing `config.json` values continue to win without auto-migration.

- [ ] **Step 4: Run tests to verify they pass**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigStoreTests"`

Expected: PASS with all 13 ConfigStore cases.

- [ ] **Step 5: Run the full test-project gate**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore`

Expected: `Failed: 0, Passed: 188, Skipped: 0, Total: 188`.

- [ ] **Step 6: Ready for controller commit**

Controller commit scope: the one default-model initializer and the two exact ConfigStore test updates.

---

### Task 3: Catalog-backed Settings combo and radio-style tray submenu

**Files:**
- Create: `host/DeadAir.Core/Config/TranslationMenuBuilder.cs`
- Create: `host/DeadAir.Core.Tests/TranslationMenuBuilderTests.cs`
- Modify: `host/DeadAir.App/SettingsWindow.xaml`
- Modify: `host/DeadAir.App/SettingsWindow.xaml.cs`
- Modify: `host/DeadAir.App/App.xaml.cs`
- Modify: `host/DeadAir.App/Theme.xaml`
- Test: `host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj`

**Interfaces:**
- Consumes: `LanguageCatalog.Languages : IReadOnlyList<string>`, `CleanupConfig.OutputLanguage : string`, and `CleanupConfig.TranslationActive : bool`.
- Produces: `TranslationMenuBuilder.Build(string? outputLanguage) : TranslationMenuState`.
- Produces: `TranslationMenuState(string Header, IReadOnlyList<TranslationMenuOption> Options)`.
- Produces: `TranslationMenuOption(string Header, string OutputLanguage, bool IsChecked)`.
- Produces: `SettingsWindow.OutputLanguageBox` populated from the catalog, with one case-insensitive hand-edited extra item when required.
- Produces: tray parent `Translate → Off` or `Translate → {current}` with exactly one checked child; child click updates the live output language only, and `OnSettingsSaved()` rebuilds the menu from the saved value.

- [ ] **Step 1: Write the failing headless tray-model tests**

Create `host/DeadAir.Core.Tests/TranslationMenuBuilderTests.cs` with the complete file:

```csharp
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class TranslationMenuBuilderTests
{
    [Fact]
    public void Build_English_UsesOffHeaderAndChecksFirstCatalogChild()
    {
        var state = TranslationMenuBuilder.Build("English");

        Assert.Equal("Translate → Off", state.Header);
        Assert.Equal(12, state.Options.Count);
        Assert.Equal("Off (English)", state.Options[0].Header);
        Assert.Equal("English", state.Options[0].OutputLanguage);
        Assert.True(state.Options[0].IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_CatalogLanguage_UsesLanguageHeaderAndChecksMatchingChild()
    {
        var state = TranslationMenuBuilder.Build("Hindi");

        Assert.Equal("Translate → Hindi", state.Header);
        var selected = Assert.Single(state.Options.Where(option => option.IsChecked));
        Assert.Equal("Hindi", selected.Header);
        Assert.Equal("Hindi", selected.OutputLanguage);
    }

    [Fact]
    public void Build_HandEditedLanguage_AppendsAndChecksExtraChild()
    {
        var state = TranslationMenuBuilder.Build("  Klingon  ");

        Assert.Equal("Translate → Klingon", state.Header);
        Assert.Equal(13, state.Options.Count);
        Assert.Equal("Klingon", state.Options[^1].Header);
        Assert.Equal("Klingon", state.Options[^1].OutputLanguage);
        Assert.True(state.Options[^1].IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_CatalogMatchIsCaseInsensitiveAndUsesCanonicalLabel()
    {
        var state = TranslationMenuBuilder.Build("  arabic  ");

        Assert.Equal("Translate → Arabic", state.Header);
        Assert.Equal(12, state.Options.Count);
        var selected = Assert.Single(state.Options.Where(option => option.IsChecked));
        Assert.Equal("Arabic", selected.OutputLanguage);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~TranslationMenuBuilderTests"`

Expected: FAIL at compile time with `CS0103: The name 'TranslationMenuBuilder' does not exist in the current context`.

- [ ] **Step 3: Write the minimal pure implementation**

Create `host/DeadAir.Core/Config/TranslationMenuBuilder.cs` with the complete file:

```csharp
namespace DeadAir.Core.Config;

public sealed record TranslationMenuOption(
    string Header,
    string OutputLanguage,
    bool IsChecked);

public sealed record TranslationMenuState(
    string Header,
    IReadOnlyList<TranslationMenuOption> Options);

public static class TranslationMenuBuilder
{
    public static TranslationMenuState Build(string? outputLanguage)
    {
        var requested = string.IsNullOrWhiteSpace(outputLanguage)
            ? "English"
            : outputLanguage.Trim();
        var catalogMatch = LanguageCatalog.Languages.FirstOrDefault(language =>
            string.Equals(language, requested,
                StringComparison.OrdinalIgnoreCase));
        var current = catalogMatch ?? requested;

        var languages = LanguageCatalog.Languages.ToList();
        if (catalogMatch is null)
            languages.Add(current);

        var options = languages
            .Select(language => new TranslationMenuOption(
                string.Equals(language, "English",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Off (English)"
                    : language,
                language,
                string.Equals(language, current,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var parentHeader = string.Equals(current, "English",
            StringComparison.OrdinalIgnoreCase)
            ? "Translate → Off"
            : $"Translate → {current}";

        return new TranslationMenuState(parentHeader, options);
    }
}
```

- [ ] **Step 4: Run tests to verify the pure implementation passes**

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~TranslationMenuBuilderTests"`

Expected: PASS with four cases.

- [ ] **Step 5: Replace the Settings XAML literals with a code-populated ComboBox**

In `host/DeadAir.App/SettingsWindow.xaml`, replace the complete current Output language block with:

```xml
            <TextBlock FontWeight="Bold" Text="Output language"/>
            <ComboBox x:Name="OutputLanguageBox" Margin="0,4,0,12"/>
```

This removes both hardcoded `ComboBoxItem` elements; no language name remains duplicated in XAML.

- [ ] **Step 6: Populate Settings from the catalog and preserve a hand-edited extra item**

In the `SettingsWindow` constructor, replace the block beginning with `var outputLang` and ending with `Select(OutputLanguageBox, outputLang);` with this complete block:

```csharp
        foreach (var language in LanguageCatalog.Languages)
            OutputLanguageBox.Items.Add(
                new ComboBoxItem { Content = language });

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

Leave the existing case-insensitive `Select`, `Selected`, and `OnSave` assignment unchanged. They continue to select and save either a catalog item or the appended hand-edited item.

- [ ] **Step 6b: Add a submenu-capable parent style to the theme**

`DeadAirMenuItem` (`host/DeadAir.App/Theme.xaml`) is a leaf-only template — a `Border`/`ContentPresenter` with no `PART_Popup` or `ItemsPresenter` — so a parent styled with it can never open its children. Add this complete keyed style to `Theme.xaml` immediately after the `DeadAirMenuItem` style (keyed resources only in this file; do not touch existing styles):

```xml
    <Style x:Key="DeadAirSubmenuItem" TargetType="MenuItem">
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                    <Border x:Name="Bd" Background="Transparent" Padding="8,5">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="18"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <ContentPresenter Grid.Column="1" ContentSource="Header"
                                              VerticalAlignment="Center"/>
                            <Path Grid.Column="2" Data="M 0 0 L 4 4 L 0 8 Z"
                                  Fill="{StaticResource MutedBrush}"
                                  VerticalAlignment="Center" Margin="8,0,0,0"/>
                            <Popup x:Name="PART_Popup" Placement="Right"
                                   IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}"
                                   AllowsTransparency="True" Focusable="False">
                                <Border Background="{StaticResource PanelBrush}"
                                        BorderBrush="{StaticResource StrokeBrush}"
                                        BorderThickness="1" CornerRadius="6" Padding="3">
                                    <ItemsPresenter/>
                                </Border>
                            </Popup>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

The popup border deliberately mirrors `DeadAirContextMenu`'s template border (PanelBrush / StrokeBrush / 1 / 6 / 3) so the submenu panel matches the parent menu visually.

- [ ] **Step 7: Replace the tray toggle fields and construction with the submenu parent**

In the field declarations of `host/DeadAir.App/App.xaml.cs`, keep these two fields together and remove the `_translateLanguage` field completely:

```csharp
    private System.Windows.Controls.MenuItem _modeMenuItem = null!;
    private System.Windows.Controls.MenuItem _translateMenuItem = null!;
```

In `BuildMenu`, replace the complete block beginning with the comment `// Like the Polished toggle this is transient` and ending with `_translateMenuItem = translate;` with:

```csharp
        // Child clicks update the live config for the next utterance. Persistence
        // remains Settings-save-only, matching Polished mode's transient behavior.
        // Parent takes the submenu-capable style; DeadAirMenuItem is leaf-only.
        var translate = new System.Windows.Controls.MenuItem
        {
            Style = (System.Windows.Style)Current.FindResource("DeadAirSubmenuItem"),
        };
        _translateMenuItem = translate;
        SyncTranslationMenu();
```

Leave `menu.Items.Add(translate);` immediately after `menu.Items.Add(mode);`, so the submenu remains directly below Polished mode.

- [ ] **Step 8: Add live selection and complete menu resynchronization**

Add these complete methods immediately after `BuildMenu` in `host/DeadAir.App/App.xaml.cs`:

```csharp
    private void SelectTranslationLanguage(string language)
    {
        _config.Cleanup.OutputLanguage = language;
        SyncTranslationMenu();
    }

    private void SyncTranslationMenu()
    {
        var state = TranslationMenuBuilder.Build(
            _config.Cleanup.OutputLanguage);
        var itemStyle = (System.Windows.Style)Current.FindResource(
            "DeadAirMenuItem");

        _translateMenuItem.Header = state.Header;
        _translateMenuItem.Items.Clear();
        foreach (var option in state.Options)
        {
            var child = new System.Windows.Controls.MenuItem
            {
                Header = option.Header,
                IsCheckable = true,
                IsChecked = option.IsChecked,
                Style = itemStyle,
            };
            var language = option.OutputLanguage;
            child.Click += (_, _) => SelectTranslationLanguage(language);
            _translateMenuItem.Items.Add(child);
        }
    }
```

The click path deliberately does not call `ConfigStore.Save`: rebuilding from the live config updates the parent and guarantees exactly one checked child immediately.

- [ ] **Step 9: Re-sync the submenu after Settings saves**

In `OnSettingsSaved`, replace the complete old translation block:

```csharp
            if (_config.Cleanup.TranslationActive)
                _translateLanguage = _config.Cleanup.OutputLanguage.Trim();
            _translateMenuItem.Header = $"Translate → {_translateLanguage}";
            // Setter fires Checked/Unchecked; both handlers re-assign the same
            // value OutputLanguage already holds, so this is idempotent.
            _translateMenuItem.IsChecked = _config.Cleanup.TranslationActive;
```

with:

```csharp
            SyncTranslationMenu();
```

This rebuilds the checked child, parent header, and any hand-edited extra child from the value Settings just saved.

- [ ] **Step 10: Build the WPF app and run the full test-project gate**

Run from the repository root:

`dotnet build host/DeadAir.slnx --no-restore`

Expected: build succeeds, including WPF/XAML compilation.

Then run:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore`

Expected: `Failed: 0, Passed: 192, Skipped: 0, Total: 192`.

- [ ] **Step 11: Ready for controller commit**

Controller commit scope: the pure tray projection and tests, Settings catalog population, tray submenu wiring, Settings-save resync, and removal of `_translateLanguage`.

---

### Task 4: Model rollout and final controller verification

**Files:**
- Modify: `README.md`
- Modify: `docs/spec.md`
- Test: `host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj`
- Test: `host/DeadAir.slnx`

**Interfaces:**
- Consumes: all Task 1–3 interfaces plus installed Ollama model `gemma3:12b`.
- Produces: controller-verified 12-language Settings/tray behavior, non-Latin/RTL cursor injection, unchanged failure fallback, and a target-machine configuration explicitly set to `gemma3:12b`.

- [ ] **Step 0: Update active documentation to the new model and catalog**

Active docs (README, docs/spec.md) still describe `qwen2.5:7b` and a two-language UI; a fresh install following README would pull the wrong model. Apply exactly these replacements — historical plans/specs stay untouched:

In `README.md`:
1. `local model (default ` + backtick + `qwen2.5:7b` + backtick + `)` → same with `gemma3:12b`.
2. `(v1 ships English + Spanish in the UI; the` / `config value is free-form)` → `(the UI ships a 12-language catalog; the` / `config value is free-form)`.
3. `Toggle from the tray ("Translate →` / `Spanish") or pick the language in Settings.` → `Pick the language from the tray submenu ("Translate →") or in Settings.`
4. Line 92 architecture sketch: `qwen2.5:7b, transcript cleanup` → `gemma3:12b, transcript cleanup`.
5. `pulled: ` + backtick + `ollama pull qwen2.5:7b` + backtick → same with `gemma3:12b`, then append to that sentence: ` An existing config.json keeps its stored model until changed in Settings (no auto-migration).`
6. Fenced install command `ollama pull qwen2.5:7b` → `ollama pull gemma3:12b`.
7. Config table `ollama` row: `http://127.0.0.1:11434` / `qwen2.5:7b` → `http://127.0.0.1:11434` / `gemma3:12b`.
8. Config table `outputLanguage` row: `Free-form (UI lists English + Spanish).` → `Free-form (UI lists the 12-language catalog).`

In `docs/spec.md`:
9. Diagram box `qwen2.5:7b (ROCm/Vulkan)` → `gemma3:12b (Vulkan)` (pad with spaces to keep the box edges aligned).
10. Example config JSON `"model": "qwen2.5:7b"` → `"model": "gemma3:12b"`.
11. `ollama pull qwen2.5:7b` → `ollama pull gemma3:12b`.
12. `; ` + backtick + `qwen2.5:7b` + backtick + ` pulled.` → same with `gemma3:12b`.

Verification: `grep -n "qwen2.5:7b" README.md docs/spec.md` returns no hits afterward.

- [ ] **Step 1: Run the final automated gates**

Run from the repository root:

`dotnet build host/DeadAir.slnx --no-restore`

Expected: build succeeds with no compile or XAML errors.

Run from the repository root:

`dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore`

Expected: `Failed: 0, Passed: 192, Skipped: 0, Total: 192`.

- [ ] **Step 2: Perform the controller-only model rollout**

On the target machine, outside the worker sandbox, run:

`ollama pull gemma3:12b`

Expected: Ollama completes the approximately 8.1 GB Q4 model download successfully.

Then open DeadAir Settings, set **Ollama model** to `gemma3:12b`, and save once. This explicit one-time change is required for an existing `config.json`; no migration code should perform it.

- [ ] **Step 3: Complete the controller-only manual smoke checklist (not automated)**

- [ ] Open the tray menu and hover/click "Translate →": the submenu opens, all 12 children render with the dark-red theme (no white stock popup), the checked child shows the checkmark; with a hand-edited config language, a 13th child appears checked.
- [ ] Submenu interaction paths after the template replacement: click-outside dismisses; Escape closes; Left closes the submenu / Right and Enter open it; arrow keys walk the children; after each dismissal the submenu reopens on first attempt (mouse AND keyboard).
- [ ] Dictate → Hindi (Devanagari), Arabic (RTL), and Mandarin (CJK); confirm each lands correctly at the cursor. This verifies the injection path is clean for non-Latin and RTL scripts.
- [ ] Add a dictionary term, dictate it inside a non-Latin sentence, and confirm the term survives untranslated.
- [ ] Switch language from the tray and confirm the new target applies on the next utterance without opening or saving Settings.
- [ ] Stop Ollama while translation is active; dictate and confirm raw English lands at the cursor with a `translation skipped: {reason}` toast.
- [ ] Spot-check latency on `gemma3:12b` and confirm `ollama ps` shows `100% GPU`.

- [ ] **Step 4: Confirm all explicit no-code-change boundaries**

Inspect the completed changes and confirm there are no modifications under `sidecar/`, and no production modifications to `host/DeadAir.Core/Cleanup/PromptBuilder.cs`, prompt templates, skip-guard/failure paths, injection, pill, ScopeGeometry, or ASR configuration logic. Confirm there is no config migration and no per-language model routing.

- [ ] **Step 5: Ready for controller commit**

Controller records the automated gate output and manual-smoke results; workers perform no source-control action.
