# DeadAir Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give DeadAir its logo identity (user-supplied skull-waveform art) on the exe/shortcuts/taskbar/tray/settings window, and retheme the Settings window + tray menu in DeadEye's red theme.

**Architecture:** Assets already staged in `host/DeadAir.App/Assets/` (`deadair-logo.jpg` full art, `deadair-tile.png` cropped tile, `deadair.ico` multi-size 256/64/48/32/16). T15 wires the icon everywhere (csproj `ApplicationIcon`, tray `TaskbarIcon.Icon` with degrade-to-system fallback, Settings window icon + logo header). T16 adds two resource dictionaries — `Theme.xaml` (DeadEye tokens + keyed tray-menu styles, merged app-wide; keyed only, so nothing leaks into the pill) and `SettingsTheme.xaml` (implicit dark control styles incl. a full dark ComboBox template, merged ONLY inside SettingsWindow). The pill windows are untouched.

**Tech Stack:** .NET 8 WPF. DeadEye red-theme tokens (v1.19): bg `#060404`, panel `#120D0C`, ink `#DED7CE`, body `#B3ACA4`, muted `#857E77`, accent `#C11D12`, accent-strong `#E8382B`, stroke `#29BEAFA5` (warm stroke at 0.16 alpha). Branch `feat/deadair-identity` (already checked out; assets committed by the controller).

## Global Constraints

- The pill (`RecordingIndicatorWindow`) is NOT touched by this plan — no implicit style may reach it. Implicit control styles live only in `SettingsTheme.xaml`, merged only in `SettingsWindow.Resources`. `Theme.xaml` (app-merged) contains tokens and KEYED styles only.
- Tray icon load must degrade, never crash the launch (missing ico → `SystemIcons.Application`), matching the house degrade-don't-crash posture.
- Settings public surface and code-behind logic unchanged — T16 is XAML-only for SettingsWindow (all `x:Name`s and event handlers preserved exactly).
- Never-activate constraints on the pill are irrelevant here (untouched) but no change may add `Activate()` anywhere.
- Test command: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore` (expect 151 passed; this plan adds no Core changes). Build: `dotnet build "host/DeadAir.App/DeadAir.App.csproj" --no-restore` (expect 0 errors; MSB3027 file-lock means a stale DeadAir.App.exe is running — report it, the controller kills it).

---

### Task 15: Logo icon wiring (exe, tray, settings window)

**Files:**
- Modify: `host/DeadAir.App/DeadAir.App.csproj`
- Modify: `host/DeadAir.App/App.xaml.cs` (tray icon only)

**Interfaces:**
- Consumes: `Assets/deadair.ico`, `Assets/deadair-logo.jpg`, `Assets/deadair-tile.png` (already in the repo).
- Produces: loose `Assets\*` beside the exe (Content + CopyToOutputDirectory) — T16's XAML references them via `siteoforigin` pack URIs.

- [ ] **Step 1: csproj — application icon + content assets**

Replace the entire contents of `host/DeadAir.App/DeadAir.App.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\DeadAir.Core\DeadAir.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="H.NotifyIcon.Wpf" Version="2.4.1" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>Assets\deadair.ico</ApplicationIcon>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="Assets\deadair.ico" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Assets\deadair-logo.jpg" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Assets\deadair-tile.png" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Tray icon**

In `host/DeadAir.App/App.xaml.cs`, replace:

```csharp
        _tray = new TaskbarIcon
        {
            ToolTipText = "DeadAir — starting…",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
```

with:

```csharp
        // Skull-waveform identity on the tray; degrade to the system icon if the
        // loose asset is missing — an icon must never cost us the launch.
        var trayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "deadair.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "DeadAir — starting…",
            Icon = File.Exists(trayIconPath)
                ? new System.Drawing.Icon(trayIconPath)
                : System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
```

(`System.IO.Path`/`File` are already imported via `using System.IO;` at the top of the file.)

- [ ] **Step 3: Build + suite**

Run the plan's build and test commands. Expected: 0 errors / 151 passed. Also verify the icon reached the exe: `dotnet build` then confirm `host/DeadAir.App/bin/Debug/net8.0-windows/Assets/deadair.ico` exists.

