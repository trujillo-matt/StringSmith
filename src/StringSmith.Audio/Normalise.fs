namespace StringSmith.Audio

open System.IO

/// Gets the user's audio into a form the rest of the pipeline accepts.
module Normalise =

    /// Rocksmith2014.Audio.AudioReader handles exactly these; Wwise takes WAV and the
    /// library converts ogg/flac to WAV itself. Anything else goes through FFmpeg first.
    let nativelySupported (path: string) =
        match Path.GetExtension path with
        | null -> false
        | ext ->
            match ext.ToLowerInvariant() with
            | ".wav" | ".ogg" | ".flac" -> true
            | _ -> false

    /// FFmpeg arguments for a 16-bit stereo PCM WAV at the source sample rate. Kept as a
    /// function so the exact invocation is asserted in a test.
    let ffmpegArgs (input: string) (output: string) =
        [ "-y"; "-v"; "error"; "-i"; input; "-vn"; "-acodec"; "pcm_s16le"; "-ac"; "2"; output ]

    /// Converts to WAV if needed. Returns the path to hand downstream.
    let toWav (env: Env) (ffmpegPath: string option) (input: string) (workDir: string) : Result<string, string> =
        if nativelySupported input then
            Ok input
        else
            match ffmpegPath with
            | None ->
                Error $"{Path.GetFileName input} is not WAV/OGG/FLAC and FFmpeg is not available to convert it."
            | Some ffmpeg ->
                let stem =
                    match Path.GetFileNameWithoutExtension input with
                    | null | "" -> "audio"
                    | s -> s
                let output = Path.Combine(workDir, stem + ".wav")
                env.Run ffmpeg (ffmpegArgs input output)
                |> Result.bind (fun _ ->
                    if env.FileExists output then Ok output
                    else Error $"FFmpeg reported success but {output} was not written.")
