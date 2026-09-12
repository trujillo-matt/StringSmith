# StringSmith

Native macOS app that builds playable Rocksmith 2014 CDLC packages, with Dynamic
Difficulty, from a Guitar Pro tab and your own audio file. No Wine, no Windows binaries.

**Status: Milestone 1 vertical slice.** The pipeline is implemented and tested headlessly
end to end (Guitar Pro in, `_p` and `_m` PSARCs out, round-tripped and verified). The
desktop app builds and its logic is tested. What has **not** been done yet is running the
app on a Mac and loading a package in the game. See "What still needs a Mac".

## What you need

- macOS 12 or later, Intel or Apple Silicon. Both are built natively.
- **Wwise Authoring** from Audiokinetic, installed to `/Applications/Audiokinetic/`
  (2019 through 2024 are detected). Required: it is the only way to encode Rocksmith's
  WEM audio. Free with an Audiokinetic account.
- **FFmpeg** (`brew install ffmpeg`), only if your audio is MP3/M4A/AAC. WAV, OGG and
  FLAC need nothing.
- A Guitar Pro file (`.gp3` `.gp4` `.gp5` `.gpx` `.gp`), an audio file, and album art (PNG/JPEG).

## Using it

1. Drop the tab and the audio anywhere in the window, or use the Choose buttons.
2. In **Track mapping**, assign tracks to Lead / Rhythm / Bass. A suggestion is prefilled
   from each track's instrument; check it. Warnings appear under any row that looks wrong
   (a vocal line on a guitar staff, a 6-string track mapped to Bass, and so on).
3. Confirm **Metadata**. Values are prefilled from the tab and the audio tags; the label
   beside each field says where it came from. Choose album art.
4. **Audio sync.** This matters more than anything else. A tab's tempo map is beats and
   whole-number BPM; against a real recording it drifts, typically by seconds over a song.
   The status chip stays red until you set a start offset and tempo scale (or anchors).
   The readout shows where the tab ends versus where the audio ends. Get that mismatch
   near zero. The app never reports an unsynced chart as correct.
5. Pick platforms and an output folder, press **Build**. Progress is shown per stage. The
   result lists each package with Reveal in Finder, per-arrangement stats, and any issues
   the arrangement checker found.
6. Copy the `.psarc` into Rocksmith's `dlc` folder and test it in the game.

## Building from source

Requires the .NET 10 SDK (`global.json` pins 10.0.x) and `git submodule update --init`.

```sh
dotnet build StringSmith.slnx -c Release

# Tests are Expecto executables, not `dotnet test`; exit code 0 is green.
# Each suite is independent, so run them separately if one gives trouble.
# Pipeline is last here because it is the only slow one (it builds real PSARCs).
for s in App Audio Conversion GuitarPro Sync Pipeline; do
  dotnet "tests/StringSmith.$s.Tests/bin/Release/net10.0/StringSmith.$s.Tests.dll" --colours 0 --summary
done

# unsigned .app bundle
scripts/publish-mac.sh arm64    # or x64
```

First launch of the unsigned bundle: right-click, Open. Code signing and notarization are
out of scope for this milestone. A bundle you built yourself carries no quarantine flag and
opens without the prompt; the right-click path only matters for one you downloaded or
copied from another machine.

## How it works, briefly

| Layer | Does |
|---|---|
| `StringSmith.GuitarPro` | AlphaTab parses the file; AlphaTab's own MIDI generator unrolls repeats into one timeline |
| `StringSmith.Sync` | tick to audio time: nominal tempo map, then an anchor warp (offset + scale is the two-anchor case) |
| `StringSmith.Conversion` | notes, chords, techniques, beat map, tuning into Rocksmith arrangement XML |
| `StringSmith.Pipeline` | loudness, preview, WEM via Wwise, then one call into iminashi's `PackageBuilder` for phrases, DD, SNG, manifests and PSARC |
| `StringSmith.App` | Avalonia + FuncUI, Elmish |

Phrases, sections and Dynamic Difficulty come from `Rocksmith2014.NET`'s own generators,
not from code here. `CLAUDE.md` records every constant and behaviour that was verified
against source, including two corrections to the original brief.

## What still needs a Mac

Most development happened on headless Linux, but the app has now had a first run on an
Apple Silicon Mac. Confirmed there: the window renders, dependency detection finds Homebrew
tools and correctly reports Wwise missing, native file dialogs and drag-and-drop work, and
the App, Audio, Conversion and GuitarPro suites pass on arm64. That run also turned up two
real bugs, both since fixed — see "First run on real macOS" in `CLAUDE.md`.

**Still not verified:** a full build with a real Wwise install (so WEM encoding end to end),
the Gatekeeper right-click-Open path for a transferred bundle, the differential diff against
DLC Builder, and the one that matters most — the package loading and playing in Rocksmith
2014 with DD working. That last step is a manual gate and cannot be automated.

## Licences

StringSmith is MIT. `external/Rocksmith2014.NET` is MIT (iminashi). AlphaTab is MPL-2.0,
consumed as an unmodified NuGet package. Test fixtures under `tests/fixtures/alphatab` are
unmodified alphaTab test files (MPL-2.0, see the NOTICE there). Issue wording in the app
is from DLC Builder's English strings (MIT, attributed in `IssueText.fs`).
