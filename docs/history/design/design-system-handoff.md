# Babel Player — Design System Handoff Spec
## Target: Fix audit issues and bring system to consistent state

**Prepared:** 2026-04-07  
**Stack:** C# / .NET 10 · Avalonia 12.0.1 · Fluent theme  
**Source of truth:** `App.axaml` ThemeDictionaries  
**Files touched by this spec:** `App.axaml`, `MainWindow.axaml`, `SettingsWindow.axaml`, `ApiKeysDialog.axaml`

---

## Change 1 — Rename `BorderBrushBrush` → `BorderBrush`

**Why:** Doubled "Brush" suffix is a naming error. All 15 call sites must change atomically or the app breaks.

**In `App.axaml`** — rename the key in both theme dictionaries:

```xml
<!-- Light -->
<SolidColorBrush x:Key="BorderBrush" Color="#CCCCCC" />

<!-- Dark -->
<SolidColorBrush x:Key="BorderBrush" Color="#252530" />
```

**All call sites** — replace every `{DynamicResource BorderBrushBrush}` with `{DynamicResource BorderBrush}`.

Files: `MainWindow.axaml` (approx 12 occurrences), `SettingsWindow.axaml` (0 occurrences — it uses hardcoded hex already, see Change 3).

---

## Change 2 — Add missing tokens to `App.axaml`

Add these new entries to **both** `Light` and `Dark` `ResourceDictionary` blocks inside `Application.Resources > ResourceDictionary.ThemeDictionaries`.

### 2a — Accent colors (primary action, purple indigo)

```xml
<!-- Light -->
<SolidColorBrush x:Key="AccentBrush"         Color="#4F46E5" />
<SolidColorBrush x:Key="AccentSecondaryBrush" Color="#7C3AED" />
<SolidColorBrush x:Key="AccentSubtleBrush"    Color="#312350" />

<!-- Dark -->
<SolidColorBrush x:Key="AccentBrush"         Color="#4F46E5" />
<SolidColorBrush x:Key="AccentSecondaryBrush" Color="#7C3AED" />
<SolidColorBrush x:Key="AccentSubtleBrush"    Color="#312350" />
```

Note: accent colors are the same in both themes because they are brand-fixed. The gradient is always `AccentBrush → AccentSecondaryBrush` left-to-right.

### 2b — Destructive and semantic action colors

```xml
<!-- Light -->
<SolidColorBrush x:Key="DestructiveBrush"     Color="#6B2525" />
<SolidColorBrush x:Key="DestructiveFgBrush"   Color="#FCA5A5" />
<SolidColorBrush x:Key="PositiveBrush"        Color="#1E2E1E" />
<SolidColorBrush x:Key="PositiveFgBrush"      Color="#86EFAC" />
<SolidColorBrush x:Key="InfoBrush"            Color="#1E2436" />
<SolidColorBrush x:Key="InfoFgBrush"          Color="#93C5FD" />

<!-- Dark (same values — these are semantically dark-only surfaces) -->
<SolidColorBrush x:Key="DestructiveBrush"     Color="#6B2525" />
<SolidColorBrush x:Key="DestructiveFgBrush"   Color="#FCA5A5" />
<SolidColorBrush x:Key="PositiveBrush"        Color="#1E2E1E" />
<SolidColorBrush x:Key="PositiveFgBrush"      Color="#86EFAC" />
<SolidColorBrush x:Key="InfoBrush"            Color="#1E2436" />
<SolidColorBrush x:Key="InfoFgBrush"          Color="#93C5FD" />
```

### 2c — Surface elevation tokens

These consolidate the multiple near-identical elevated-surface hex values (`#151522`, `#1A1A28`, `#18182A`):

```xml
<!-- Light -->
<SolidColorBrush x:Key="SurfaceRaisedBrush"   Color="#F0F0FA" />
<SolidColorBrush x:Key="SurfaceSubtleBrush"   Color="#E8E8F5" />

<!-- Dark -->
<SolidColorBrush x:Key="SurfaceRaisedBrush"   Color="#151522" />
<SolidColorBrush x:Key="SurfaceSubtleBrush"   Color="#18182A" />
```

### 2d — Warning surface tokens (diagnostics bar)