- [ ] **Step 4: Commit**

```bash
git add host/DeadAir.App/DeadAir.App.csproj host/DeadAir.App/App.xaml.cs
git commit -m "feat(host): DeadAir skull-waveform icon on exe, taskbar, and tray"
```

---

### Task 16: DeadEye red theme — Settings window + tray menu

**Files:**
- Create: `host/DeadAir.App/Theme.xaml`
- Create: `host/DeadAir.App/SettingsTheme.xaml`
- Modify: `host/DeadAir.App/App.xaml`
- Modify: `host/DeadAir.App/App.xaml.cs` (BuildMenu styles)
- Modify: `host/DeadAir.App/SettingsWindow.xaml` (full restyle; code-behind untouched)

**Interfaces:**
- Consumes: T15's loose assets; existing SettingsWindow control names/handlers (must all survive verbatim: `HotkeyBox, EngineBox, OllamaModelBox, ModeBox, SkinBox, FanGainSlider/FanGainValue, WiggleSlider/WiggleValue, WiggleSpeedSlider/WiggleSpeedValue, DictionaryBox, SaveButton`, handlers `OnTuningChanged`, `OnSave`).
- Produces: keyed styles `DeadAirContextMenu`, `DeadAirMenuItem`, `DeadAirSeparator` (App-scope, used by BuildMenu).

- [ ] **Step 1: Theme.xaml (tokens + keyed tray-menu styles — app-merged, no implicit styles)**

Create `host/DeadAir.App/Theme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- DeadEye red-theme tokens (v1.19). KEYED resources only in this file:
         it is merged app-wide and must never restyle the pill implicitly. -->
    <SolidColorBrush x:Key="BgBrush" Color="#060404"/>
    <SolidColorBrush x:Key="PanelBrush" Color="#120D0C"/>
    <SolidColorBrush x:Key="InkBrush" Color="#DED7CE"/>
    <SolidColorBrush x:Key="BodyBrush" Color="#B3ACA4"/>
    <SolidColorBrush x:Key="MutedBrush" Color="#857E77"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#C11D12"/>
    <SolidColorBrush x:Key="AccentStrongBrush" Color="#E8382B"/>
    <SolidColorBrush x:Key="StrokeBrush" Color="#29BEAFA5"/>

    <Style x:Key="DeadAirContextMenu" TargetType="ContextMenu">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource StrokeBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
    </Style>

    <Style x:Key="DeadAirMenuItem" TargetType="MenuItem">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Style.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
                <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="DeadAirSeparator" TargetType="Separator">
        <Setter Property="Background" Value="{StaticResource StrokeBrush}"/>
        <Setter Property="Margin" Value="4,2"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 2: SettingsTheme.xaml (implicit dark styles — merged ONLY in SettingsWindow)**

Create `host/DeadAir.App/SettingsTheme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Implicit dark styles for the Settings window ONLY (merged in its
         Window.Resources; never app-wide — the pill must stay untouched). -->
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Theme.xaml"/>
    </ResourceDictionary.MergedDictionaries>

    <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
    </Style>

    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource StrokeBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CaretBrush" Value="{StaticResource InkBrush}"/>
        <Setter Property="Padding" Value="4,2"/>
    </Style>

    <Style TargetType="Button">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="12,4"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="3" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center"
                                          VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter Property="Background" Value="{StaticResource AccentStrongBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="Slider">
        <Setter Property="Foreground" Value="{StaticResource AccentBrush}"/>
    </Style>

    <Style TargetType="ComboBoxItem">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="Padding" Value="6,3"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                    <Border Background="{TemplateBinding Background}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter Property="Background" Value="{StaticResource AccentStrongBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ComboBox">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource StrokeBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid>
                        <ToggleButton Grid.Column="0" Focusable="False"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay,
                                                  RelativeSource={RelativeSource TemplatedParent}}"
                                      ClickMode="Press">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton">
                                    <Border Background="{StaticResource PanelBrush}"
                                            BorderBrush="{StaticResource StrokeBrush}"
                                            BorderThickness="1" CornerRadius="3">
                                        <Path HorizontalAlignment="Right" Margin="0,0,8,0"
                                              VerticalAlignment="Center"
                                              Data="M 0 0 L 4 4 L 8 0 Z"
                                              Fill="{StaticResource MutedBrush}"/>
                                    </Border>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter Margin="8,3,24,3" VerticalAlignment="Center"
                                          Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                          IsHitTestVisible="False"/>
                        <Popup IsOpen="{TemplateBinding IsDropDownOpen}"
                               Placement="Bottom" AllowsTransparency="True"
                               StaysOpen="False">
                            <Border Background="{StaticResource PanelBrush}"
                                    BorderBrush="{StaticResource StrokeBrush}"
                                    BorderThickness="1" CornerRadius="3"
                                    MinWidth="{TemplateBinding ActualWidth}"
                                    MaxHeight="220">
                                <ScrollViewer>
                                    <ItemsPresenter/>
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 3: App.xaml — merge Theme.xaml app-wide**

