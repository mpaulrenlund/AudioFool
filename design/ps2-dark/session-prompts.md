# PS2 Dark — build session prompts

Three Claude Code sessions in `C:\MusicPlayer`, in order. Paste one prompt per session. Each assumes the previous one is done and recorded in `design/progress.md`.

---

## Session 1 — Two token sets and live switching

```text
We're adding a second theme to AudioFool: PS2 Dark, switchable while the app runs.
Read CLAUDE.md and HANDOFF.md first, then design/ps2-dark/theme-spec.md (§1, §3, §6
and §9) and design/ps2-dark/theme-tokens.dark.json.

This session builds the plumbing and the switch. The two dark-only effects (title bar
glow, Play/Pause glow) are the next session.

1. Tokens: add the six new keys in theme-spec.md §6 to design/theme-tokens.json with
   their PS1 (transparent) values. Embed design/ps2-dark/theme-tokens.dark.json as
   AudioFool.theme-tokens.dark.json beside the light file, and copy it into the test
   project's TestData the way theme-tokens.json is.
2. Parity test in AudioFool.Core.Tests: both files parse with ThemeTokens.Parse, they
   have the same set of keys, and every value outside color.* and shadow.* is identical.
3. The rename in §9 (accent.teal → accent.playback, accent.tealText →
   accent.playbackText, in both files and every reference). Ask me before doing it.
4. AppSettings.Theme (Light or Dark, stored by name, default Dark). ThemeService applies
   the saved theme at startup and can switch at runtime by swapping the token
   dictionary (§3.3).
5. Make every color lookup follow a switch (§3.3 item 3): the type.* Foreground setters,
   the 11 static references, the WPF-UI accent, WPF-UI's Light/Dark base dictionary,
   and the dark window frame if Windows draws it.
6. The control (§3.1): the status bar Theme button left of Bit-Perfect, and Ctrl+T.
   Save the setting on every switch. Add Ctrl+T to README's shortcut table.
7. ThemeLab: a --theme light|dark switch.

Verify: dotnet test passes. ThemeLab renders of the main window and every dialog in both
themes. In the installed app: switch while a song plays and a dialog is open, close and
reopen (same theme), and remove Theme from settings.json (opens Dark). Use UI Automation
and PrintWindow, never the mouse or keyboard.

As always: describe the plan and wait for my approval before editing. When done, list
what changed and anything that differs from the spec, and add the decisions to
design/progress.md.
```

---

## Session 2 — Dark-only details

```text
Continue the PS2 Dark theme. Read CLAUDE.md, HANDOFF.md, design/ps2-dark/theme-spec.md
(§7) and the previous session's notes in design/progress.md.

1. Title bar menu glow (§7.1): the radial gradient from color.window.titleGlow and the
   titleBar.glow.* numbers, behind the title bar's content. Invisible in Light because
   the color is transparent there.
2. Play/Pause (§7.3): the 1 px color.control.playRing ring and the shadow.playGlow glow,
   on an element behind the button so its own molded drop shadow stays.
3. Check the rest of §7 against ThemeLab renders in Dark: window button hover (Close
   still turns red with a white icon), Shuffle and Repeat on colors and the repeat-one
   badge, status lights, the now-playing row, a liked heart, the waveform, the focused
   search box.

Render with ThemeLab in both themes (Light must look exactly as before) and compare with
the spec. Describe the plan and wait for my approval before editing. Record the outcome
in design/progress.md.
```

---

## Session 3 — Polish and testing

```text
Finish the PS2 Dark theme. Read CLAUDE.md, HANDOFF.md, design/ps2-dark/theme-spec.md
(§8, §10, §11) and the earlier sessions' notes in design/progress.md.

1. Every window in both themes at 100%, 125% and 150% scaling with ThemeLab: main window,
   tags, Statistics, Analysis, Last.fm, art search, art viewer, prompts, menus and
   tooltips. List anything that still shows a light value in Dark or a dark value in
   Light.
2. Spot-check the contrast figures in §8 against ThemeLab's brush dump.
3. Switch themes with each dialog open; nothing may stay in the old theme.
4. Docs: merge design/ps2-dark/claude-md-addition.md into CLAUDE.md; fix the PS1 spec's
   slider.volumeFill entry (§10); make sure README lists Ctrl+T; save dark and light
   renders to design/ps2-dark/screenshots/.

Describe the plan and wait for my approval before editing.
```
