# StringSmith

Native macOS desktop app that builds playable Rocksmith 2014 CDLC packages, with
Dynamic Difficulty, from a Guitar Pro tab plus a user-supplied audio file.

**Milestone 1 scope:** a working vertical slice with a usable GUI. One GP file, one
audio file, track-to-arrangement mapping, metadata confirmation, PC/Mac/both output,
Build button, playable package with DD.

---

## Stack (decided, do not re-litigate)

| Layer | Choice | Licence |
|---|---|---|
| Runtime | .NET, native macOS (osx-x64 + osx-arm64) | |
| UI | Avalonia 11 + Avalonia.FuncUI (Elmish) | MIT |
| Rocksmith core | `iminashi/Rocksmith2014.NET` as a git submodule | MIT |
| Guitar Pro parsing | AlphaTab 1.8.4 (NuGet, .NET target, headless) | MPL-2.0 |
| Audio decode/LUFS | `Rocksmith2014.Audio` (NAudio / NVorbis / BunLabs.NAudio.Flac) | MIT |
| Audio input normalise | FFmpeg, **only** for mp3/m4a to WAV (see below) | |
| WEM encode | Wwise Authoring console, native macOS | proprietary, user-installed |
| Images | `Magick.NET-Q8-AnyCPU` 14.17.1 (**not** `-x64`) | Apache-2.0 |

**Target framework is `net10.0`, not `net8.0`.** The current HEAD of
Rocksmith2014.NET (`b87c9a3`, 2026-03-13) targets `net10.0` across all F# projects;
`Rocksmith2014.XML` is `netstandard2.1` and `Rocksmith2014.DD.Model` is
`netstandard2.0`. The brief said ".NET 8+", which this satisfies, but the SDK
requirement is .NET 10.

### Licence boundaries

- **slopsmith** (byrongamatos) is AGPL-3.0. Readable as a reference for the GP-to-RS
  conversion approach. **Do not copy code.** Cite it in comments where its approach
  informed a design decision.
- **AlphaTab** is MPL-2.0 (file-level copyleft). Consume it as an unmodified NuGet
  package. If any AlphaTab file ever has to be modified, keep it in a separate
  directory so the obligation stays contained.
- The app never fetches tabs from any service. No network tab-fetch path, ever.
  Ultimate Guitar has no public API and an anti-scraping ToS.

---

## Verified constants

Everything in this section was read out of source in this repository's pinned
submodule, not recalled. File references are to `external/Rocksmith2014.NET`.

### PSARC container — `src/Rocksmith2014.PSARC/Header.fs`, `Entry.fs`

- magic `PSAR`; `VersionMajor = 1us`, `VersionMinor = 4us` (two uint16, reads as
  `0x00010004` = 65540); compression `zlib`
- header length 32 bytes; `ToCEntrySize = 30u`; `BlockSizeAlloc = 65536u`
- `ArchiveFlags = 4u` means encrypted (`Header.IsEncrypted`)
- TOC entry, 30 bytes: 16-byte MD5 of the name, uint32 z-block index,
  **uint40** uncompressed length, **uint40** offset

### Crypto keys — decoded from F# byte-string literals

```
psarcKey  C53DB23870A1A2F71CAE64061FDD0E1157309DC85204D4C5BFDF25090DF2572C
sngKeyPC  CB648DF3D12A16BF71701414E69619EC171CCA5D2A142E3E59DE7ADDA18A3A30
sngKeyMac 9821330E34B91F70D0A48CBD625993126970CEA09192C0E6CDA676CC9838289D
```

All three match the brief exactly.

### Two constants in the brief are WRONG

1. **There is no `ARC_IV`.** PSARC TOC encryption is AES-256-**CFB** (128-bit
   block, 128-bit feedback, `PaddingMode.Zeros`) with a **16-byte zero IV** in both
   directions — `Cryptography.fs:26` (`aes.CreateEncryptor(psarcKey, Array.zeroCreate<byte> 16)`)
   and `:37` (`aes.DecryptCfb(buffer, Array.zeroCreate<byte> 16, ...)`).
   The value `E915AA018FEF71FC508132E4BB4CEB42` appears **nowhere** in the repository.
   Evidence this is right and not merely self-consistent: DLC Builder reads official
   retail PSARCs in production, and a wrong IV would corrupt the first 16 bytes of
   every TOC. `Rocksmith2014.PSARC.Tests` passes 9/9 here.
   SNG encryption is a different scheme: AES-256-**CTR** (hand-rolled over ECB,
   with an SSE2 fast path), IV stored in the file itself after the 8-byte header,
   written as 16 zero bytes by `encryptSNG` when the caller passes `None`.
