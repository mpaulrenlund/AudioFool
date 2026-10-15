# Proposed CLAUDE.md changes for the PS2 Dark theme

Merge these into `CLAUDE.md` in session 3, once the dark theme works. They replace the opening of "The PS1 theme" section and adjust two of its rules. Everything else in that section stays.

---

## Replace the section heading and its first paragraph with

```markdown
## The themes

AudioFool has two themes, switchable while it runs: **PS1 Light** (the original,
inspired by the PlayStation 1) and **PS2 Dark** (matte black with a blue glow). The
status bar's Theme button and Ctrl+T switch between them; the choice is saved in
settings.json, and the app opens in Dark when nothing has been saved.

- **`design/theme-spec.md`**: source of truth for every size, layout rule and behavior,
  and for the PS1 Light colors. Read it before any UI work.
- **`design/theme-tokens.json`**: the PS1 Light token set.
- **`design/ps2-dark/theme-spec.md`**: only what differs in PS2 Dark (colors, shadows,
  three effects, theme switching).
- **`design/ps2-dark/theme-tokens.dark.json`**: the PS2 Dark token set.
- **`design/progress.md`**: every design decision and what each session verified.
```

## Add to "Rules that must not slip"

```markdown
- **Both token files keep the same keys.** A new token goes into both files in the
  same change. Sizes, spacing and other non-color values are identical in both; only
  `color.*` and `shadow.*` differ. The parity test enforces this.
- **Every color reference must follow a theme switch.** Use DynamicResource for
  `color.*` and `shadow.*` (never StaticResource), and don't hold on to a brush read
  in code: look it up when you draw.
- **Check both themes.** Any visual change is rendered with ThemeLab in Light and Dark.
```

## Replace the color-role rule

Replace

```markdown
- **Teal means playback** (play/pause, seekbar, now-playing row). **Blue is only for artist names.** Don't add colors outside the roles in the spec.
```

with

```markdown
- **The playback color means playback** (`accent.playback`: teal in Light, blue in
  Dark): play/pause, the seekbar, the now-playing row. **Artist names have their own
  color** (`accent.blue`). Don't add colors outside the roles in the two specs.
```

(If the rename in `design/ps2-dark/theme-spec.md` §9 was skipped, write `accent.teal` instead of `accent.playback`.)