Replace the contents of `host/DeadAir.App/App.xaml` with:

```xml
<Application x:Class="DeadAir.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Theme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: BuildMenu — apply the keyed tray-menu styles**

In `host/DeadAir.App/App.xaml.cs`'s `BuildMenu()`, after `var menu = new System.Windows.Controls.ContextMenu();` add:

```csharp
        menu.Style = (System.Windows.Style)Current.FindResource("DeadAirContextMenu");
        var itemStyle = (System.Windows.Style)Current.FindResource("DeadAirMenuItem");
```

then set `Style = itemStyle` on each of the three `MenuItem`s (`mode`, `settings`, `exit`) via their object initializers (e.g. add `Style = itemStyle,` to each initializer; for `exit` which uses `{ Header = "Exit" }`, it becomes `{ Header = "Exit", Style = itemStyle }`), and replace `menu.Items.Add(new System.Windows.Controls.Separator());` with:

```csharp
        menu.Items.Add(new System.Windows.Controls.Separator
        {
            Style = (System.Windows.Style)Current.FindResource("DeadAirSeparator"),
        });
```

- [ ] **Step 5: SettingsWindow.xaml — full restyle**

Replace the entire contents of `host/DeadAir.App/SettingsWindow.xaml` with (all names/handlers preserved; adds the theme merge, window icon, logo header, and dark chrome):

```xml
<Window x:Class="DeadAir.App.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="DeadAir Settings" Width="440" Height="600"
        WindowStartupLocation="CenterScreen"
        Icon="pack://siteoforigin:,,,/Assets/deadair.ico"
        Background="{DynamicResource BgBrush}">
    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="SettingsTheme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>
    <ScrollViewer Margin="12">
        <StackPanel>
            <Image Source="pack://siteoforigin:,,,/Assets/deadair-tile.png"
                   Height="96" Margin="0,4,0,12"
                   RenderOptions.BitmapScalingMode="HighQuality"/>

            <TextBlock FontWeight="Bold" Text="Hotkey (hold to talk)"/>
            <ComboBox x:Name="HotkeyBox" Margin="0,4,0,12"/>

            <TextBlock FontWeight="Bold" Text="ASR engine"/>
            <ComboBox x:Name="EngineBox" Margin="0,4,0,12">
                <ComboBoxItem Content="auto"/>
                <ComboBoxItem Content="gpu"/>
                <ComboBoxItem Content="cpu"/>
            </ComboBox>

            <TextBlock FontWeight="Bold" Text="Ollama model"/>
            <TextBox x:Name="OllamaModelBox" Margin="0,4,0,12"/>

            <TextBlock FontWeight="Bold" Text="Default cleanup mode"/>
            <ComboBox x:Name="ModeBox" Margin="0,4,0,12">
                <ComboBoxItem Content="Faithful"/>
                <ComboBoxItem Content="Polished"/>
            </ComboBox>

            <TextBlock FontWeight="Bold" Text="Scope skin"/>
            <ComboBox x:Name="SkinBox" Margin="0,4,0,12">
                <ComboBoxItem Content="nebula"/>
                <ComboBoxItem Content="lantern"/>
            </ComboBox>

            <TextBlock FontWeight="Bold" Text="Nebula fan sensitivity"/>
            <DockPanel Margin="0,4,0,12">
                <TextBlock x:Name="FanGainValue" DockPanel.Dock="Right" Width="36"
                           TextAlignment="Right"/>
                <Slider x:Name="FanGainSlider" Minimum="0.5" Maximum="8"
                        TickFrequency="0.1" IsSnapToTickEnabled="True"
                        ValueChanged="OnTuningChanged"/>
            </DockPanel>

            <TextBlock FontWeight="Bold" Text="Nebula wiggle"/>
            <DockPanel Margin="0,4,0,12">
                <TextBlock x:Name="WiggleValue" DockPanel.Dock="Right" Width="36"
                           TextAlignment="Right"/>
                <Slider x:Name="WiggleSlider" Minimum="0" Maximum="1.5"
                        TickFrequency="0.05" IsSnapToTickEnabled="True"
                        ValueChanged="OnTuningChanged"/>
            </DockPanel>

            <TextBlock FontWeight="Bold" Text="Nebula wiggle speed"/>
            <DockPanel Margin="0,4,0,12">
                <TextBlock x:Name="WiggleSpeedValue" DockPanel.Dock="Right" Width="36"
                           TextAlignment="Right"/>
                <Slider x:Name="WiggleSpeedSlider" Minimum="0" Maximum="4"
                        TickFrequency="0.1" IsSnapToTickEnabled="True"
                        ValueChanged="OnTuningChanged"/>
            </DockPanel>

            <TextBlock FontWeight="Bold"
                       Text="Custom dictionary (one term per line)"/>
            <TextBox x:Name="DictionaryBox" Margin="0,4,0,12" Height="120"
                     AcceptsReturn="True" VerticalScrollBarVisibility="Auto"/>

            <Button x:Name="SaveButton" Content="Save" Width="90"
                    HorizontalAlignment="Right" Click="OnSave"/>
        </StackPanel>
    </ScrollViewer>