2. **The first PSARC entry is not named `NamesBlock.bin`.** It is the nameless
   manifest entry: `{ Name = String.Empty; Data = createManifestData () }`
   (`PSARC.fs:113`), and `md5Hash` returns 16 zero bytes for empty input
   (`Cryptography.fs`). MD5-ing the literal string `NamesBlock.bin` would yield a
   non-zero digest and break the archive. The entry content is a newline-separated
   list of the remaining filenames.

### Platform divergence — `src/Rocksmith2014.Common/Platform.fs`

Verified exactly as the brief stated:

| | PC | Mac |
|---|---|---|
| SNG path | `songs/bin/generic` | `songs/bin/macos` |
| audio path | `audio/windows` | `audio/mac` |
| package suffix | `_p` | `_m` |
| SNG key | `sngKeyPC` | `sngKeyMac` |

WEM payload is platform-independent and reused. `PackageBuilder.buildPackages`
already builds every requested platform in parallel from one asset set.

### Arrangement XML element and attribute names — `src/Rocksmith2014.XML/`

Read from the `IXmlSerializable` implementations, not assumed.

- `<levels count="N">` / `<level difficulty="N">` (sbyte) with children
  `<notes count>`, `<chords count>`, `<anchors count>`, `<handShapes count>`
- `<phrases count>` / `<phrase maxDifficulty="N" name="..." disparity="0" ignore="0" solo="0"/>`
  — `MaxDifficulty` is `byte`; `disparity`/`ignore`/`solo` are omitted when abridged XML is on
- `<phraseIterations count>` / `<phraseIteration time="" phraseId="" variation="">`
  containing `<heroLevels count="3">` / `<heroLevel hero="1|2|3" difficulty="N"/>`
- `<newLinkedDiffs count>` / `<newLinkedDiff levelBreak="" ratio="" phraseCount="">`
  containing `<nld_phrase id="N"/>`
- `<phraseProperties count>` / `<phraseProperty phraseId redundant levelJump empty difficulty>`
- `<ebeat time="" measure="">` — `measure` is the bar number on a downbeat, **-1 on weak beats**
- `<tuning string0..string5>` — `short[6]`, **low E first**, semitone offsets from standard
- note attributes actually emitted: `time sustain string fret` plus
  `linkNext accent bend bendValue bendValues fret hammerOn harmonic harmonicPinch hopo
  ignore leftHand mute palmMute pickDirection pluck pullOff rightHand slap slideTo
  slideUnpitchTo tap tremolo vibrato`
- `NoteMask` (ushort) flags: `LinkNext Accent HammerOn Harmonic Ignore FretHandMute
  PalmMute PullOff Tremolo PinchHarmonic PickDirection Slap Pluck RightHand`
- `Note.Tap` is an **sbyte**, not a bool. `Note.Vibrato` is a **byte** (frequency).
  `Note.MaxBend` is a float in semitones; `BendValue(time, step)` step is semitones.
- `ChordTemplate.Fingers` and `.Frets` both default to `{-1,-1,-1,-1,-1,-1}` —
  this is the fingering gap, confirmed in source.

### Cover art — `src/Rocksmith2014.DLCProject/DDS.fs`

`targetSizes = [| 64u; 128u; 256u |]`, as the brief stated.

### Volume — `src/Rocksmith2014.Audio/Volume/Volume.fs`

BS.1770 integrated loudness, reference **-16 LUFS**, `Math.Round(-16. - loudness, 1)`.

---

## Answers to the five unverified items

1. **Arrangement XML names** — verified, above.
2. **NuGet vs project reference** — only `Rocksmith2014.XML` (1.1.1, 5 versions) is
   published. `Common`, `SNG`, `PSARC`, `DD`, `DD.Model`, `Conversion`, `DLCProject`,
   `Audio`, `XML.Processing` all return 404. **Submodule + project references** is the
   only option. Confirmed by querying `api.nuget.org/v3-flatcontainer`.