```xml
<!-- Light -->
<SolidColorBrush x:Key="WarningBackgroundBrush" Color="#FFF3E0" />
<SolidColorBrush x:Key="WarningBorderBrush"     Color="#E65100" />

<!-- Dark -->
<SolidColorBrush x:Key="WarningBackgroundBrush" Color="#2A1200" />
<SolidColorBrush x:Key="WarningBorderBrush"     Color="#5A2800" />
```

### 2e — Remove unused token

Delete `PipelineProgressBorderBrush` from both theme dictionaries. It is defined but has zero call sites; leaving it creates false documentation.

---

## Change 3 — Wire segment ListBox to existing tokens

**File:** `MainWindow.axaml` — `ListBox.Styles` block around line 876.

**Current (broken for light theme):**
```xml
<Style Selector="ListBoxItem:selected /template/ ContentPresenter">
    <Setter Property="Background" Value="#20203A" />
</Style>
<Style Selector="ListBoxItem:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="#1C1C2C" />
</Style>
```

**Replace with:**
```xml
<Style Selector="ListBoxItem:selected /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource SegmentSelectedBrush}" />
</Style>
<Style Selector="ListBoxItem:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource SegmentPointerOverBrush}" />
</Style>
```

Also wire the segment row divider and text colors in the `DataTemplate`:

| Element | Current | Replace with |
|---|---|---|
| Row `Border.BorderBrush` | `#252530` | `{DynamicResource BorderBrush}` |
| Time range `TextBlock.Foreground` | `#6A6A82` | `{DynamicResource StatusTextBrush}` |
| Source text `TextBlock.Foreground` | `#686878` | `{DynamicResource StatusTextBrush}` |
| Translated text `TextBlock.Foreground` | `#E8E8F0` | `{DynamicResource PrimaryTextBrush}` |

---

## Change 4 — Wire hardcoded hex values in MainWindow panels

**Left panel `Border` (line 23):**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#1A1A28` | `{DynamicResource PanelBackgroundBrush}` |
| `BorderBrush` | `#2E2E44` | `{DynamicResource BorderBrush}` |

Note: the left panel uses `Border.Resources` to shadow tokens for always-dark rendering. Keep that mechanism — just fix the outer `Border` attributes to use the tokens too.

**Right panel `Border` (line 826):**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#16161E` | `{DynamicResource PanelBackgroundBrush}` |
| `BorderBrush` | `#252530` | `{DynamicResource BorderBrush}` |

**Diagnostics warning bar `Border` (line 584):**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#2A1200` | `{DynamicResource WarningBackgroundBrush}` |
| `BorderBrush` | `#5A2800` | `{DynamicResource WarningBorderBrush}` |

**VSR status overlay `Border` (line 598):**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#151522` | `{DynamicResource SurfaceRaisedBrush}` |
| `BorderBrush` | `#2B2940` | `{DynamicResource BorderBrush}` |

**Transport capsule `Border` (line 711):**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#18182A` | `{DynamicResource SurfaceSubtleBrush}` |

**Play/Pause circular button:**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#30304A` | `{DynamicResource ButtonBackgroundBrush}` |
| `:pointerover` bg | `#3C3C5C` | `{DynamicResource ButtonBackgroundBrush}` + inline opacity fine |
| `:pressed` bg | `#252545` | Keep hardcoded — no token for pressed-darker state yet |

**Pipeline ProgressBar `Foreground`:**

| Property | Current | Replace with |
|---|---|---|
| `Foreground` | `#7C3AED` | `{DynamicResource AccentSecondaryBrush}` |

**Run Pipeline button gradient:**

```xml
<!-- Before -->
<GradientStop Color="#4F46E5" Offset="0" />
<GradientStop Color="#7C3AED" Offset="1" />

<!-- After -->
<GradientStop Color="{DynamicResource AccentBrush}" Offset="0" />
<GradientStop Color="{DynamicResource AccentSecondaryBrush}" Offset="1" />
```

Apply the same replacement to the `:pointerover` and `:pressed` gradient variants. The `:pointerover` stops (`#5B52F0`, `#8B4DF8`) and `:pressed` stops (`#3D35C8`, `#6A2FCC`) are fine as hardcoded values — they are interaction-derived from the accent, not the brand color itself.

**Cancel Pipeline button:**

| Property | Current | Replace with |
|---|---|---|
| `Background` | `#6B2525` | `{DynamicResource DestructiveBrush}` |
| `Foreground` | `White` | Keep — white on DestructiveBrush is correct |

