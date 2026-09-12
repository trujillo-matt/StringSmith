# StringSmith — Milestone 1 implementation plan

Status: **approved; steps 1-7 implemented and green headlessly (see CLAUDE.md, Status). Step 8 (UI) in progress.**

Everything below rests on source I read in `iminashi/Rocksmith2014.NET` at commit
`b87c9a3` and `CoderLine/alphaTab` at current `main`, plus spikes run in this
environment. Constants and API shapes are quoted from those sources, not recalled.
Claims about macOS behaviour are marked, because this environment is headless Linux
x86_64 and cannot test them.

---

## 1. Answers to the five unverified items

| # | Item | Answer | Confidence |
|---|---|---|---|
| 1 | Arrangement XML names for difficulty levels and phrase max difficulty | `<levels>/<level difficulty>` (sbyte); `<phrases>/<phrase maxDifficulty name disparity ignore solo>` (byte). Full list in `CLAUDE.md`. | **High** — read from the `IXmlSerializable` implementations |
| 2 | NuGet vs project reference | Only `Rocksmith2014.XML` (1.1.1) is published. The other nine 404. Submodule + project references required. | **High** — queried `api.nuget.org` |
| 3 | AlphaTab headless | **Works.** Parses GP3/GP4/GP5/GPX/GP with no display, no fonts, no rendering deps loaded. | **High** — spiked, see §2 |
| 4 | DLC Builder UI framework | **Avalonia** 11.3.12 + FuncUI 1.5.2 + FuncUI.Elmish, Fluent theme, Elmish MVU. Precedent holds. | **High** — read `DLCBuilder.fsproj` |
| 5 | Wwise macOS CLI + detection | `/Applications/Audiokinetic/<20{19,21,22,23}*>/Wwise.app/Contents/Tools/WwiseConsole.sh`, `generate-soundbank "<tmp>/Template.wproj" --platform "Windows" --language "English(US)" --no-decode --quiet`. Input is **WAV**, not OGG. No `WWISEROOT` on macOS. | **High** for the code path; **Unverified** that it runs, since no macOS or Wwise here |

### Corrections to the brief

Two stated constants are wrong, and one rationale is weaker than stated.

1. **`ARC_IV = E915AA01...` is not used.** PSARC TOC encryption is AES-256-CFB with a
   **16-byte zero IV**, both directions. That string appears nowhere in the repository.
   SNG is a different scheme entirely: AES-256-**CTR**, IV stored in the file, written
   as zeros.
2. **The first PSARC entry is not named `NamesBlock.bin`.** It is a nameless entry
   (`Name = String.Empty`), whose MD5 digest is therefore 16 zero bytes. Hashing the
   literal string would break the archive.
3. The brief justified .NET partly on the ML.NET DD level predictor. That is true as far
   as it goes, but **DLC Builder's shipped default is `LevelCountGeneration.Simple`, not
   `MLModel`** (`Configuration.fs:107`). The .NET decision is still correct — DD, SNG and
   PSARC are all .NET regardless — but the ML.NET argument carries less weight than stated.

Also: the repo targets **`net10.0`**, not net8.0. And the brief's audio architecture is
inverted: **FFmpeg is used nowhere** in iminashi's stack, which does decode and BS.1770
loudness in managed code and accepts only wav/ogg/flac. FFmpeg's real job is narrow —
normalising mp3/m4a input to WAV and reading tags.

---

## 2. What the spikes established

Run in this environment on .NET 10.0.401, headless Linux x86_64:

- **AlphaTab parses headlessly.** An `AssemblyLoad` trace across a full parse of
  `full-song.gp5` loaded only `AlphaTab` plus BCL assemblies. `AlphaSkia` and
  `System.Drawing.Common` are declared dependencies but are touched only on rendering
  paths. GP3, GP5, GPX (GP6) and GP (GP7/8) all parsed; bends, slide types, harmonic
  types, vibrato, ties and dynamics all extracted correctly.