3. **AlphaTab headless** — **works.** Parsed GP3, GP5, GPX (GP6) and GP (GP7/8) on
   headless Linux with no display and no fonts. An `AssemblyLoad` trace across the
   parse shows only `AlphaTab` plus BCL assemblies: **no AlphaSkia, no
   System.Drawing.Common**, even though both are declared package dependencies. They
   are touched only on rendering paths. No fallback needed.
4. **DLC Builder UI framework** — **Avalonia**, confirmed. `Avalonia.Desktop` 11.3.12,
   `Avalonia.FuncUI` 1.5.2, `Avalonia.FuncUI.Elmish` 1.5.2, `Avalonia.Themes.Fluent`.
   Elmish MVU, one `MainWindow.fs`, `Views/` + `Modals/` + `Controls/` split. The
   precedent the Avalonia choice rested on holds.
5. **Wwise on macOS** — `src/Rocksmith2014.Audio/Wwise/`:
   - detection: `/Applications/Audiokinetic/<dir matching regex `20(19|21|22|23)`>/Wwise.app/Contents/Tools/WwiseConsole.sh`.
     No `WWISEROOT` on macOS; that variable is the Windows path only.
   - invocation: `generate-soundbank "<tmpdir>/Template.wproj" --platform "Windows" --language "English(US)" --no-decode --quiet`
   - **the input is WAV, not OGG.** Flow: source audio to WAV, copy to
     `<tmpdir>/Originals/SFX/Audio.wav`, extract a version-specific Wwise project
     template (embedded `wwise2019/2021/2022/2023.zip`), run the console, collect
     `<tmpdir>/.cache/Windows/SFX/*.wem`, then patch the header: seek to byte 40 and
     write `uint32 3`.
   - `--platform "Windows"` is used even for Mac output, which is why one WEM serves both.
   - the version regex stops at 2023, so **Wwise 2024+ is not detected**. Widen it.

---

## Findings that reshaped the plan

1. **`PhraseGenerator.generate` already exists** in
   `src/Rocksmith2014.XML.Processing/PhraseGenerator.fs`. Its heuristic: a `COUNT`
   phrase on the first beat before content, a new phrase roughly every **9 measures**,
   snapped away from note sustains / linkNext chains / hand shapes, minimum **2000 ms**
   separation, `noguitar` sections wherever the gap is at least **2500 ms**, and an
   `END` phrase after the last content. It is invoked automatically by
   `PackageBuilder.setupInstrumental` when `ForcePhraseCreation` is set or when
   `PhraseIterations.Count <= 3 && Sections.Count <= 3`. **Do not write a phrase
   heuristic.** StringSmith's real obligation is emitting a correct `Ebeats` list with
   measure markers, because that list is the generator's only structural input.
2. **`PackageBuilder.buildPackages` collapses brief stages 7, 8 and 9 into one call.**
   It does DDS cover art, manifests (`.json` + `.hsan`), SNG conversion and per-platform
   encryption, XBlock, aggregate graph, soundbank + WEM, showlights auto-generation,
   `toolkit.version`, `appid.appid`, and PSARC packing for every platform in parallel.
   It drives off **XML files on disk** (`Instrumental.XmlPath`). So StringSmith's job is
   narrower than the brief implies: write arrangement XML to a work directory, build a
   `DLCProject` record, supply an `AudioConversionTask: Async<unit>`, and call it.
3. **FFmpeg is used nowhere in iminashi's stack** (grepped: zero hits). `AudioReader.Create`
   accepts **only** `.wav`, `.ogg`, `.flac`. The brief wants mp3/flac/wav/m4a/ogg input, so
   FFmpeg's actual role is narrow: an **input normaliser** for mp3 and m4a, plus tag
   reading. It is not the loudness or OGG-encode path the brief described.
4. **`ArrangementChecker` supplies 44 coded issue types** (`I01`..) covering wrong chord
   fingering, bends, linkNext, harmonics, bass string range, frets over 24, notes after
   song end, missing END phrase, and more. Feed these straight into the inline-warning UI
   rather than inventing checks.