---

## Change 5 — Promote `control-pill` to app-level styles

**Current location:** `Border.Styles` block wrapping the controls bar in `MainWindow.axaml` (~line 638).

**Move to:** `App.axaml > Application.Styles` block, after `<FluentTheme />`.

The full style block to add to `App.axaml`:

```xml
<!-- control-pill: icon/action buttons in the playback toolbar -->
<Style Selector="Button.control-pill">
    <Setter Property="Foreground" Value="#A8A8C8" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderBrush" Value="Transparent" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="6" />
</Style>
<Style Selector="Button.control-pill /template/ ContentPresenter">
    <Setter Property="Background" Value="Transparent" />
</Style>
<Style Selector="Button.control-pill:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="#1D1D2A" />
</Style>
<Style Selector="Button.control-pill:pressed /template/ ContentPresenter">
    <Setter Property="Background" Value="#252537" />
</Style>
<Style Selector="Button.control-pill.active">
    <Setter Property="Foreground" Value="#E7DBFF" />
    <Setter Property="BorderBrush" Value="{DynamicResource AccentSecondaryBrush}" />
</Style>
<Style Selector="Button.control-pill.active /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource AccentSubtleBrush}" />
</Style>
<!-- Missing states — add these: -->
<Style Selector="Button.control-pill.active:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="#3D2860" />
</Style>
<Style Selector="Button.control-pill.active:pressed /template/ ContentPresenter">
    <Setter Property="Background" Value="#251840" />
</Style>
```

After moving, **remove** the `Border.Styles` block from `MainWindow.axaml`. Leave `dev-pill` in the local `Border.Styles` — it is intentionally scoped.

---

## Change 6 — Token-link `SettingsWindow.axaml`

These are pure find-and-replace substitutions. No structural changes needed.

| Element | Current | Replace with |
|---|---|---|
| Backend status row `Border.Background` | `#1A1A2A` | `{DynamicResource ControlBackgroundBrush}` |
| VSR status card `Border.Background` | `#151522` | `{DynamicResource SurfaceRaisedBrush}` |
| VSR status card `Border.BorderBrush` | `#2B2940` | `{DynamicResource BorderBrush}` |
| VSR label `TextBlock.Foreground` (×5) | `#9A98B8` | `{DynamicResource StatusTextBrush}` |
| VSR value `TextBlock.Foreground` (×5) | `#D8D6F2` | `{DynamicResource PrimaryTextBrush}` |
| Models tab item `Border.Background` | `#1E1E2C` | `{DynamicResource ControlBackgroundBrush}` |
| Models tab status text `Foreground` | `#A0A0B8` | `{DynamicResource StatusTextBrush}` |

**Do not touch:** Ko-fi / GitHub Sponsors buttons. Their `#EAE7E3`, `#1D2027`, `#BF3989`, `#1F2328` values are brand-specified for those external services and intentionally override the app palette.

---

## Change 7 — Token-link `ApiKeysDialog.axaml`

**Remove `RequestedThemeVariant="Dark"`** from the `Window` element. The dialog should follow the app theme.

**Wire all backgrounds and borders:**

| Element | Current | Replace with |
|---|---|---|
| `Window.Background` | `#16161E` | `{DynamicResource PanelBackgroundBrush}` |
| Card `Border.Background` | `#1E1E2C` | `{DynamicResource ControlBackgroundBrush}` |
| Card `Border.BorderBrush` | `#2A2A3C` | `{DynamicResource BorderBrush}` |
| Provider name `TextBlock.Foreground` | `#E0E0F0` | `{DynamicResource PrimaryTextBrush}` |
| Input `TextBox.Background` | `#12121A` | `{DynamicResource WindowBackgroundBrush}` |
| Input `TextBox.BorderBrush` | `#2A2A3C` | `{DynamicResource BorderBrush}` |
| Input `TextBox.Foreground` | `#C0C0D0` | `{DynamicResource PrimaryTextBrush}` |
| Toggle reveal `Button.Background` | `#252535` | `{DynamicResource ButtonBackgroundBrush}` |
| Footer `Border.BorderBrush` | `#252530` | `{DynamicResource BorderBrush}` |
| Footer `Border.Background` | `#12121A` | `{DynamicResource WindowBackgroundBrush}` |
| Close `Button.Background` | `#252530` | `{DynamicResource ButtonBackgroundBrush}` |
| Close `Button.Foreground` | `#C0C0D0` | `{DynamicResource PrimaryTextBrush}` |
| Placeholder footer `Border.Background` | `#1A1A28` | `{DynamicResource PanelBackgroundBrush}` |
| Placeholder footer `Border.BorderBrush` | `#202030` | `{DynamicResource BorderBrush}` |

