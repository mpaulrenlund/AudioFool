# Session kickoff prompts

Paste the matching message at the start of each Claude Code session. `CLAUDE.md` loads automatically, but these keep each session focused.

Suggested model per session is in brackets. Use the model's default effort level unless a session goes wrong (see the note at the bottom).

---

## Session 1 — Setup and theme foundation [Opus]

```
We're implementing the PS1 theme for AudioFool. Read design/theme-spec.md,
design/theme-tokens.json, and design/progress.md first.

This session: session 1, setup and theme foundation.
- Create a new branch for the theme work.
- Look at how styling is organized in the app today and propose how to
  build one central theme from theme-tokens.json (colors, type, radii,
  spacing, shadows).
- Don't restyle any screens yet; just build the foundation and make sure
  screens can read from it.

Before editing anything, explain how styling works now and your plan.
When you're done, list what changed, then update design/progress.md.
```

## Session 2 — Title bar and panels [Sonnet]

```
We're implementing the PS1 theme for AudioFool. Read design/theme-spec.md
and design/progress.md first.

This session: session 2, title bar and panels (spec sections 6.1–6.4).
Stay within that scope; don't change the song table, playback bar, or
status bar. Use values from the central theme, not hard-coded ones.

Before editing anything, tell me your plan. When you're done, list what
changed and anything that differs from the spec or the screenshots,
then update design/progress.md.
```

## Session 3 — Album header and song table [Opus or Sonnet]

```
We're implementing the PS1 theme for AudioFool. Read design/theme-spec.md
and design/progress.md first.

This session: session 3, album header and song table (spec sections
6.5–6.6). Pay particular attention to the row states (selected vs. now
playing), the # column with the triangle slot, and single-click select /
double-click play. Stay within that scope. Use values from the central
theme, not hard-coded ones.

Before editing anything, tell me your plan. When you're done, list what
changed and anything that differs from the spec or the screenshots,
then update design/progress.md.
```

## Session 4 — Playback bar and status bar [Sonnet]

```
We're implementing the PS1 theme for AudioFool. Read design/theme-spec.md
and design/progress.md first.

This session: session 4, playback bar and status bar (spec sections
6.7–6.8), including the library size calculation. Note the alignment rule:
the Shuffle button lines up with the start of the song table's # column.
Stay within that scope. Use values from the central theme, not hard-coded
ones.

Before editing anything, tell me your plan. When you're done, list what
changed and anything that differs from the spec or the screenshots,
then update design/progress.md.
```

## Session 5 — Polish and testing [Sonnet]

```
We're implementing the PS1 theme for AudioFool. Read design/theme-spec.md
and design/progress.md first.

This session: session 5, polish and testing (spec sections 7–9). Compare
each part of the app against design/screenshots/, check keyboard focus
and accessible names, test at 125% and 150% display scaling, and run the
edge cases in progress.md. Walk the "What's changing" checklist in spec
section 9 to catch anything left from the old theme.

Start by listing every difference you find, without fixing anything.
I'll pick which to fix.
```

---

## If a session goes wrong

- First check the request: was the scope clear, and did Claude read the spec?
- If Claude had the right context and clearly tried but still got it wrong → switch to a more capable model (Sonnet → Opus).
- If it skipped files, didn't check its work, or stopped partway → raise the effort level.
- If a session has run long, start a fresh one rather than pushing on.