</Window>
```

- [ ] **Step 6: Build + suite**

Run the plan's build and test commands. Expected: 0 errors / 151 passed. (XAML template errors surface at build as MC/XLS diagnostics — if any template line fails to compile, fix the minimal syntax issue and flag it prominently in the report.)

- [ ] **Step 7: Commit**

```bash
git add host/DeadAir.App/Theme.xaml host/DeadAir.App/SettingsTheme.xaml host/DeadAir.App/App.xaml host/DeadAir.App/App.xaml.cs host/DeadAir.App/SettingsWindow.xaml
git commit -m "feat(host): DeadEye red theme on Settings window and tray menu"
```

---

### Task 17: Live smoke (user)

- [ ] Relaunch (`taskkill /IM DeadAir.App.exe /F` first), then `dotnet run --project "host/DeadAir.App/DeadAir.App.csproj"` (background).
- [ ] User checks: skull icon on taskbar + tray (+ shortcut/exe in Explorer); tray right-click menu dark with red hover; Settings opens near-black with logo header, bone text, dark dropdowns (open one — the popup must be dark, not white), red-accent Save hover; save still applies live; a recording still works end-to-end.
- [ ] Log check: no ERROR lines.

---

### Task 18: Theme the stragglers (sliders, scrollbars, tray check glyph, dark title bar)

**User amendment at the T17 smoke:** "get the scroll bars, checkboxes and sliders to follow the theme; maybe darkmode the top of the window." The "checkbox" is the tray menu's checkable "Polished mode" glyph (Settings has no CheckBox — but an implicit dark CheckBox style ships anyway for future use). The title bar darkens via DWM's immersive-dark-mode attribute.

**Files:**
- Modify: `host/DeadAir.App/SettingsTheme.xaml` (replace the Slider style; add ScrollBar + CheckBox styles)
- Modify: `host/DeadAir.App/Theme.xaml` (replace `DeadAirMenuItem` with a templated version incl. accent check glyph)
- Modify: `host/DeadAir.App/SettingsWindow.xaml.cs` (dark title bar P/Invoke — the only code-behind change)

- [ ] **Step 1: SettingsTheme.xaml — replace the Slider style**

Replace:

```xml
    <Style TargetType="Slider">
        <Setter Property="Foreground" Value="{StaticResource AccentBrush}"/>
    </Style>
