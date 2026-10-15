# AudioFool — PS2 Dark Theme: Design Brief (draft)

**Status:** starting point for design review. Nothing here is final until it's been tried in a mockup. Once decisions are settled, this becomes `theme-spec.md` + `theme-tokens.dark.json` in this folder, the same way the PS1 theme was delivered.

**Location in the repo:** `C:\MusicPlayer\design\ps2-dark\` (alongside the existing PS1 files in `design\`).

---

## 1. Goal

A dark mode for AudioFool inspired by the PlayStation 2, sitting alongside the existing light PS1 theme.

- **Same app, different skin.** Layout, sizes, spacing, and behaviors stay exactly as they are in the PS1 theme. Only colors (and possibly a few effects) change.
- **Same token names.** The dark theme provides a second value for every token in `theme-tokens.json` (`panel.bg`, `text.primary`, `accent.teal`, and so on). The app switches themes by swapping token sets, so no screen needs theme-specific code.
- **Feels like a PS2, not a generic dark mode.** It should be recognizably "that black console with the blue glow."

---

## 2. PS2 reference material

What makes the PS2 recognizable, as raw material for the theme:

| Source | What stands out |
|---|---|
| **The console** | Matte black body; horizontal ridged lines across the top; the blue PS logo; blue light on the eject button; power light green when on, red in standby |
| **System menu / startup** | Near-black, deep navy space; glowing blue-white text; floating translucent cubes and towers; soft light rather than hard edges |
| **Memory card browser** | Deep blue gradients; glossy, glowing icons |
| **DualShock 2** | Black controller; the same four symbol colors as PS1, but sitting on black buttons |
| **Game cases** | Translucent dark blue plastic |
| **Discs** | Blue-tinted underside on the CD-format games |

The PS1 theme was "light gray console plastic with colored buttons." The PS2 counterpart is **"matte black with a blue glow."**

---

## 3. Starting palette (proposal, to be tested in a mockup)

Avoid pure black and pure white. Both look harsh on screen, and neither matches the console's matte finish.

### Surfaces

| Token | Proposed | Notes |
|---|---|---|
| `window.bg` | `#111216` | Matte console black, slight blue undertone |
| `panel.bg` | `#1A1C22` | One step up from the window |
| `panel.border` | `#2B2F38` | Subtle edge, like molded plastic |
| `panel.divider` | `#242831` | Header and table dividers |
| `panel.highlight` | `rgba(255,255,255,0.04)` | Very faint top-edge highlight |

### Text

| Token | Proposed | Notes |
|---|---|---|
| `text.heading` | `#F0F2F6` | |
| `text.primary` | `#E2E5EB` | Off-white, not pure white |
| `text.secondary` | `#B3B9C4` | |
| `text.muted` | `#868D99` | Headers, counts, times |
| `text.placeholder` | `#6E7480` | |

### Accents and states

| Role | Proposed | Notes |
|---|---|---|
| Playback (play/pause, waveform played portion, now-playing triangle) | `#4C8DFF` | "PS2 blue," the eject-light glow. See open question 1. |
| Now-playing row background | `#16233D` | Dark blue tint, like the game cases |
| Now-playing row text | `#9CC0FF` | |
| Artist names | `#A9C8FF` | Pale "system menu" blue-white. See open question 1. |
| Selection background | `#252A34` | Clearly lighter than the panel, without a glow |
| Hover | `rgba(255,255,255,0.04)` | |
| Status on (Bit-Perfect on, Last.fm) | `#3DDC84` | Power light green |
| Status off (Bit-Perfect off) | `#FF5A5F` | Standby red. See open question 3. |

These are starting points chosen for readability on dark backgrounds (aiming for at least 4.5:1 for text and 3:1 for icons). They'll be checked and adjusted in the mockup.

---

## 4. Open questions

These will be decided while reviewing the mockup.

1. **Which color means playback?** In the PS1 theme, teal means playback and blue means artist names. Blue is the PS2's signature color, so it's the natural choice for playback, which means artist names need something else (pale blue-white, as proposed, or plain text). The alternative is to keep teal for playback in both themes for consistency.
2. **Window buttons.** The PS1 theme uses the logo's yellow/blue/red. The PS2 logo is single-color, so the options are the DualShock symbol colors, a single blue, or neutral light gray with colored hover.
3. **Bit-Perfect off color.** Standby red is the most authentic, but red can read as "error." Amber or the PS1 yellow would be gentler.
4. **Glow effects.** A soft glow on the play button, now-playing row, or focus outlines would evoke the system menu. It could also feel busy, and it's more work to build in WPF. Glow only on the play button is a possible middle ground.
5. **Transport buttons.** Matte black DualShock 2–style buttons with colored symbols, or dark gray buttons that stand out more clearly against the black bar.
6. **Switching themes.** Follow the Windows light/dark setting, use a manual toggle, or both? If there's a toggle, where does it live (status bar, settings, keyboard shortcut)?

---

## 5. Current app features to cover

The app has grown since the PS1 spec was written. Both themes need these covered, and the PS1 spec should be updated to match:

- **Waveform seekbar** (played vs. unplayed colors)
- **Shuffle and repeat** as small stacked buttons beside Next
- **Favorite heart** at the end of song rows
- **Sort button** in the Artists header
- **Artist subline** with size ("7 albums · 0.8 GB")
- **Format label** such as "CD Quality Lossless"
- **Full release date** in the album header ("2026-07-26")

---

## 6. Process

1. **Brief** (this document): agree on direction.
2. **Mockup:** build the dark version of the current app on the design canvas, starting from the latest screenshot.
3. **Iterate:** settle the open questions and fine-tune colors in context.
4. **Package:** in this folder, `theme-spec.md` (only what differs from PS1), `theme-tokens.dark.json` (full token set), a screenshot, session prompts, and a `CLAUDE.md` addition.
5. **Build:** likely three sessions with Claude Code: token set and theme switching, component-specific details (waveform, glow if any), then polish and testing.