- **The library stack builds and passes its own tests.** After one mandatory package
  swap (§3), `Rocksmith2014.DLCProject` and its whole dependency chain build clean, and
  **353 tests pass**: PSARC 9, SNG 31, DD 33, Conversion 74, XML 45, XML.Processing 161.
  DD passing means the ML.NET model loads and runs here; Conversion passing means
  XML-to-SNG works.

Test corpus available: 94 real Guitar Pro files from AlphaTab's `test-data`, including
technique-specific fixtures (`bends.gp5`, `slides.gp5`, `harmonic-types.gp5`,
`effects.gp5`) and a full 11-track song in GP5, GPX and GP7.

---

## 3. Mandatory dependency changes

`Magick.NET-Q8-x64` must become **`Magick.NET-Q8-AnyCPU` 14.17.1**. Two reasons, one fix:

- The `-x64` package has no `osx-arm64` native, so Apple Silicon would need Rosetta. This
  is why DLC Builder itself publishes `osx-x64` only. The brief makes native Apple Silicon
  a hard requirement, so this is not optional. `-AnyCPU` 14.17.1 ships `osx-arm64`.
- `-x64` 14.10.4 carries 81 NuGet audit advisories which `TreatWarningsAsErrors=true`
  escalates into build errors. The swap clears all of them.

Verified: the two-line change builds clean end to end.

**Resolved in step 1:** `<Platforms>x64</Platforms>` does **not** conflict with
`-r osx-arm64`. Tested on `Rocksmith2014.SNG` and `Rocksmith2014.Audio`; both build clean
for arm64. No override needed. The Magick swap is delivered by an own project file at
`src/Vendor/Rocksmith2014.DLCProject/` that compiles the submodule's sources unmodified
(rationale in `CLAUDE.md`, "Build layout").

---

## 4. Audio sync approach

### The problem, quantified

A GP tempo map is musical positions and usually integer BPM. Take the nominal 95 BPM in
the test file against a true 94.3 BPM recording: over a four-minute song (~377 beats) the
tab places the last beat at 238.1 s and the recording puts it at 239.9 s. **~1.8 seconds
of accumulated drift.** Rocksmith note timing needs tens of milliseconds. So a start
offset alone is useless; tempo correction is mandatory.

### Options considered

| Approach | Fixes | Cost | Verdict |
|---|---|---|---|
| Offset only | nothing but click-track recordings | trivial | insufficient |
| Offset + global tempo scale | rounded-BPM error | small | **minimum viable** |
| Anchor points, piecewise-linear | human tempo drift too | moderate | **best for M1** |
| Waveform view | visual confidence | large | scope creep, agreed |
| Onset detection / DTW | automatic | large + out of scope | excluded by the brief |

### Chosen design

**One model, two views.** The model is a list of anchors; the simple controls are a
two-parameter projection of it, so there is no rework when anchors arrive.

```
SyncMap = { Anchors : (tabTick * audioMs) list }   // ordered, >= 1
```

- Mapping `tabTick -> audioMs` is piecewise-linear between anchors, extrapolated with the
  adjacent segment's slope outside them (or the nominal tempo map when there is one anchor).
- Two anchors is exactly offset + tempo scale. The UI exposes **Start offset (ms)** and
  **Tempo scale** as primary fields, and an optional expander with an anchor table for
  three or more points.
- Sync is applied as a pure function at XML emission time, to both `Ebeats` and note times.
  That keeps it headlessly testable and keeps the musical domain (ticks) separate from the
  audio domain (ms).
- **Linear tempo ramps: match AlphaTab, warn the user.** Correction to an earlier draft of
  this plan: AlphaTab's own MIDI generator steps every tempo automation at its position and
  ignores `IsLinear` on the tempo path. We do the same, so our timing agrees with AlphaTab's
  `BeatTickLookup`, and we flag bars carrying ramps in the sync section as places where
  anchors are strongly recommended. Pretending linear integration makes a tab "right"
  against a real recording would be the dishonest option.

### Honesty requirements

The brief says never present an unsynced chart as correct. Concretely:

