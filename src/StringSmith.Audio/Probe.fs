namespace StringSmith.Audio

open System
open System.IO
open System.Text.RegularExpressions

/// Where a tool was found, or every place we looked.
type ToolStatus =
    | Found of path: string * version: string
    | Missing of searched: string list

type Dependencies =
    { FFmpeg: ToolStatus
      FFprobe: ToolStatus
      Wwise: ToolStatus }

    member this.AllPresent =
        match this.FFmpeg, this.FFprobe, this.Wwise with
        | Found _, Found _, Found _ -> true
        | _ -> false

/// Launch-time dependency detection. Runs once at startup; failures are reported with
/// what was looked for and where, never discovered mid-build.
module Probe =

    /// Homebrew (Apple Silicon, Intel) and MacPorts, ahead of whatever PATH says, because a
    /// GUI app launched from Finder does not inherit a shell's PATH.
    let private macToolDirs = [ "/opt/homebrew/bin"; "/usr/local/bin"; "/opt/local/bin" ]

    let private pathDirs (env: Env) =
        match env.EnvVar "PATH" with
        | Some p -> p.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        | None -> []

    /// Candidate full paths for an executable name, in search order, de-duplicated.
    let candidates (env: Env) (name: string) =
        (if env.IsMacOS then macToolDirs else []) @ pathDirs env
        |> List.map (fun d -> Path.Combine(d, name))
        |> List.distinct

    /// First line of `<tool> -version`, e.g. "ffmpeg version 7.1 Copyright ..." -> "7.1".
    let parseFfmpegVersion (output: string) =
        let m = Regex.Match(output, @"^\s*ff\w+ version (\S+)", RegexOptions.Multiline)
        if m.Success then m.Groups.[1].Value else output.Split('\n').[0].Trim()

    let private findTool (env: Env) (name: string) : ToolStatus =
        let cands = candidates env name
        match cands |> List.tryFind env.FileExists with
        | None -> Missing cands
        | Some path ->
            match env.Run path [ "-version" ] with
            | Ok out -> Found(path, parseFfmpegVersion out)
            | Error _ -> Found(path, "unknown version")

    let findFFmpeg env = findTool env "ffmpeg"
    let findFFprobe env = findTool env "ffprobe"

    /// Audiokinetic installs to /Applications/Audiokinetic/Wwise<version>/. The console is
    /// a shell script inside the app bundle. This mirrors Rocksmith2014.Audio.WwiseFinder
    /// but accepts any 20xx, where the library's regex stops at 2023. WWISEROOT is honoured
    /// on every platform as an override; the library only reads it on Windows.
    let private wwiseYear = Regex(@"20\d\d")

    let private leaf (path: string) : string =
        match Path.GetFileName path with
        | null -> ""
        | n -> n

    let private wwiseConsoleIn (root: string) =
        Path.Combine(root, "Wwise.app", "Contents", "Tools", "WwiseConsole.sh")

    let findWwise (env: Env) : ToolStatus =
        let audiokinetic = "/Applications/Audiokinetic"
        let fromEnv =
            env.EnvVar "WWISEROOT" |> Option.map (fun r -> [ r ]) |> Option.defaultValue []
        let fromApps =
            env.SubDirectories audiokinetic
            |> List.filter (fun d -> wwiseYear.IsMatch(leaf d))
            |> List.sortDescending // newest first
        let roots = fromEnv @ fromApps
        let searched = (audiokinetic :: roots) |> List.distinct
        roots
        |> List.map (fun r -> r, wwiseConsoleIn r)
        |> List.tryFind (fun (_, cli) -> env.FileExists cli)
        |> function
            | Some(root, cli) -> Found(cli, leaf root)
            | None -> Missing searched

    let all (env: Env) : Dependencies =
        { FFmpeg = findFFmpeg env
          FFprobe = findFFprobe env
          Wwise = findWwise env }

    /// Plain-language summary for the UI, one line per missing tool.
    let describeMissing (deps: Dependencies) : string list =
        let line name what = function
            | Found _ -> None
            | Missing searched ->
                let where = String.Join(", ", searched)
                Some $"{name} was not found. It is needed to {what}. Looked in: {where}"
        [ line "FFmpeg" "convert mp3/m4a audio to WAV" deps.FFmpeg
          line "ffprobe" "read the audio file's tags and length" deps.FFprobe
          line "Wwise" "encode audio to Rocksmith's WEM format (install Wwise 2019-2024 from Audiokinetic)" deps.Wwise ]
        |> List.choose id