**Wire semantic action buttons** using new tokens from Change 2b:

| Button | Background | Foreground |
|---|---|---|
| Save | `{DynamicResource PositiveBrush}` | `{DynamicResource PositiveFgBrush}` |
| Validate | `{DynamicResource InfoBrush}` | `{DynamicResource InfoFgBrush}` |
| Clear | `{DynamicResource DestructiveBrush}` | `{DynamicResource DestructiveFgBrush}` |

---

## Change 8 — Fix `control-pill.active` missing hover/press states (already covered in Change 5)

These are the two states currently absent from the style — `active:pointerover` and `active:pressed`. Without them, hovering an active pill (e.g. Dub Mode toggled on) drops the background to the non-active hover color, which visually deactivates the button while the state is still on.

Values used above (`#3D2860` hover, `#251840` pressed) are derived from `AccentSubtleBrush` (`#312350`) by ±10% lightness. If the accent changes in the future, these should be updated together.

---

## Change 9 — Standardize CornerRadius

No new resource type needed for Avalonia static scalars — define as documented constants in a code comment in `App.axaml`:

```xml
<!-- Corner radius scale:
     Sm = 4  (inline buttons, inputs, small chips)
     Md = 8  (cards, overlays, dialogs)
     Lg = 12 (transport capsule, large surface containers)
     Pill = 21–22 (Ko-fi / GitHub sponsor buttons — brand exception) -->
```

Apply the scale to inconsistent call sites:

| File | Current radius | Correct value | Element |
|---|---|---|---|
| MainWindow | `CornerRadius="5"` | `4` | API Keys button, speaker buttons |
| MainWindow | `CornerRadius="4"` | `4` | Most buttons ✅ |
| MainWindow | `CornerRadius="6"` | `8` | Run/Cancel pipeline buttons (these are primary CTA — should be Md) |
| MainWindow | `CornerRadius="8"` | `8` | VSR overlay ✅ |
| MainWindow | `CornerRadius="12"` | `12` | Transport capsule ✅ |
| ApiKeysDialog | `CornerRadius="6"` | `8` | Key entry card |
| ApiKeysDialog | `CornerRadius="4"` | `4` | Input and action buttons ✅ |
| SettingsWindow | `CornerRadius="6"` | `8` | Backend status row, Models item cards |
| SettingsWindow | `CornerRadius="8"` | `8` | VSR status card ✅ |

---

## States and Interactions Reference

This table covers every interactive component and all required states. States marked ❌ are currently missing or wrong.

### control-pill button

| State | Foreground | Background | BorderBrush |
|---|---|---|---|
| Default | `#A8A8C8` | `Transparent` | `Transparent` |
| `:pointerover` | `#A8A8C8` | `#1D1D2A` | `Transparent` |
| `:pressed` | `#A8A8C8` | `#252537` | `Transparent` |
| `.active` | `#E7DBFF` | `AccentSubtleBrush` | `AccentSecondaryBrush` |
| `.active:pointerover` ❌ | `#E7DBFF` | `#3D2860` | `AccentSecondaryBrush` |
| `.active:pressed` ❌ | `#E7DBFF` | `#251840` | `AccentSecondaryBrush` |
| `:disabled` | inherited opacity | `Transparent` | `Transparent` |

### Generic panel button (pipeline panel, segment panel header)

| State | Background | Notes |
|---|---|---|
| Default | `ButtonBackgroundBrush` | `CornerRadius=4` |
| `:pointerover` | Fluent default (theme handles) | Do not override unless needed |
| `:pressed` | Fluent default | |
| `:disabled` | `ButtonBackgroundBrush` + `Opacity=0.4` | |

### ComboBox (pipeline panel)

All ComboBoxes in the pipeline panel share the same token set:
- `Background="{DynamicResource ControlBackgroundBrush}"`
- `Foreground="{DynamicResource PrimaryTextBrush}"`
- `BorderBrush="{DynamicResource BorderBrush}"`
- `FontSize="12"`
- `HorizontalAlignment="Stretch"`