- A persistent status chip with three states: **Not synced** (defaults untouched),
  **Roughly synced** (2 anchors), **Anchored** (3+). It is never green on defaults.
- An always-visible readout: tab length under current sync, audio length, and the signed
  mismatch in seconds.
- An inline warning when the mismatch exceeds 1 s, or whenever status is "Not synced".
- The post-build report states the sync status and estimated drift alongside the output paths.

**Not yet investigated:** AlphaTab 1.8 has native `MasterBar.SyncPoints`,
`AutomationType.SyncPoint` and `Score.BackingTrack`. If a GP file already carries sync
points, we may be able to seed anchors from them instead of starting at zero. Worth a
short spike during step 4; not a dependency.

---

## 5. Phrase and section generation

**Do not write a heuristic. The library already has one**, and writing a second would
diverge from the DD generator's expectations.

`Rocksmith2014.XML.Processing.PhraseGenerator.generate` does:

- a `COUNT` phrase on the first beat before content starts, inserting a beat if there is no room
- a new phrase roughly every **9 measures** (`measureCounter >= 9`)
- each candidate time snapped away from note sustains, `linkNext` chains and hand shapes,
  searching both earlier and later and taking whichever is closer to the beat
- a minimum **2000 ms** between phrases
- `noguitar` sections wherever the gap to the next content is at least **2500 ms**
- an `END` phrase on the beat after the last content
- an anchor inserted at a phrase boundary when the active anchor does not already sit there

It is invoked automatically by `PackageBuilder.setupInstrumental` when
`ForcePhraseCreation` is set or `PhraseIterations.Count <= 3 && Sections.Count <= 3`. Our
emitted XML will have zero phrases, so it fires. We will set `ForcePhraseCreation = true`
to be explicit rather than relying on the threshold.

**Therefore StringSmith's actual obligation is the `Ebeats` list**, because that is the
generator's only structural input: `Ebeat(timeMs, measure)` where `measure` is the bar
number on a downbeat and **-1 on every weak beat**. Get the beat map wrong and phrases,
sections, DD and the in-game beat grid are all wrong together.

On "all occurrences of the same phrase must receive the same level count": that is
`PhraseCombiner.combineSamePhrases`, driven by `GeneratorConfig.PhraseSearchThreshold`.
PhraseGenerator itself names phrases `p0, p1, p2...` without detecting repeats; repeat
detection is DD's job. DLC Builder's defaults are **threshold 80** and
`LevelCountGeneration.Simple`. We adopt both, and expose the level-count mode so `MLModel`
can be selected.

---

## 6. Fingering and tab quality

- **Fingering is not fabricated.** `ChordTemplate.Fingers` defaults to `{-1,...}` and
  `Note.LeftHand` to `-1`; AlphaTab reports `Fingers.Unknown` on real community files. We
  pass the absence through and say so in the UI.
- **Tuning mismatch** between the chosen track and the metadata tuning is an inline warning.
- **Length mismatch** between tab and audio is the sync readout in §4.
- **Wrong-instrument detection:** `Track.PlaybackInfo.Program` (General MIDI) catches a
  vocal line charted on a guitar staff — the test file's "Anette voice" track is program 73,
  flute. We surface the MIDI program in the track list and warn when it is implausible for
  the assigned arrangement role.
- **`ArrangementChecker` gives 44 coded issue types** (`I01`..) covering wrong chord
  fingering, bend problems, linkNext errors, harmonics, bass string range, frets over 24,
  notes after song end, missing END phrase. These feed the inline-warning UI directly
  rather than us inventing checks.

---

## 7. Window and view structure

### Sectioned form, not a step flow

A wizard matches the dependency order, but loses on three of the brief's own requirements:

- *"failures ... leave any already-completed work recoverable rather than discarding the
  session"* — a wizard that has to be re-walked is the opposite
- *"warnings surface inline next to the field they concern"* — needs fields to stay visible,
  not buried behind a past step
- the real workflow is **iterative**: build, find the sync off, tweak, rebuild. A wizard taxes
  every iteration.

