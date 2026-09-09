# Babel Player — Design System Audit

**Audit date:** 2026-04-07  
**Scope:** All `.axaml` files — `App.axaml`, `MainWindow.axaml`, `SettingsWindow.axaml`, `ApiKeysDialog.axaml`, `CrashReportWindow.axaml`, `DevLogWindow.axaml`  
**Components reviewed:** 7 | **Issues found:** 24 | **Score: 41/100**

---

## Summary

Babel Player has the foundations of a coherent dark-indigo design language — a defined color palette, a clear typographic rhythm, and emerging component patterns like `control-pill`. The critical problem is that this language lives partly in `App.axaml` semantic tokens and partly as dozens of raw hex literals scattered across view files, often inconsistently. There is no spacing scale, no shared button style beyond the one named class, no typography scale resource, and the `CrashReportWindow` and `ApiKeysDialog` are entirely detached from the token system. The system scores poorly on token coverage and consistency, but the architectural intent is visible and salvageable.

---

## Token Coverage

### Colors — defined in `App.axaml` ThemeDictionaries

| Token | Light | Dark | Used via DynamicResource |
|---|---|---|---|
| `WindowBackgroundBrush` | `#FFFFFF` | `#12121A` | ✅ MainWindow |
| `PanelBackgroundBrush` | `#1A1A28` | `#16161E` | ✅ App.axaml only |
| `ControlBackgroundBrush` | `#EBEBEB` | `#1E1E2C` | ✅ Pipeline panel ComboBoxes, TextBoxes |
| `ButtonBackgroundBrush` | `#E0E0E0` | `#2A2A3A` | ✅ Panel buttons, error-details button |
| `BorderBrushBrush` | `#CCCCCC` | `#252530` | ✅ Pipeline panel borders |
| `ProgressBackgroundBrush` | `#D0D0D0` | `#2A2A3A` | ✅ Pipeline ProgressBar |
| `PipelineProgressBorderBrush` | `#D5D5D5` | `#151522` | ❌ Defined but not used anywhere |
| `SegmentSelectedBrush` | `#E0E0F0` | `#20203A` | ❌ Defined but not used — ListBox uses hardcoded `#20203A` |
| `SegmentPointerOverBrush` | `#E8E8F8` | `#1C1C2C` | ❌ Defined but not used — ListBox uses hardcoded `#1C1C2C` |
| `StatusTextBrush` | `#555555` | `#686880` | ✅ Pipeline panel status text |
| `PrimaryTextBrush` | `#1C1C2C` | `#E0DEFF` | ✅ Widespread |
| `VideoOverlayTextBrush` | `#C8C8D8` | `#C8C8D8` | ✅ VSR overlay |
| `VideoOverlaySubtextBrush` | `#A0A0B8` | `#A0A0B8` | ✅ Scrub bar timestamps |

**Token coverage gap: 3 of 13 defined tokens are unused; their actual values are hardcoded at the use site instead.**

### Hardcoded hex values — by file

**MainWindow.axaml**

| Location | Value | Should be |
|---|---|---|
| Left panel `Border.Background` | `#1A1A28` | `PanelBackgroundBrush` |
| Left panel `Border.BorderBrush` | `#2E2E44` | `BorderBrushBrush` (or new token) |
| Diagnostics warning bar `Background` | `#2A1200` | New token `WarningBackgroundBrush` |
| Diagnostics warning bar `BorderBrush` | `#5A2800` | New token `WarningBorderBrush` |
| Video surface center bg | `#0E0E14` | New token or `WindowBackgroundBrush` |
| VSR overlay `Background` | `#151522` | New token `SurfaceRaisedBrush` |
| VSR overlay `BorderBrush` | `#2B2940` | Align with `BorderBrushBrush` |
| Right panel `Background` | `#16161E` | `PanelBackgroundBrush` |
| Right panel `BorderBrush` | `#252530` | `BorderBrushBrush` |
| Segment list `ListBoxItem:selected` | `#20203A` | `SegmentSelectedBrush` |
| Segment list `ListBoxItem:pointerover` | `#1C1C2C` | `SegmentPointerOverBrush` |
| Segment time text | `#6A6A82` | `StatusTextBrush` |
| Segment source text | `#686878` | `StatusTextBrush` |
| Segment translated text | `#E8E8F0` | `PrimaryTextBrush` |
| Transport capsule `Background` | `#18182A` | New token `SurfaceSubtleBrush` |
| Play button bg | `#30304A` | `ButtonBackgroundBrush` |
| Pipeline ProgressBar `Foreground` | `#7C3AED` | New token `AccentBrush` |
| Run Pipeline gradient stops | `#4F46E5`, `#7C3AED` | `AccentBrush` / `AccentSecondaryBrush` |
| Cancel Pipeline `Background` | `#6B2525` | New token `DestructiveBrush` |
| `control-pill:active` border | `#6C4AF2` | `AccentBrush` |
| `control-pill:active` bg | `#312350` | `AccentSubtleBrush` |
| `dev-pill` colors | `#D4A017`, `#3A3010` | Dev-only tokens (acceptable as-is) |