```

with:

```xml
    <Style TargetType="Slider">
        <Setter Property="Height" Value="22"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Slider">
                    <Grid VerticalAlignment="Center">
                        <Border Height="4" CornerRadius="2"
                                Background="{StaticResource PanelBrush}"
                                BorderBrush="{StaticResource StrokeBrush}"
                                BorderThickness="1"/>
                        <Track x:Name="PART_Track">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                                    <RepeatButton.Template>
                                        <ControlTemplate TargetType="RepeatButton">
                                            <Border Height="4" CornerRadius="2" Margin="1,0,0,0"
                                                    Background="{StaticResource AccentBrush}"/>
                                        </ControlTemplate>
                                    </RepeatButton.Template>
                                </RepeatButton>
                            </Track.DecreaseRepeatButton>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                                    <RepeatButton.Template>
                                        <ControlTemplate TargetType="RepeatButton">
                                            <Border Background="Transparent" Height="22"/>
                                        </ControlTemplate>
                                    </RepeatButton.Template>
                                </RepeatButton>
                            </Track.IncreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Width="14" Height="14">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Ellipse Fill="{StaticResource AccentStrongBrush}"
                                                     Stroke="{StaticResource BgBrush}"
                                                     StrokeThickness="1"/>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 2: SettingsTheme.xaml — add ScrollBar + CheckBox styles (before the closing `</ResourceDictionary>`)**

```xml
    <Style TargetType="ScrollBar">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Width" Value="8"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageUpCommand"
                                              Opacity="0" Focusable="False"/>
                            </Track.DecreaseRepeatButton>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageDownCommand"
                                              Opacity="0" Focusable="False"/>
                            </Track.IncreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb>
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Border x:Name="ThumbBd" CornerRadius="4" Margin="1"
                                                    Background="{StaticResource MutedBrush}"/>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="ThumbBd" Property="Background"
                                                            Value="{StaticResource AccentBrush}"/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Orientation" Value="Horizontal">
                            <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="Height" Value="8"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <StackPanel Orientation="Horizontal">
                        <Border Width="14" Height="14" CornerRadius="2"
                                Background="{StaticResource PanelBrush}"
                                BorderBrush="{StaticResource StrokeBrush}"
                                BorderThickness="1" VerticalAlignment="Center">
                            <Path x:Name="Check" Data="M 2 6 L 5 9 L 11 2"
                                  Stroke="{StaticResource AccentStrongBrush}"
                                  StrokeThickness="2" Visibility="Collapsed"/>
                        </Border>
                        <ContentPresenter Margin="6,0,0,0" VerticalAlignment="Center"/>
                    </StackPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 3: Theme.xaml — replace `DeadAirMenuItem` with the templated version (accent check glyph)**

Replace the existing `DeadAirMenuItem` style with:

```xml
    <Style x:Key="DeadAirMenuItem" TargetType="MenuItem">
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                    <Border x:Name="Bd" Background="Transparent" Padding="8,5">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="18"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <Path x:Name="Check" Grid.Column="0" Data="M 0 3 L 3 6 L 8 0"
                                  Stroke="{StaticResource AccentStrongBrush}" StrokeThickness="2"
                                  VerticalAlignment="Center" Visibility="Collapsed"/>
                            <ContentPresenter Grid.Column="1" ContentSource="Header"
                                              VerticalAlignment="Center"/>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                        </Trigger>
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

(The tray menu is flat — no submenus — so a role-less template is safe; MenuItem input/toggle behavior lives on the control, not the template.)

- [ ] **Step 4: SettingsWindow.xaml.cs — dark title bar**

Add `using System.Runtime.InteropServices;` to the usings, then add inside the class:

```csharp
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark-mode the non-client title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20).
        // Best-effort: on failure the bar just stays light.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }
```

- [ ] **Step 5: Build + suite**

`dotnet build "host/DeadAir.App/DeadAir.App.csproj" --no-restore` → 0 errors; `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore` → 151 passed.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.App/SettingsTheme.xaml host/DeadAir.App/Theme.xaml host/DeadAir.App/SettingsWindow.xaml.cs
git commit -m "feat(host): theme sliders, scrollbars, check glyphs; dark title bar"
```