DLC Builder reaches the same conclusion: single window, sectioned.

**Chosen: one window, vertically stacked collapsible sections with progressive disclosure.**
Sections past the current frontier stay visible but disabled and dimmed until their
prerequisite is satisfied, so the user sees the whole shape of the task without being able to
act out of order. A persistent bottom bar carries dependency status, the Build button,
progress and the status pane.

### Sections

1. **Dependencies** — collapsed when healthy. FFmpeg and Wwise probe results from launch.
   Expands itself and blocks Build when something is missing, naming what and where it looked.
2. **Source files** — GP file and audio file pickers, both drag-and-drop. Shows detected
   format, track count, tab duration, audio duration and codec.
3. **Track mapping** — one row per GP track: name, tuning, string count, capo, MIDI program,
   note count. A combo per row for Lead / Rhythm / Bass / Vocals / None. Inline warnings for
   tuning mismatch and implausible instrument.
4. **Metadata** — title, artist, album, year, tuning, tuning frequency (default 440.0), album
   art, tone selection. Prefilled from the GP file and the audio tags, every field editable,
   nothing silently trusted. Prefill provenance shown per field.
5. **Audio sync** — §4: offset, tempo scale, status chip, length readout, optional anchor table.
6. **Output** — PC / Mac / both, output folder picker, DD level-count mode.
7. **Build** — button, staged progress, status pane, result paths with "Reveal in Finder"
   (`open -R <path>`).

### Threading

FuncUI + Elmish. Every long stage runs as an `Async` off the UI thread, reporting through
`IProgress<float>` — which `PackageBuilder.BuildConfig` already accepts — dispatched back as
Elmish messages. The window never blocks. The pipeline layer takes no Avalonia reference at all.

---

## 8. Module layout

```
StringSmith/
├─ StringSmith.sln
├─ CLAUDE.md
├─ Directory.Build.props            # TFM, warnings, arm64/x64 overrides
├─ Directory.Packages.props         # central versions incl. Magick AnyCPU swap
├─ external/
│  └─ Rocksmith2014.NET/            # git submodule, pinned to b87c9a3
├─ src/
│  ├─ StringSmith.Core/             # neutral domain types. NO AlphaTab, NO Avalonia
│  ├─ StringSmith.GuitarPro/        # AlphaTab  ->  Core.TabScore
│  ├─ StringSmith.Sync/             # SyncMap, tick->ms, tempo-ramp integration
│  ├─ StringSmith.Conversion/       # Core.TabScore -> InstrumentalArrangement
│  ├─ StringSmith.Audio/            # FFmpeg probe/normalise/tags; wraps RS Audio + Wwise
│  ├─ StringSmith.Pipeline/         # stage orchestration, DLCProject assembly, progress
│  └─ StringSmith.App/              # Avalonia + FuncUI. The ONLY project with UI deps
└─ tests/
   ├─ StringSmith.GuitarPro.Tests/
   ├─ StringSmith.Sync.Tests/
   ├─ StringSmith.Conversion.Tests/
   └─ StringSmith.Pipeline.Tests/
```

### Why `StringSmith.Core` carries a neutral tab model

`Core.TabScore` is deliberately not an AlphaTab type. It is the documented Python-sidecar
seam **and** it makes conversion testable with hand-built fixtures instead of binary GP files.

```
TabScore = { Title; Artist; Album; Year; TicksPerQuarter;
             TempoMap: TempoEvent[]; Bars: BarInfo[]; Tracks: TabTrack[] }
TabTrack = { Index; Name; MidiProgram; IsPercussion; Capo;
             TuningMidi: int[];            // high string first, as AlphaTab gives it
             Notes: TabNote[] }
TabNote  = { TickStart; TickDuration; String; Fret;
             Techniques: TechniqueFlags; BendPoints: BendPoint[];
             SlideToFret: int option; UnpitchedSlideToFret: int option; ... }
```

