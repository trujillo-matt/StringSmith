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
# tests are Expecto executables; exit code 0 is green
for t in tests/*/bin/Release/net10.0/*.Tests.dll; do dotnet "$t" --colours 0 --summary; done
# unsigned .app bundle
scripts/publish-mac.sh arm64    # or x64
```

First launch of the unsigned bundle: right-click, Open. Code signing and notarization are
out of scope for this milestone.

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

Development and testing so far happened on headless Linux. Verified there: every pipeline
stage, the full PSARC round trip on both platforms, the app's view and update logic, and
the `osx-arm64` bundle's structure and native libraries. **Not yet verified:** the app
window on screen, native dialogs and drag-and-drop in practice, Wwise encoding with a real
install, and, the one that matters, the package loading and playing in Rocksmith 2014 with
DD working. That last step is a manual gate and cannot be automated.

## Licences

StringSmith is MIT. `external/Rocksmith2014.NET` is MIT (iminashi). AlphaTab is MPL-2.0,
consumed as an unmodified NuGet package. Test fixtures under `tests/fixtures/alphatab` are
unmodified alphaTab test files (MPL-2.0, see the NOTICE there). Issue wording in the app
is from DLC Builder's English strings (MIT, attributed in `IssueText.fs`).