**SettingsWindow.axaml**

| Location | Value | Should be |
|---|---|---|
| Backend status row `Background` | `#1A1A2A` | `ControlBackgroundBrush` |
| VSR status card `Background` | `#151522` | `SurfaceRaisedBrush` |
| VSR status card `BorderBrush` | `#2B2940` | `BorderBrushBrush` |
| VSR label text | `#9A98B8` | `StatusTextBrush` |
| VSR value text | `#D8D6F2` | `PrimaryTextBrush` |
| Ko-fi button border `Background` | `#EAE7E3`, `#1D2027`, etc. | Brand-exception (intentional) |
| Models tab item `Background` | `#1E1E2C` | `ControlBackgroundBrush` |
| Models tab status text | `#A0A0B8` | `StatusTextBrush` |

**ApiKeysDialog.axaml**

This window is entirely disconnected from the token system. It is hardcoded dark-only (forced `RequestedThemeVariant="Dark"`) and uses its own parallel palette. No `DynamicResource` references exist in this file at all.

| Used value | Semantic match |
|---|---|
| `#16161E` (window bg) | `PanelBackgroundBrush` |
| `#1E1E2C` (card bg) | `ControlBackgroundBrush` |
| `#2A2A3C` (border) | `BorderBrushBrush` |
| `#E0E0F0` (provider name) | `PrimaryTextBrush` |
| `#12121A` (input bg) | `WindowBackgroundBrush` |
| `#1E2E1E` / `#86EFAC` (Save) | New `PositiveBrush` |
| `#1E2436` / `#93C5FD` (Validate) | New `InfoBrush` |
| `#2E1E1E` / `#FCA5A5` (Clear) | `DestructiveBrush` |
| `#252535` (toggle reveal) | `ButtonBackgroundBrush` |

**CrashReportWindow.axaml**

Also fully disconnected. Has its own in-window style block. The palette is close but shifted (warmer neutrals vs. the cool indigo palette everywhere else). The close button uses `#a13544` which has no equivalent anywhere. This is defensible as a special-purpose emergency surface but the divergence is notable.

---

## Spacing

No spacing tokens exist. The app uses a mix of raw numeric values: `Padding="16,14,16,12"`, `Padding="14,10"`, `Padding="8,4"`, `Spacing="5"`, `Spacing="6"`, `Spacing="8"`, `Spacing="10"`, `Spacing="12"`, `Spacing="20"`. There is no declared scale.

The actual usage cluster around `4`, `6`, `8`, `10`, `12`, `14`, `16`, `20` which is consistent with an 4px base grid, but it is implicit rather than enforced.

---

## Typography

No typography tokens defined. Font sizes in use: `9`, `10`, `11`, `12`, `13`, `14`, `15`, `16`, `18`, `20`, `24`. Weights used: `Normal` (implicit), `Medium`, `SemiBold`. No token names exist for any of these — everything is inline.

The scale is reasonably systematic (10/11/12 for metadata/status/body, 13/14 for labels, 15 for subheadings, 24 for page titles) but it is not enforced and is easy to drift.

---

## Component Inventory

### ✅ Reasonably defined

**`control-pill` button** (MainWindow inline Style)
- States: default, `:pointerover`, `:pressed`, `.active`, `.active` + `:pointerover` (missing)
- Gap: `.active:pointerover` state is not styled — the hover would revert to the non-active surface color, which is visually wrong.
- Gap: `.active:pressed` not defined either.
- Gap: lives only in a `Border.Styles` block on one element in MainWindow. Not reusable from other files.

**`dev-pill` button** (MainWindow inline Style)
- States: default, `:pointerover`, `:pressed`
- Acceptable — intentionally distinct, dev-only.

**Pipeline section header** (pattern, not named)
- Recurring pattern: `Border` with bottom/top `BorderBrush` separator + `TextBlock` in CAPS at `FontSize="10"` `FontWeight="SemiBold"`.
- Used 6 times. Not extracted as a named control or style.

