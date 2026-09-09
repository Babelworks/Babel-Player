# Typography

## Purpose

`Styles/Typography.axaml` is the single source of truth for UI text in Babel Player. It exists so that views stop hard-coding font families, sizes, and weights, and instead reach for a small set of semantic classes and `DynamicResource` keys.

The file defines:

- The UI font families (Inter for sans, Cascadia Mono for mono) as `DynamicResource` keys.
- A fixed 6-step font size scale, also exposed as `DynamicResource` keys.
- Sensible defaults on common controls (`Window`, `TextBlock`, `TextBox`, `ComboBox`, `CheckBox`, `RadioButton`, `Button`) so most text looks right with no markup.
- A set of `TextBlock` *semantic classes* (`text-display`, `text-title`, `text-secondary`, `text-caption`, `text-label`, `text-emphasis`, `text-overline`, `text-micro`, `text-mono`) for the common text roles in the app.

`Typography.axaml` is merged into the app by `App.axaml`:

```xml
<Application.Styles>
    <StyleInclude Source="/Styles/Typography.axaml" />
    ...
</Application.Styles>
```

It is loaded once at startup, so every view in the app sees the same defaults and classes. Views should not redefine font families, the size scale, or the semantic classes locally.

---

## What it defines

### Font families

| Key | Value |
|---|---|
| `UiFontFamily` | `Inter, system-ui, -apple-system, "Segoe UI", sans-serif` |
| `UiMonoFontFamily` | `"Cascadia Mono", "Cascadia Code", Consolas, monospace` |

Inter is loaded application-wide via `WithInterFont()`; the fallbacks exist for platforms where Inter cannot be resolved.

### Font size scale

A deliberately small, compact scale tuned for dense desktop UI. These are the only sizes views should use.

| Key | Value | Typical use |
|---|---|---|
| `FontSizeDisplay` | `15` | Largest headings, page-level titles (for example the `Pipeline` panel header). |
| `FontSizeTitle` | `14` | Dialog titles and section titles. |
| `FontSizeBody` | `13` | Default body text (implicit on `TextBlock`, `TextBox`, `ComboBox`, `Window`). |
| `FontSizeSecondary` | `12` | Field labels and supporting copy next to controls; also the default for `CheckBox`, `RadioButton`, `Button`. |
| `FontSizeCaption` | `11` | Short helper text, pill-shaped buttons, small descriptions. |
| `FontSizeMicro` | `10` | Smallest metadata, all-caps section rails. |

### Control defaults

These setters apply automatically without any class or `DynamicResource` reference at the call site:

| Control | Family | Size | Weight |
|---|---|---|---|
| `Window` | `UiFontFamily` | `FontSizeBody` | (inherits) |
| `TextBlock` | `UiFontFamily` | `FontSizeBody` | `Normal` |
| `TextBox` | `UiFontFamily` | `FontSizeBody` | — |
| `ComboBox` | `UiFontFamily` | `FontSizeBody` | — |
| `CheckBox` | `UiFontFamily` | `FontSizeSecondary` | — |
| `RadioButton` | `UiFontFamily` | `FontSizeSecondary` | — |
| `Button` | `UiFontFamily` | `FontSizeSecondary` | `Normal` |
| `Button.control-pill` | (inherits) | `FontSizeCaption` | (inherits) |

If your text belongs to any of these controls and looks like regular body copy, the default is already correct — do not add a `FontSize` or `FontWeight` setter.

### Semantic text classes

All of these are `TextBlock` classes. Apply them with the `Classes` attribute on a `TextBlock`.

| Class | Size | Weight | Intended use |
|---|---|---|---|
| *(no class)* | `FontSizeBody` (13) | `Normal` | Default body copy. Most user-facing lines should stay on this. |
| `text-display` | `FontSizeDisplay` (15) | `SemiBold` | Page-level title (for example the top-of-panel label). |
| `text-title` | `FontSizeTitle` (14) | `SemiBold` | Dialog or card title. |
| `text-emphasis` | `FontSizeBody` (13) | `Medium` | Body text that needs slight emphasis, for example the translated line in the segment list. |
| `text-secondary` | `FontSizeSecondary` (12) | `Normal` | Labels next to controls, supporting copy. |
| `text-caption` | `FontSizeCaption` (11) | `Normal` | Short helper text, inline descriptions. |
| `text-label` | `FontSizeCaption` (11) | `Medium` | Small form field names — label-weight, not bold. |
| `text-overline` | `FontSizeMicro` (10) | `Medium` | All-caps section rails (`TRANSCRIPTION`, `TRANSLATION`, `TARGET LANGUAGE`, etc.). Put the text itself in upper case; the class does not transform case. |
| `text-micro` | `FontSizeMicro` (10) | `Normal` | Smallest metadata and diagnostic lines. |
| `text-mono` | `FontSizeSecondary` (12) | `Normal`, `UiMonoFontFamily` | Code, IDs, paths, timings that should line up monospace. |