5. **Apple Silicon: `Magick.NET-Q8-x64` is x64-only**, and DLC Builder publishes
   `osx-x64` only (`publish.fsx`), so iminashi's own macOS build runs under Rosetta 2.
   `Magick.NET-Q8-AnyCPU` 14.17.1 ships an `osx-arm64` native. Swapping the package
   reference is a two-line change that builds clean and **also clears all 81 NuGet audit
   advisories** that `TreatWarningsAsErrors=true` otherwise escalates to build errors.
   This swap is mandatory, not cosmetic.
6. **`Sse2.IsSupported` is false on arm64**, so `Rocksmith2014.SNG` falls back to its
   scalar AES-CTR path. Correct, just slower. Not a blocker.
7. **`<Platforms>x64</Platforms>` does NOT block `osx-arm64`.** Tested: `Rocksmith2014.SNG`
   (no RID list) and `Rocksmith2014.Audio` (RID list omitting arm64) both build clean with
   `-r osx-arm64`. `Platforms` only enumerates valid `$(Platform)` configuration values;
   `RuntimeIdentifier` is orthogonal. No override needed.
8. **arm64 ships without `ww2ogg`/`revorb`.** `Rocksmith2014.Audio` gates its bundled tools on
   `'$(RuntimeIdentifier)'=='osx-x64'` exactly, and the `Tools/mac/` binaries are x86_64
   Mach-O anyway. They are used only for WEM **decode** (`Conversion.wemToOgg`,
   `withTempOggFile`), which our encode-only pipeline never calls. Known gap, documented,
   not on the Milestone 1 path. Also: Audio's Debug configuration hard-sets
   `RuntimeIdentifier=osx-x64` on macOS; a global `-r` passed from our build overrides it.

## AlphaTab to Rocksmith unit conversions

- AlphaTab tick resolution is **960 per quarter note** (observed: `dur=960` quarter,
  `480` eighth, `1440` dotted quarter).
- `Staff.Tuning` is MIDI note numbers, **high string first**. Rocksmith `Tuning.Strings`
  is semitone offsets from standard, **low string first**. Reverse, then subtract
  `[40,45,50,55,59,64]`.
- `BendPoint.offset` is 0-60 across the note duration (`MaxPosition = 60`);
  `BendPoint.value` is in **quarter-tones** (`MaxValue = 12` = 6 semitones = 3 whole
  tones). So `BendValue.Step = BendPoint.value / 2.0` and
  `BendValue.Time = noteStartMs + (offset / 60) * noteDurationMs`.
  Source: `packages/alphatab/src/model/BendPoint.ts`.
- `MasterBar.TempoAutomations` carry `RatioPosition` (fraction within the bar) and
  `IsLinear`. **Linear tempo ramps occur in real files** (the Nightwish test file ramps
  across bars 85-94). **AlphaTab's own MIDI generator does NOT integrate them**: it emits
  one stepped tempo change per automation at its `RatioPosition` and never reads `IsLinear`
  on the tempo path (`MidiFileGenerator._generateMasterBar`). We match that stepped
  behaviour so our tick-to-ms agrees with AlphaTab's `BeatTickLookup`, and we surface the
  presence of ramps as a sync warning. Neither stepping nor integrating is "correct"
  against a real recording; sync anchors are what fix drift.
- `MidiUtils.QuarterTime = 960` is the tick resolution, confirmed from source.
- **Repeat unrolling** comes from AlphaTab, not from us: `MidiPlaybackController` is not
  public in the .NET assembly, but `MidiFileGenerator` is, and after `Generate()` its
  `TickLookup.MasterBars` lists one `MasterBarTickLookup` per playback occurrence with
  absolute `Start`/`End` and per-occurrence `TempoChanges`. `Beat.PlaybackStart` is
  bar-relative, so an unrolled beat tick is `occurrence.Start + beat.PlaybackStart`. A
  no-op `IMidiFileHandler` object expression is the only scaffolding needed.
- `Note.TrillFret` is a sentinel (observed `-60`, `-56`, `-41`) when `IsTrill` is false.
  Always gate on `IsTrill`.
- `Note.LeftHandFinger` / `RightHandFinger` are `Fingers.Unknown` on real community
  files, matching the Rocksmith `-1` fingering gap. **Do not fabricate fingerings.**
- **AlphaTab is not thread-safe.** `Score.ResetIds()` is static and importers share state;
  concurrent loads fail non-deterministically with "Collection was modified". The producer
  serialises every parse/generate through one lock. Never call AlphaTab from two threads.