**Provider config group** (pattern, not named)
- Recurring pattern: `StackPanel Spacing="5"` with a label `TextBlock` at 12px + a `ComboBox` with token-backed background/foreground/border.
- Used 9 times identically across Transcription, Translation, Dub sections.

### ⚠️ Partially defined

**Generic Button**
- The pipeline panel uses `CornerRadius="4"` or `"5"` or `"6"` inconsistently — no standard corner radius exists.
- Padding varies: `"8,4"`, `"10,8"`, `"0,10"`, `"0,7"`, `"8,4"`, `"16,7"` — no size scale.
- The ApiKeysDialog defines semantic button variants (Save=green, Validate=blue, Clear=red) that don't exist anywhere else.

**ProgressBar**
- Used in two contexts: pipeline progress (tall, 8px, accent-colored foreground) and model download (slim, 4px, default). The pipeline one has a hardcoded `Foreground="#7C3AED"` that should be an accent token.

**Status indicator dots (Ellipse)**
- Used in ApiKeysDialog (provider status: set/unset) and in the segment list (TTS ready indicator). No shared style, same visual metaphor implemented twice independently.

### ❌ Not defined / one-offs

**`CrashReportWindow`** — fully standalone, warm-neutral palette, its own in-window styles. No token linkage.

**`ApiKeysDialog`** — hardcoded dark-only, parallel color set, no token linkage.

**`ModelDownloadEntry` card** — hardcoded `Background="#1E1E2C"` instead of `ControlBackgroundBrush`.

---

## Naming Issues

| Issue | Example | Recommendation |
|---|---|---|
| Token name redundancy | `BorderBrushBrush` (Brush suffix doubled) | Rename to `BorderBrush` |
| Inconsistent CornerRadius | `4`, `5`, `6`, `8`, `12`, `21`, `22` in use | Define 3 values: `RadiusSm=4`, `RadiusMd=8`, `RadiusLg=12` |
| `PipelineProgressBorderBrush` defined, never used | — | Remove or wire it |
| `SegmentSelectedBrush` / `SegmentPointerOverBrush` defined, never used | — | Wire to `ListBoxItem` styles |
| Section headers are called "TRANSCRIPTION", "TRANSLATION", "DUB", "API KEYS", "LANGUAGE ROUTING", etc. — casing/format is consistent | ✅ | No change needed |

---

## Priority Actions

**1. Fix `BorderBrushBrush` naming** — the doubled "Brush" suffix is a typo-class error that will confuse anyone reading the token list. Rename to `BorderBrush` throughout (this is a find-and-replace across all `.axaml` files, roughly 15 call sites).

**2. Add `AccentBrush` and `DestructiveBrush` to App.axaml** — the purple gradient (`#4F46E5` → `#7C3AED`) is the app's primary action color and appears in at least 4 separate places. The red (`#6B2525`) for Cancel/danger also appears in multiple windows. Naming them creates a single change point for brand adjustments.

**3. Wire `SegmentSelectedBrush` and `SegmentPointerOverBrush` to the ListBox** — these tokens were clearly designed for this purpose but the ListBox inline styles use hardcoded hex values instead, which means the light-theme variants will never work correctly for segment selection.

**4. Extract `control-pill` to a global style in App.axaml** — it is currently defined inside a single `Border.Styles` block. Moving it to the application-level style sheet makes it reusable from future views without copying.

**5. Token-link `ApiKeysDialog` and `SettingsWindow` status card** — both windows are either fully or partially disconnected from the token system. The ApiKeysDialog hardcodes `RequestedThemeVariant="Dark"` which prevents light-theme support. At minimum, the backgrounds and border brushes should use `DynamicResource`.

**6. Define `SurfaceRaisedBrush`** — `#151522` / `#1A1A28` / `#18182A` all represent "elevated surface on a dark panel" and appear 5+ times. They are visually the same intent, slightly different hex values. Consolidate to one token.

**7. Document the implicit spacing scale** — formalizing 4/8/12/16/20 as named constants (even just in a comment block in App.axaml) makes the intent auditable.

---

## What Is Working Well

The semantic separation between `PrimaryTextBrush` and `StatusTextBrush` is sound and used consistently throughout the pipeline panel. The theme-dictionary approach in App.axaml is the right Avalonia pattern for light/dark support. The pipeline panel's always-dark override via `Border.Resources` is an elegant workaround for mixed-theme surfaces. The `control-pill` style demonstrates that the team knows how to write named component styles — it just needs to be promoted to app scope.
