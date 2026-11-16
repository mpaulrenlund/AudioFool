# A built-in equalizer: plan for a new session

Written 2026-11-16 (end of session 78) at the user's request: "I wonder if there's a way
to build in an equalizer like Equalizer APO?", then "draft the plan … for a new chat".
**Built in session 79** (committed `87f7574`): the plan below as it was written, with
the user's answers and each piece's results added under the questions. Read it before
changing the equalizer; README's *Equalizer* section says how it behaves.

## Why the user wants it

The user already uses **Equalizer APO with Peace** (its front end), installed at
`C:\Program Files\EqualizerAPO`, with their own presets for their IEMs and headphones.
Equalizer APO works inside Windows' shared audio engine, so it only reaches AudioFool in
**Shared** mode. In **Bit-Perfect** (exclusive WASAPI) AudioFool talks to the DAC
directly and Equalizer APO never sees the music. A built-in EQ works in both modes.

At the time of writing Peace had no preset active (`config\config.txt` is
`Include: peace.txt`, and `peace.txt` is empty). If both are on in Shared mode the music
gets both EQs; worth one line in the README.

## Ask the user first (before any code)

Each has a recommendation; the user decides. Ask them in one go.

1. **Bit-Perfect with EQ on.** An EQ changes every sample, which is what "bit-perfect"
   rules out. *Recommended:* EQ still runs over the exclusive connection (the file's own
   rate, no resampling, no Windows mixing); the output readout under the volume slider
   says so ("Exclusive · 44.1 kHz · EQ"); the Bit-Perfect chip keeps its meaning of
   "exclusive connection". *Alternative:* EQ only in Shared mode.
2. **DSD with EQ on.** An EQ can't work on DSD sent as DoP (any change turns the markers
   into hiss). *Recommended:* in Bit-Perfect, DoP stays untouched and the EQ is skipped
   for that track (the readout says "DSD over PCM", no "EQ"); in Shared, DSD is already
   converted to PCM, so the EQ applies. *Alternative:* with EQ on, convert DSD to PCM in
   Bit-Perfect too.
3. **Remembered across launches?** *Recommended:* yes (on/off and the preset), unlike
   Bit-Perfect, which starts off every launch because it silences other apps; an EQ
   doesn't.
4. **What it looks like.** The spec is silent, so this is the user's design call, and
   the usual rule applies: describe the plan, wait for approval. *Recommended first
   step:* an **EQ** chip in the status bar's right group (spec §6.8: the same chip as
   Theme and Bit-Perfect, 7 px dot + "EQ", the dot showing on/off), opening a menu: Off,
   then the presets, a check on the one in use. A slider editor like Peace's is a later,
   separate step. No tooltips (standing rule); keep accessible names.