- **AlphaTab does not reject garbage.** Zero, sequential and random byte inputs all load as
  a default empty score (1 track, 1 bar, 0 notes, 120 bpm, 6 strings); only text throws
  `UnsupportedFormatError`. The producer therefore reports `NoPlayableTracks` whenever no
  track has any notes. Garbage and a genuinely empty tab are indistinguishable and are
  reported the same way on purpose.
- `Track.PlaybackInfo.Program` (General MIDI) is a useful signal for suggesting an
  arrangement role, and for catching a vocal line charted on a guitar staff (the test
  file's "Anette voice" track is MIDI program 73, flute).

---

## Build layout: the vendor shim

The submodule's `Directory.Build.props` and `Directory.Packages.props` do **not** chain to
ours (no `GetPathOfFileAbove` import), so they shadow anything at the repo root for every
project under `external/`. We cannot change a package pin in the submodule from outside it.

The only project that needs a different pin is `Rocksmith2014.DLCProject` (sole consumer of
Magick.NET). So `src/Vendor/Rocksmith2014.DLCProject/Rocksmith2014.DLCProject.fsproj` is an
**own project file that compiles the submodule's sources unmodified** via
`<Compile Include="$(RocksmithNetRoot)src/Rocksmith2014.DLCProject/...">`, under our props,
with `Magick.NET-Q8-AnyCPU` pinned. Rules:

- The `Compile` list must stay byte-for-byte in upstream order (F# is order-dependent).
  When bumping the submodule commit, regenerate the list from upstream's `.fsproj` and diff.
- `AssemblyName` and the two `EmbeddedResource` `LogicalName`s are pinned so upstream's
  `EmbeddedFileProvider(...).GetFileInfo("res/rsenumerable_*.flat")` still resolves.
  Verified: the shim's manifest names match the original DLL exactly.
- Every other library is referenced straight from the submodule, unpatched. **Never edit
  files under `external/`.**

Solution file is `StringSmith.slnx` (the .NET 10 default format), with solution folders
`Vendor` and `External`.

## Status: what is built and proven headlessly

Steps 1-7 of `docs/PLAN-milestone-1.md` are implemented and tested on Linux x86_64 with
the .NET 10 SDK. Run everything with `dotnet build StringSmith.slnx -c Release` and then
each `tests/*/bin/Release/net10.0/*.Tests.dll --colours 0 --summary` (Expecto exes; exit
code 0 means green; `dotnet test` is not wired up).

| Project | Tests | What it proves |
|---|---|---|
| Core | (types) | neutral tab model, tick/ms units, unrolled timeline, sidecar seam |
| GuitarPro | 29 | GP3/5/6/7 load, repeat unrolling, stepped tempo, tuning/strings, techniques, sync points, thread safety, garbage handling |
| Sync | 24 | nominal time, anchor warp, drift report, FsCheck monotonicity |
| Conversion | 28 | ebeats with measure markers, notes/chords/templates/handshapes/anchors, ties, slides, bends, tuning, XML round trip |
| Audio | 18 | FFmpeg/Wwise detection incl. Wwise 2024+, ffprobe tag parsing, WAV normalise args |
| App | 33 | every section renders in every model state headlessly; prefill precedence, role suggestion, NotSynced-on-defaults, build gate |
| Pipeline | 9 | **the acceptance round trip**: `full-song.gp5` -> two arrangements -> `_p` and `_m` PSARCs -> TOC read back -> SNG decrypted per platform, hardest level equal note-for-note to the packed XML; per-platform keys proved by ciphertext divergence; failure leaves converted XML on disk |

The round trip's packed XML had 14 DD levels and 8-10 phrases from our single-level input,
so PhraseGenerator and the DD generator accept what Conversion emits. WEM encoding is the
one stage not exercised here (no Wwise); the MIT WEM fixtures from iminashi's integration
tests stand in, referenced in place.

Step 8 (App) builds with zero warnings. Step 9: `scripts/publish-mac.sh arm64` cross-publishes
from Linux and the resulting `StringSmith.app` was inspected: the apphost is Mach-O arm64,
`Magick.Native-Q8-arm64.dll.dylib` is present (the AnyCPU swap delivered it), Avalonia/Skia/
HarfBuzz natives are universal, 280 files, `Info.plist` versioned. Steps 10 and 11 (DLC Builder
differential, in-game test) need a Mac and are the user's.

## First run on real macOS (arm64)

Verified on an Apple Silicon Mac (T6000, 10 cores, 16 GB, macOS 27) by a Cowork session.
This is the first time any of it ran on a Mac. What it settled:

- The bundle launches and the window renders correctly: dark theme, 1120x908, seven
  sections, bottom bar.
- Dependency probing works. FFmpeg and ffprobe were found through the Homebrew path, which
  is the case the search order exists for (a Finder-launched app inherits no shell PATH).
  Wwise was absent and reported with the actionable message.
- Native `NSOpenPanel` dialogs open from the Choose buttons. Drag and drop works for both
  tabs and audio.
- Metadata prefill, role suggestion, the red NOT SYNCED chip on defaults, the drift readout,
  the ramped-bar warning and the itemised build blockers all behaved as designed.
- App, Audio, Conversion and GuitarPro suites passed on arm64 hardware.
- The bundle built locally carries no quarantine flag, so the **right-click > Open Gatekeeper
  path is still unexercised.** It needs a bundle that has been downloaded or transferred.

Two real bugs came out of that run. Both are fixed.

1. **The Year field displayed "440"**, the Tuning frequency value from the row below it.
   Avalonia's `TextBox.Text` setter raises a change notification, and FuncUI's
   `TextBox.onTextChanged` is wired to it, so every render that assigned `Text` dispatched a
   "the user edited this" message; when FuncUI recycled a control into a different slot, that
   message reached the wrong field's handler. Fixed in two places:
   `Controls/GuardedTextBox.fs` suppresses view-originated notifications (the mechanism
   iminashi's `FixedTextBox.fs` uses, for the same reason on this same stack), and
   `Views/Widgets.fs` now gives every field row a constant child count so controls are not
   reassigned between slots at all. Regression-tested headlessly: the control instantiates
   without an Avalonia Application, so the suppression is asserted directly.
2b. **A second macOS run (video, 2026-09-15) showed the Year fix was incomplete.** The
   `GuardedTextBox` half was right but the other half was not: `Widgets.section` still held
   a conditional child, and the guarded control pinned its callback with an always-equal
   comparer, so recycled controls kept stale callbacks. Typing into a field edited the one
   above, and Year displayed a value the model did not hold, so the build stayed blocked.
   Fixed properly and generalised into the rules above. That run also confirmed Wwise is
   now installed and detected.
2. **The Pipeline suite hung for over 20 minutes** at 100% CPU with RSS climbing to 2.1 GB
   and every stack sample inside GC internals. **The cause was the test, not the pipeline.**
   Measured on Linux: `Build.run` takes 1.7 s end to end, Packaging 0.7 s, Validating 27 ms,
   `ArrangementChecker` 3-7 ms, 96 MB total allocated. The old
   "the Mac SNG does not decrypt with the PC key" test fed wrong-key garbage to SNG's binary
   reader, which does `Array.init (reader.ReadInt32())` (`BinaryHelpers.fs:28`) — a garbage
   length field asks for up to 2^31 elements. On Linux that allocation threw immediately and
   the test passed; on a Mac with ~73 MB of free pages it became an unbounded GC thrash.
   **Never hand a wrong key to a binary parser.** Replaced with a test that proves the same
   property without parsing anything: identical plaintext under two keys must produce
   different ciphertext. Measured: the two SNGs are byte-identical for the first 24 bytes
   (magic `0x4A`, header, and the 16-byte zero IV, which independently re-confirms the
   zero-IV finding above) and then 99% of bytes differ. Both build calls in that suite now
   carry a 10-minute liveness timeout so a wedge fails loudly instead of spinning.

## The FuncUI index-matching rule (read before touching any view)

This has now caused three shipped bugs. **FuncUI matches children by index.** If a children
list changes length between renders, every sibling after the change shifts one slot, and
FuncUI patches the control that was rendering slot N with the view for slot N+1. Two things
then go wrong at once: the recycled control keeps its own change callback, so typing into
one field edits its neighbour; and once the model and the displayed text disagree, FuncUI's
value comparison on the `text` attribute sees no change and never corrects the display.

That is the whole explanation for the reported symptoms: a visible `2025` in Year while the
build stayed blocked on "Year must be a number", and typing into one field editing the one
above. The trigger was a single innocuous line in `Widgets.section`:

```fsharp
if not enabled then dim disabledReason     // WRONG: a conditional child
```

When a section flipped from disabled to enabled, that line vanished and all eight metadata
rows moved up one slot.

**The rules, all enforced by tests:**

1. **Never write a conditional child.** Render it always and toggle `isVisible`.
2. **Any run whose length depends on model state goes inside `Widgets.container`** (a
   `VariableChildren` panel). The run then occupies exactly one slot however long it gets.
   Appending at the end of a list is harmless; inserting or removing in the middle is not.
3. **Never use a raw `TextBox` or `ComboBox` for input.** Use `GuardedTextBox` /
   `GuardedComboBox` from `Controls/`. Avalonia's property setters raise change
   notifications, so a plain handler fires on every render that assigns a value, dispatching
   edits nobody made. These guard that, and re-apply both the value and the callback every
   render so a recycled control is always current. iminashi's `Fixed*` controls pin the
   callback instead with an always-equal comparer, which is safe only while the view
   structure never changes shape; ours does, so we re-apply.
4. **Handlers that can fire from a view-set value must compare against current state**
   before dispatching (the checkboxes do: `if not m.Output.PC then dispatch TogglePC`).

`tests/StringSmith.App.Tests` enforces rules 1 and 2 with a shape test that walks the view
tree and asserts every children count is identical across nine model states, stopping at
`VariableChildren` nodes. It is not a vacuous test: reintroducing the `if not enabled` line
makes `metadata` and `output` fail with exactly the one-slot shift (11 children to 10,
`/3` becoming `/2`). Rule 3 is covered by direct tests on the guarded controls, which
construct headlessly.

Steps 1-7 are covered by `Directory.Build.props` nullness checking; the App has it disabled
because Avalonia's annotations would turn view code into null-matches for no safety gain.
Expected compile-time traps, all hit and fixed during this work: `Path.*` helpers return
`string | null` on .NET 10; `[<AutoOpen>]` module types (`LevelCountGeneration`, `Issue`)
resolve only via `open`, not full qualification, and need a direct project reference from
test projects; F# forbids `\"` inside `$"…"` holes and misparses `{DateTime.Now:HH:mm:ss}`.

## Open questions

- **Audio sync.** A GP tempo map is beats and whole-number BPM, not absolute seconds
  against a specific recording; it will drift. Note that AlphaTab 1.8 has native
  `MasterBar.SyncPoints` / `AutomationType.SyncPoint` / `Score.BackingTrack`, which may
  be reusable rather than rolling our own anchor model. **Not yet investigated.**
- **Wwise 2024/2025.** Whether the `generate-soundbank` invocation and the embedded
  project templates still work on versions past 2023 is untested. The templates are
  version-pinned zips.
- **Differential test baseline.** Building the same source through DLC Builder's macOS
  build, to diff against, requires a macOS machine plus a Wwise install.
- **Not verifiable in this environment.** Development here is headless Linux x86_64:
  the `.app` bundle, Apple Silicon behaviour, Wwise, and the in-game manual gate all
  need real macOS hardware. Claims about them stay labelled as inference until tested there.

## Deferred seam: Python audio sidecar

Audio transcription is out of scope for Milestone 1 and must not be implemented. Keep the
boundary clean so it can be attached later: the conversion layer consumes an interface that
yields timed note events, and the GP parser is one implementation of it. A future Python
sidecar (Basic Pitch / librosa / Demucs) becomes a second implementation behind the same
interface, talking over a process boundary with a serialised note-event contract. Nothing in
the conversion, sync, phrase or packaging layers may depend on AlphaTab types directly.

## Conventions

- Pipeline logic stays free of Avalonia references so it is headlessly testable. Conversion,
  sync, phrase handling and packaging live in separate projects from the views.
- Never present an unsynced or approximately synced chart as correct. Report expected drift.
- Surface tuning mismatches and large tab-vs-audio length mismatches as inline UI warnings
  next to the field concerned, not only in a log.
- Long-running work runs off the UI thread with visible progress. External dependencies
  (FFmpeg, Wwise) are probed at launch, not mid-build.