Positions are **ticks**, not milliseconds. `StringSmith.Sync` owns the only tick-to-ms
conversion, so the musical and audio domains never mix. A future sidecar implements the same
producer contract; nothing downstream of `Core` may reference AlphaTab.

### Dependency direction

```
App -> Pipeline -> Conversion -> Sync -> Core
                -> Audio                 ^
                -> GuitarPro ------------ ┘
Pipeline, Conversion, Audio -> external/Rocksmith2014.NET (XML, XML.Processing, DD,
                                Conversion, SNG, PSARC, DLCProject, Audio, Common)
```

### Unit conversions owned by `Conversion`

- ticks to ms via `Sync`; AlphaTab resolution is **960 per quarter**
- tuning: AlphaTab MIDI notes high-string-first **reversed** then minus `[40,45,50,55,59,64]`
  to get Rocksmith's low-first semitone offsets
- bends: `BendValue.Step = BendPoint.value / 2.0` (AlphaTab value is quarter-tones, max 12 =
  6 semitones); `BendValue.Time = noteStartMs + (offset / 60) * durationMs`
- `Note.TrillFret` is a sentinel when `IsTrill` is false — always gate on the flag

---

## 9. Order of work

Pipeline first, headlessly tested, then UI on top.

| Step | Deliverable | Testable here? |
|---|---|---|
| 1 | Scaffold: solution, submodule pinned, props, Magick swap, `osx-arm64` `<Platforms>` risk resolved | yes, except arm64 |
| 2 | `Core` domain types | yes |
| 3 | `GuitarPro` adapter + tests over the 94-file corpus | yes |
| 4 | `Sync`: SyncMap, tempo-ramp integration, property tests on monotonicity and anchor exactness | yes |
| 5 | `Conversion`: TabScore to `InstrumentalArrangement`, incl. Ebeats with measure markers; XML save/load round-trip tests | yes |
| 6 | `Pipeline`: DLCProject assembly, ForcePhraseCreation + DD, `PackageBuilder.buildPackages`; PSARC unpack + TOC decrypt + SNG decrypt round-trip test; both `_p` and `_m` path/key assertions | yes |
| 7 | `Audio`: FFmpeg probe and normalise, tag read, Wwise detection widened past 2023, launch preflight | partly — no Wwise here |
| 8 | `App`: Avalonia shell, seven sections, async build, progress, inline warnings | builds here, not runnable |
| 9 | `.app` bundle packaging for osx-x64 and osx-arm64 | **no — needs your Mac** |
| 10 | Differential diff against DLC Builder output | **no — needs your Mac + Wwise** |
| 11 | In-game load and DD check | **no — manual gate, you** |

Acceptance per the brief: round-trip (PSARC unpacks, TOC decrypts, SNG decrypts back to the
notes we wrote) is step 6 and fully automatable here. Structural parity against a known-good
DLC Builder package is step 10 and needs macOS. **I will flag clearly when I reach step 9.**

---

## 10. Out of scope, confirmed

No transcription of any kind, no stem separation, no audio-only mode, no Ultimate Guitar or
any network tab fetch, no catalog integration, no code signing or notarization (unsigned
local `.app` is fine), no project save/load, no batch processing, no tab or waveform editor.

---

## 11. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| ~~`<Platforms>x64</Platforms>` blocks `osx-arm64`~~ | resolved | tested in step 1: it does not. Both SNG and Audio build for arm64 |
| Wwise untestable here; version regex stops at 2023 | high — no WEM means no package | widen the regex; preflight at launch; surface the exact failure |
| Beat map wrong, so phrases + DD + grid all wrong | high | Ebeats is step 5's primary test target, with measure markers asserted |
| Linear tempo ramps | medium | step them exactly as AlphaTab does; warn inline on the bars that carry them; anchors fix the rest |
| Submodule pinned to a moving upstream | medium | pin the commit; record it in `CLAUDE.md`; the Magick swap is a local patch to track |
| Community tab quality | medium | `ArrangementChecker` issues surfaced inline; never claim correctness |
| macOS-only behaviour unverifiable here | medium | marked as inference throughout; you own steps 9-11 |