No per-ComboBox inline styling needed. If these need custom hover/focus states, define a named `Style` in App.axaml rather than repeating inline.

### Segment ListBoxItem

| State | Background |
|---|---|
| Default | `Transparent` |
| `:pointerover` | `SegmentPointerOverBrush` |
| `:selected` | `SegmentSelectedBrush` |
| `:selected:pointerover` | `SegmentSelectedBrush` (no change — selection wins) |

### Status indicator dots (`Ellipse`)

Two instances exist. No shared style currently. Define in App.axaml:

```xml
<Style Selector="Ellipse.status-dot-ready">
    <Setter Property="Width" Value="8" />
    <Setter Property="Height" Value="8" />
    <Setter Property="Fill" Value="#22C55E" />
    <Setter Property="HorizontalAlignment" Value="Center" />
</Style>
<Style Selector="Ellipse.status-dot-set">
    <!-- ApiKeysDialog: provider key is set -->
    <Setter Property="Width" Value="8" />
    <Setter Property="Height" Value="8" />
    <Setter Property="HorizontalAlignment" Value="Center" />
    <!-- Fill is bound from VM (StatusDotColor) — do not set here -->
</Style>
```

Apply `Classes="status-dot-ready"` to the TTS-ready dot in segment list. The ApiKeysDialog dot uses a VM-bound fill so no structural class applies there, but the width/height/alignment can still use a base class.

---

## Edge Cases

**Long segment text** — `TextWrapping="Wrap"` is already set on all three segment text rows. No change needed. Confirm that very long source text (3+ lines) does not push the status dot column off-screen — the `ColumnDefinitions="*,12"` constrains the status column to 12px, which is correct.

**Empty segment list** — currently no empty state. Right panel shows the `Segments` header with "0 Items" and an empty `ListBox`. Acceptable for now; an empty-state `TextBlock` ("Transcribe media to see segments") would improve the experience but is out of scope for this spec.

**ApiKeysDialog with no entries** — the `ItemsControl` collapses to zero height and only the placeholder footer renders. The footer copy is sufficient.

**SettingsWindow on light theme (post-fix)** — verify that the VSR status card (`SurfaceRaisedBrush` in light = `#F0F0FA`) remains readable against the light `WindowBackgroundBrush` (`#FFFFFF`). The contrast is ~5:1 for text, which passes WCAG AA.

**CrashReportWindow** — intentionally excluded from this spec. Its warm-neutral standalone palette is acceptable for an error-reporting surface. File a separate issue to align it if desired.

---

## Accessibility Notes

- **Focus order** in MainWindow follows DOM order: left panel → video surface → right panel. Tab order through the playback toolbar is left-to-right within each `StackPanel`. No change needed.
- **control-pill buttons** have `ToolTip.Tip` on media-control actions. The open-file, fullscreen, and settings buttons are covered. The CC, dub-mode, and skip buttons currently have no `ToolTip.Tip` — add them.
- **ApiKeysDialog:** After removing `RequestedThemeVariant="Dark"`, test that the status dot `Ellipse` fills (bound from VM as `StatusDotColor`) remain visible in light theme. The VM currently returns `"#22C55E"` (green) and `"#686880"` (grey) — both are legible on light backgrounds.
- **ProgressBar** — both instances lack `AutomationProperties.Name`. Add `AutomationProperties.Name="Pipeline progress"` and `AutomationProperties.Name="Model download progress"` respectively.

---

## Implementation Order

Implement in this order to avoid broken intermediate states:

1. **Change 1** (rename `BorderBrushBrush`) — do this first, atomically, across all files before anything else. The app will not compile with mixed key names.
2. **Change 2** (add new tokens to `App.axaml`) — add all tokens before wiring call sites.
3. **Changes 3–7** (wire call sites) — order within this group does not matter; each file can be done independently.
4. **Change 5** (promote `control-pill` to app scope) — ensure the existing `Border.Styles` block is removed from `MainWindow.axaml` in the same commit that adds it to `App.axaml`. Split commits will cause a broken intermediate state where the styles exist twice or not at all.
5. **Change 8** (missing pill states) — covered by Change 5 output.
6. **Changes 8–9** (CornerRadius cleanup) — cosmetic, do last.
