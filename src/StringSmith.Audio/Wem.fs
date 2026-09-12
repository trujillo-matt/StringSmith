namespace StringSmith.Audio

/// WEM encoding through the Wwise Authoring console, via Rocksmith2014.Audio.Wwise.
///
/// What the library does (read from Wwise.fs, not assumed): copies the WAV into an
/// extracted version-specific Wwise project template, runs
///   WwiseConsole.sh generate-soundbank "<tmp>/Template.wproj" --platform "Windows"
///                  --language "English(US)" --no-decode --quiet
/// collects <tmp>/.cache/Windows/SFX/*.wem, and patches byte 40 to uint32 3. The output
/// lands next to the input with a .wem extension. "Windows" is used for Mac packages too;
/// one WEM serves both platforms.
///
/// We pass the console path explicitly (from Probe.findWwise) so detection is ours and
/// accepts Wwise 2024+, which the library's own finder does not.
module Wem =

    /// Encodes `sourcePath` (wav, ogg or flac) to `<sourcePath without ext>.wem`.
    /// Long-running; the caller owns the thread and progress reporting.
    let encode (wwiseConsolePath: string) (sourcePath: string) : Async<unit> =
        async {
            let! _ = Rocksmith2014.Audio.Wwise.convertToWem (Some wwiseConsolePath) sourcePath
            return ()
        }

    /// Where the library writes the result.
    let outputPathFor (sourcePath: string) = System.IO.Path.ChangeExtension(sourcePath, "wem")
