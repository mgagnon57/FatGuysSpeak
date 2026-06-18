# Client UI Redesign — "Flow Like Water", Red/Black to Match the Landing

**Date:** 2026-06-18
**Status:** Draft for review (mockups attached — see `docs/mockups/client-ui-mockups.html`)

## Goal

Make the MAUI client feel like a natural extension of the landing page — the same gritty
**red-on-warm-black** identity — and make the whole experience *flow like water*: cohesive
color, clear hierarchy, soft motion, and zero visual "seams." No feature changes; this is a
look-and-feel + interaction-polish pass.

## Palette (from the landing page, confirmed: red + black)

| Landing var | Hex | Role |
|---|---|---|
| `--bg`  | `#0a0909` | darkest — headers, server rail, inputs |
| `--bg2` | `#101010` | primary content background |
| `--bg3` | `#191717` | panels / sidebar |
| `--bg4` | `#222020` | elevated controls, hover |
| `--red` | `#d42d00` | primary action / active |
| `--red2`| `#f04010` | bright accent / glow / focus |
| `--amber`|`#e89000` | warnings |
| `--green`|`#36b864` | success / online |
| `--text`| `#e9e4d9` | warm off-white text |
| `--muted`|`#756e62` | secondary/muted text |
| `--border`|`#292525`| dividers/borders |
| fonts | Bebas Neue / DM Mono | display / UI-mono |

### Maps onto the existing `ThemeService` token set (new default theme "Ember")

```
ThemeBgPrimary     = #101010   ThemeAccent        = #d42d00
ThemeBgPanel       = #191717   ThemeAccentLight   = #f04010
ThemeBgHeader      = #0a0909   ThemeTextPrimary   = #e9e4d9
ThemeBgElevated    = #222020   ThemeTextSecondary = #756e62
ThemeBgInput       = #0a0909   ThemeTextMuted     = #4a443d
ThemeDivider       = #292525
```

New tokens to add (so banners/states stop using hardcoded hex):
`ThemeBgActive` (`#241310`, red-tinted active row), `ThemeAccentMuted` (`rgba(212,45,0,.12)`),
`ThemeDanger` (`#d42d00`), `ThemeWarning` (`#e89000`), `ThemeSuccess` (`#36b864`).

`Ember` becomes the default in `ThemeService` (keep Dark/Midnight/OLED selectable).

## "Flow like water" principles

1. **One source of color.** Every surface reads a `Theme*` token — no stray `#1e1e1e`/`#d0d0d0`.
   Today `MainPage.xaml` alone hardcodes ~30 hex values; those get replaced. This is what makes
   it feel cohesive instead of patched.
2. **Red is a spice, not a wall.** Black/warm-grey dominates; red marks *active channel, primary
   button, focus ring, online glow, mentions*. Everything else is calm.
3. **Soft motion.** Channel/server switches cross-fade; new messages slide+fade in; hover states
   transition (150ms); the page-nav uses a gentle fade. The version-sync overlay already fades —
   extend the same easing everywhere.
4. **Rhythm & breathing room.** A 4/8/12/16 spacing scale; consistent 8–10px corner radius on
   cards, inputs, avatars, buttons; hairline `ThemeDivider` borders instead of hard black lines.
5. **Status legibility.** Online/idle/dnd dots in green/amber/red; clear role grouping; muted
   timestamps in DM Mono so they recede.
6. **Focus & affordance.** Inputs get a red focus glow; the active channel gets a red left-rail
   pill (like the landing's accent bars); hover reveals message actions instead of clutter.

## Typography (optional, recommended)

Bundle **Bebas Neue** (display) for big headers — login title, server name, section labels —
and **DM Mono** for timestamps/counts/IDs, matching the landing exactly. Body stays the system
UI font for readability. Requires adding the TTFs to `Resources/Fonts` + `ConfigureFonts`.

## Per-screen intent (see mockups)

- **Login** — centered card on grain+red-glow; "WELCOME BACK" in Bebas; red focus on fields;
  solid red "LOG IN" with a soft glow; ghost "Continue with Google"; quiet register link.
- **Main chat (hero)** — server rail with active red pill; sidebar with red-tinted active channel
  and calm hovers; message rows with hover-reveal actions, rounded avatars, reaction chips, reply
  spine, mention highlight; members grouped by role with status dots; input with red focus + red
  send; a slim connected-voice bar.
- **Settings** — Appearance section shows theme swatches with **Ember** selected; About shows the
  live version (ties into the version-sync work).
- **User profile modal** — dim overlay + card: big avatar, status dot, "online 2h", role chips.
- **Version-sync overlay** — the existing blocking overlay restyled red/black: title, red
  progress bar, countdown — consistent with the rest.

## Implementation plan (phased; each phase builds + runs clean before the next)

- **Phase 1 — Theme foundation.** Add the `Ember` theme + new tokens to `ThemeService`; set as
  default; add `App.xaml` token defaults. Verify the app recolors via the existing DynamicResource
  wiring. (Small, high-impact: most themed surfaces flip immediately.)
- **Phase 2 — Detoken the hardcoded hex.** Sweep `MainPage.xaml`, `LoginPage.xaml`,
  `RegisterPage.xaml`, `SettingsPage.xaml`, `UserProfilePage.xaml`, `StreamViewPage.xaml`,
  `WindowPickerPage.xaml`, `VideoPlayerPage.xaml` — replace literal colors with `Theme*` tokens
  (and the new state tokens for banners). This is the bulk of "cohesion."
- **Phase 3 — Flow polish.** Active-channel red pill; unified hover/active visual states; corner
  radii + spacing scale; input focus glow; cross-fade on channel/page switch; message slide-in.
- **Phase 4 — Typography (optional).** Bundle Bebas Neue + DM Mono; apply to headers/mono bits.

Each phase is independently shippable and reviewable. Tests: UI is visual — verify by building +
launching two clients and eyeballing against the mockups (the established manual-run pattern).

## Out of scope

- Layout/feature restructuring (the familiar Discord-like layout stays — we elevate, not rebuild).
- Light mode. Non-Windows visual parity.