The naming follows the pattern already visible across the app: larger/stronger roles (`display`, `title`) use `SemiBold`; quieter roles (`secondary`, `caption`, `micro`) stay `Normal`; form-oriented emphasis (`label`, `emphasis`, `overline`) uses `Medium` instead of bold. Prefer classes over sprinkling `FontWeight` on individual `TextBlock`s.

---

## How to use it

### Rule of thumb

1. If the text is regular body copy inside a `Window`, write a plain `TextBlock` with no font attributes. The defaults handle it.
2. Otherwise pick the closest semantic class from the table above and set `Classes="..."`.
3. Only reach for raw `DynamicResource` font keys (`FontSizeX`, `UiFontFamily`, `UiMonoFontFamily`) in custom control templates where a class cannot apply.
4. Do not hard-code font families, sizes, or weights in views.

### Applying a class

```xml
<!-- Panel title -->
<TextBlock Grid.Column="0" Text="Pipeline" Classes="text-display" />

<!-- Section rail -->
<TextBlock Text="TARGET LANGUAGE" Classes="text-overline" />

<!-- Field label next to a ComboBox -->
<TextBlock Text="Compute" Classes="text-secondary"
           Foreground="{DynamicResource PrimaryTextBrush}" />

<!-- Small helper text under a control -->
<TextBlock Classes="text-caption"
           Foreground="{DynamicResource StatusTextBrush}"
           Text="Used as a hint for automatic language detection." />
```

Classes are space-separated, so combining them with other per-view classes is fine:

```xml
<TextBlock Classes="text-caption warning-inline" Text="..." />
```

### Using `DynamicResource` font keys directly

Inside a custom `ControlTemplate`, a derived control style, or anywhere a `TextBlock` class cannot reach, pull from the resource keys instead of hard-coding values:

```xml
<Setter Property="FontFamily" Value="{DynamicResource UiFontFamily}" />
<Setter Property="FontSize"   Value="{DynamicResource FontSizeSecondary}" />
```

For monospace content:

```xml
<TextBlock FontFamily="{DynamicResource UiMonoFontFamily}"
           FontSize="{DynamicResource FontSizeSecondary}"
           Text="{Binding SegmentId}" />
```

Using `DynamicResource` (not `StaticResource`) is required so the values resolve against the merged resource dictionary at runtime — the same way brush tokens in `App.axaml` resolve.

### Setting colour alongside typography

`Typography.axaml` does **not** set `Foreground`. Pair any text class with a brush token from `App.axaml` when the default foreground is wrong:

```xml
<TextBlock Classes="text-secondary"
           Foreground="{DynamicResource StatusTextBrush}"
           Text="{Binding StatusMessage}" />
```

See `docs/history/design/design-system-audit.md` and `docs/history/design/design-system-handoff.md` for the colour token inventory.

---

## What not to do

- **Do not hard-code sizes or families.** `FontSize="12"`, `FontFamily="Segoe UI"`, and similar literals bypass the scale and will drift. Use a class or a `DynamicResource` key.
- **Do not invent new sizes.** The scale is intentionally 6 steps (10 / 11 / 12 / 13 / 14 / 15). New magic numbers should be a discussion, not an inline edit.
- **Do not redefine `text-*` classes locally.** If a view needs a genuinely new text role, add it to `Styles/Typography.axaml` so every view picks it up. Keep the `text-<role>` naming.
- **Do not use StaticResource for font keys.** Use DynamicResource so theme and style changes propagate consistently.
- **Do not mix `StaticResource` for font keys.** Use `DynamicResource` so theme and style changes propagate consistently.
- **Do not use `text-overline` for non-rail text.** It is specifically the all-caps `FontSizeMicro` (10) Medium rail style used for section headers; regular captions should use `text-caption` or `text-micro`.

---

## Adding a new text role

New roles should be rare. If one is justified:

1. Add a `Style Selector="TextBlock.text-<role>"` block to `Styles/Typography.axaml` that sets only the properties that differ from the `TextBlock` default (family, size, weight).
2. Reuse an existing `FontSize*` key; introduce a new size only if no existing step fits.
3. Update the class table in this document.
4. Convert at least one real call site so the role is proven in use, not speculative.

Keep the file compact. The point of `Typography.axaml` is that a contributor can read it end-to-end and know every text role the app supports.