5. **Where presets come from.** *Recommended:* the Peace presets read in place, read
   only (`C:\Program Files\EqualizerAPO\config\*.peace`; the install path is also in the
   registry under `HKLM\SOFTWARE\EqualizerAPO`), plus AudioFool's own folder for
   Equalizer APO / AutoEq text files the user drops in (`%APPDATA%\AudioFool\eq\`).
   Never write into Program Files.

**Answered 2026-11-16 (session 79): all five as recommended.** EQ runs over the
exclusive connection (readout "· EQ"), DoP skips it, Shared DSD gets it; remembered
across launches; Peace presets read only plus `%APPDATA%\AudioFool\eq\`; an EQ chip +
menu in the status bar (details still go through plan-then-approve before any UI edit).

**Piece 1 done (session 79):** `src/AudioFool.Core/Equalizer/` (`Biquad`, `EqDesign`,
`EqProcessor`, `PeacePresetReader`, `EqualizerApoTextReader`, `EqPresetLibrary`), 51
tests in `EqualizerTests.cs`, fixtures in `TestData/eq/`. The shelf maths and the text
reader follow Equalizer APO's own source (`BiQuad.cpp`, `BiQuadFilter.cpp`,
`BiQuadFilterFactory.cpp`, GitHub mirror `mirror/equalizerapo`), including its
default shelf slope (S 0.9) and the LS/HS "corner frequency" shift. Peace's
`[Filters]` codes stay unsupported (its help lists the filters but not the numbers).
Against the real Peace folder: the user's four and the peak-only stock presets read;
*Bass Boost 2*, *Bass and High Boost 2*, *High Boost 2* (shelves), *Graphic EQ*,
*Left on Right channel* (routing) and *Surround Effect Default* are reported unsupported.

**Piece 2 done (session 79), not installed:** `OutputChain.ApplyEqualizer` (after the
seek fades, before `AfterPull` and the volume; a change crossfades over 10 ms; nothing
touched with no design; a block that starts on DoP untouched), `AudioEngine.Equalizer`,
`TrackEnd` (DoP and PCM don't splice while the EQ is on), `OutputReadout` "· EQ",
"eq:" log lines and the minute line's "eq max … ms a block, … samples clipped".
Headless probe (shared, muted; `scratchpad\eqprobe`, session 79): EQ off bit-identical
over 10.2 s; EQ on bit-identical to `EqProcessor` offline after the 10 ms fade; preset
switches ≤ 1.0× the same window unswitched (hard switches 1.7–2.5×); a gapless join
bit-identical to offline with the state carried over, step 0.29× the tracks' own; 0
samples clipped with IER-M9 on *Shoot to Thrill*; worst block 1.8 ms. Real DoP not
probed (needs exclusive); the suspension itself was (300 blocks bit-identical to dry).

**Piece 3 done (session 79):** `AppSettings.EqualizerPreset` / `EqualizerFolder`
(remembered; a preset whose file has gone leaves the EQ off), `MainViewModel.Equalizer.cs`
(Peace's folder from `HKLM\SOFTWARE\EqualizerAPO\ConfigPath`; only supported, non-flat
presets offered), the chip and menu (`MainWindow.Equalizer.cs`), tokens
`status.eqOff` / `status.eqOn`. ThemeLab `--window equalizer` ALL OK in Light and Dark;
design/progress.md has the details. Spec §6.7 and §6.8, README *Equalizer*. Then
**Open Peace…** in the menu and an edit to the preset in use taken up as the menu opens
(the user's request, same session).

## What the presets look like (measured 2026-11-16)

The user's own four presets: *IER-M9 ChatGPT* (PreAmp −5.1), *HD650 Chat GPT* (no
PreAmp line, so 0), *Marcus Bass Boost* (−3.8), *Tin P1's* (−4). All are Peace's
"13 sliders equalizer using peak filters": **peak filters only**.

`.peace` is an INI file:

- `[Frequencies]` `Frequency1=10` … `Frequency13=20000`; `[Gains]` `Gain1=2` … (a
  missing GainN is 0); `[Qualities]` `Quality1=1.41` … (the Q of each band).
- `[General]` `PreAmp=-5.1` (dB; missing is 0), `Description=…`.
- `[Speakers]` and per-speaker `[FrequenciesN]` / `[QualitiesN]` (N = 1–8). In the
  user's presets these carry no gains; only "All" (the unnumbered sections) does
  anything. A preset with per-speaker `[GainsN]` exists among Peace's stock ones
  (*Equalizer 5 Band*, *Equalizer One Third Octave*, *Surround Effect Default*).
- In stock presets only: `[Filters]` `Filter5=14` / `Filter10=15` (a filter type per
  band; 14 sits on a bass band and 15 on a treble one, so probably low and high shelf,
  **not verified**), `[Disabled]` `Disabled6=1`, `GraphicEQ=1`, `[Surround]`. Don't
  guess these: support peak + preamp first, and show any preset using something else as
  not supported, or verify the codes from `Peace.chm` (in the same folder; decompile into
  the scratchpad with `hh.exe -decompile`, never into Program Files). Never activate a
  preset in Peace to find out: that changes the user's system-wide sound.

Equalizer APO / AutoEq text: `Preamp: -6.2 dB`, `Filter 1: ON PK Fc 105 Hz Gain 2.4 dB
Q 0.70`, also `LSC` / `HSC` (shelves; check Equalizer APO's documentation for how it
reads Q on a shelf before relying on it), `OFF` filters skipped. `GraphicEQ:` lines are a
later step.

## How it fits the playback chain (read the code; line numbers as of 2026-11-16)

- `AudioFool.Core/Playback/OutputChain.cs`: one BASSmix **float** mixer per device
  connection, at a fixed rate (`MixerRate`: the device's shared mix rate, or the track's
  own rate in exclusive). Every block the DAC gets passes through `Fill` (line 138): pull
  from the mixer, seek fades, `AfterPull` (the engine's timeline and the visualizer tap),
  then `ApplyGain` (the volume, line 191). **The EQ goes in `Fill`, after the seek fades
  and before `AfterPull`/`ApplyGain`**, in place on the block.
- `Fill` runs on the device's thread: no allocation, no locks, no logging there (see
  `SampleTap`'s notes). Build the filters off that thread as an immutable set and swap it
  in with `Volatile.Write`, as `_pendingSeek` is; keep the filter state in arrays the
  chain owns, set aside when the chain is created.
- **Rate:** a chain never changes rate, so work the coefficients out for `MixerRate` when
  the chain is created and when the preset changes. A band at or above half the rate is
  left out.
- **DoP:** `AudioEngine` knows which streams are DoP (`_isDopHandle`, line 41;
  `IsDopActive`, line 579). The EQ must not touch a block that carries DoP. Check
  `TrackEnd` (line 49) and `DopRule` for whether a DoP track can ever share a block with a
  PCM one on an exclusive chain (a gapless join at a matching rate); if it can, bypass for
  the whole join or don't join while the EQ is on.
- **Off means untouched:** with the EQ off (or a flat preset with PreAmp 0), `Fill` must
  not touch the samples at all, so Bit-Perfect stays bit-perfect. This is the most
  important property to prove.
- `OutputChain.SupportsVolume` (line 240) keeps the volume disabled in exclusive mode;
  leave that alone (not asked).
- Gapless joins and seeks keep running the same filters, so the state carries over and
  nothing clicks; a **preset change mid-song** swaps the coefficients, which can click:
  measure it the way session 61's seek fade was measured (largest sample-to-sample step
  against the track's own) and fade or crossfade if it's audible.
- Chain creation: `AudioEngine.cs` lines 1452 and 1470; the volume is handed over at
  1478 (the EQ gets handed over the same way). The readout: `OutputReadout.Describe`
  (`OutputReadout.cs` line 15), called at `AudioEngine.cs` line 571; spec §6.7's readout
  line (`theme-spec.md` line 308) changes with it.
- `AudioEngine` has no tests (HANDOFF step 5). Keep the logic in small tested Core
  pieces, as session 67 did with `TrackEnd`, `ReopenPoint` and `DopRule`.

## The filters

Standard biquads from the RBJ *Audio EQ Cookbook* (the same kind of filter Equalizer APO
runs). Peak, for
gain G dB, centre f0, quality Q, rate fs: A = 10^(G/40), w0 = 2π·f0/fs,
α = sin(w0)/(2Q); b0 = 1 + α·A, b1 = −2·cos w0, b2 = 1 − α·A, a0 = 1 + α/A,
a1 = −2·cos w0, a2 = 1 − α/A (divide all by a0). The preamp is a plain gain in front.

- **Double precision** for coefficients and state (transposed direct form II): a 10 Hz
  band at 192 kHz is badly behaved in float. Samples stay float in and out.
- Cost is small: 13 bands × 2 channels × 192 kHz ≈ 5 million filter steps a second.
  Measure the time per block anyway and log it once per chain.
- **Clipping:** boosts can push samples past full scale, which the device's conversion
  clips. The preamp is there for that. Count samples over full scale per block on the
  device thread (a counter only) and log the count off the thread, so a preset that clips
  shows in `playback.log`.

## Pieces, in order

1. **Core, no UI, no engine** (`AudioFool.Core`, new `Equalizer/` folder): the biquad and
   the filter set (`EqFilter`, `EqChain` or similar), the `.peace` reader, the Equalizer
   APO text reader, a preset model (name, preamp, bands, what isn't supported). Unit
   tests: coefficients against the cookbook; the response at f0 is the band's gain (to
   0.01 dB) and far from it about 0 dB; a 1 kHz sine through a +6.02 dB band at 1 kHz comes
   out at twice the amplitude; a flat preset is exactly a pass-through; the user's four presets
   parse (copy them into the test fixtures; they're the user's own files, fine to keep in
   the private repo); unsupported Peace features are reported, not guessed.
2. **Engine:** the hook in `OutputChain.Fill`, the hand-over from `AudioEngine` (a
   `SetEqualizer(preset or null)`, kept across chain rebuilds, sample-rate changes and
   device switches), the DoP bypass, the readout, `playback.log` lines ("eq: IER-M9
   ChatGPT at 44.1 kHz, 13 bands", clip counts). **Verify headless** against
   `AudioFool.Core` (docs/verifying.md), in a subagent: EQ off gives bytes identical to
   today's; a real FLAC with the IER-M9 preset matches the filters' predicted response;
   a DSD file in Bit-Perfect still has intact DoP markers with EQ on (README's *DSD*
   section shows how to check them); no click on a preset change; a gapless album plays
   through without a join click.
3. **Settings and UI**, after the user approves the design: `AppSettings`
   (`AudioFool.Core/Settings/AppSettings.cs`) gains the EQ choice; the chip and menu;
   tokens for anything new in **both** token files; ThemeLab renders in Light and Dark
   (subagent); spec §6.7 and §6.8, `design/progress.md`, README.
4. **Ship** with `tools\ship.ps1` (ask before closing AudioFool if it's open), then the
   user tries the IER-M9 preset in Shared and in Bit-Perfect.

## Traps

- Claude's shell sees a private copy of `%APPDATA%` and `%LOCALAPPDATA%`
  (docs/traps.md): test with temp folders, and never treat those copies as the real
  app's settings or presets.
- Don't launch the installed AudioFool from the shell (it writes `playback.log`); never
  drive the mouse or keyboard; renders and probes go to a subagent.
- Peace's folder is under Program Files: read only.
- The 13 BASS DLLs in `lib\bass\x64` don't include `bass_fx.dll`. Writing the filters
  in C# keeps them testable and needs no new native DLL; don't add one without asking.

## Not in this change

A slider editor (a later step, its own design round), the GraphicEQ format, per-device
or per-headphone presets switching on their own, volume in exclusive mode, and anything
else Equalizer APO does (convolution, delays, routing, surround).
