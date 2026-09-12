module StringSmith.App.Update

open System
open System.Diagnostics
open System.IO
open Avalonia.Controls
open Elmish
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Audio
open StringSmith.GuitarPro
open StringSmith.Pipeline
open StringSmith.App.Model

let private log (msg: string) (m: Model) =
    let stamp = DateTime.Now.ToString("HH:mm:ss")
    { m with Log = $"{stamp}  {msg}" :: m.Log |> List.truncate 300 }

let init () : Model * Cmd<Msg> =
    initialModel, Cmd.OfAsync.perform (fun () -> async { return Probe.all Env.live }) () DepsChecked

// ---------------------------------------------------------------- commands

let private loadTab (path: string) =
    Cmd.OfAsync.perform
        (fun (p: string) -> async { return (AlphaTabSource() :> ITabSource).Load p })
        path
        (fun r -> TabLoadFinished(path, r))

let private probeAudio (deps: Dependencies option) (path: string) =
    Cmd.OfAsync.perform
        (fun (p: string) ->
            async {
                try
                    let native = Normalise.nativelySupported p
                    let ffprobe =
                        deps |> Option.bind (fun d -> match d.FFprobe with Found(x, _) -> Some x | Missing _ -> None)
                    let tags =
                        match ffprobe with
                        | Some f -> (match Tags.read Env.live f p with Ok t -> t | Error _ -> AudioTags.Empty)
                        | None -> AudioTags.Empty
                    let lengthMs =
                        if native then (Rocksmith2014.Audio.Utils.getLength p).TotalMilliseconds
                        else
                            match tags.DurationMs with
                            | Some d -> d
                            | None -> failwith "Could not read the audio length. For MP3/M4A, FFmpeg (ffprobe) is required."
                    return Ok { Path = p; Tags = tags; LengthMs = lengthMs; NeedsFFmpeg = not native }
                with e ->
                    return Error e.Message
            })
        path
        (fun r -> AudioProbeFinished(path, r))

let private reveal (path: string) =
    try
        let psi = ProcessStartInfo(UseShellExecute = false)
        if OperatingSystem.IsMacOS() then
            psi.FileName <- "open"
            psi.ArgumentList.Add "-R"
            psi.ArgumentList.Add path
        elif OperatingSystem.IsWindows() then
            psi.FileName <- "explorer"
            psi.ArgumentList.Add $"/select,{path}"
        else
            psi.FileName <- "xdg-open"
            psi.ArgumentList.Add(if Directory.Exists path then path else Path.GetDirectoryName path)
        Process.Start psi |> ignore
    with _ -> ()

let private startBuild (m: Model) (req: BuildRequest) (audio: AudioInfo) : Cmd<Msg> =
    let ffmpeg =
        Derive.deps m |> Option.bind (fun d -> match d.FFmpeg with Found(p, _) -> Some p | Missing _ -> None)
    Cmd.ofEffect (fun dispatch ->
        async {
            try
                Directory.CreateDirectory req.WorkDir |> ignore
                // MP3/M4A become WAV in the work dir before the pipeline sees them.
                let audioPath =
                    if audio.NeedsFFmpeg then
                        match Normalise.toWav Env.live ffmpeg req.AudioPath req.WorkDir with
                        | Ok p -> p
                        | Error e -> failwith e
                    else req.AudioPath
                let! result = Build.run (fun p -> dispatch (BuildProgressed p)) { req with AudioPath = audioPath }
                dispatch (BuildFinished result)
            with e ->
                dispatch (BuildFinished(Error { Stage = Preparing; Message = e.Message; WorkDir = req.WorkDir; Completed = [] }))
        }
        |> Async.Start)

// ---------------------------------------------------------------- prefill

/// Tab values fill empty fields only; audio tags override tab values; user edits win.
let private fillFromTab (f: Field) (v: string) =
    if not (String.IsNullOrWhiteSpace v) && (f.Source = Unset || f.Source = FromTab) then { Value = v.Trim(); Source = FromTab } else f

let private fillFromAudio (f: Field) (v: string option) =
    match v with
    | Some x when f.Source <> FromUser && not (String.IsNullOrWhiteSpace x) -> { Value = x.Trim(); Source = FromAudio }
    | _ -> f

/// At most one suggested Lead and one suggested Bass, never more. Everything is visible
/// and editable in the track table.
let private suggestRoles (s: TabScore) : Map<int, StringSmith.Conversion.ArrangementRole> =
    let pick role =
        s.Tracks |> List.tryFind (fun t -> Derive.suggestRole t = Some role) |> Option.map (fun t -> t.Index, role)
    [ pick StringSmith.Conversion.Lead; pick StringSmith.Conversion.Bass ] |> List.choose id |> Map.ofList

let private classifyDrop (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".gp3" | ".gp4" | ".gp5" | ".gpx" | ".gp" -> Some(TabPicked(Some path))
    | ".wav" | ".ogg" | ".flac" | ".mp3" | ".m4a" | ".aac" -> Some(AudioPicked(Some path))
    | ".png" | ".jpg" | ".jpeg" -> Some(AlbumArtPicked(Some path))
    | _ -> None

// ---------------------------------------------------------------- update

let update (window: Window) (msg: Msg) (m: Model) : Model * Cmd<Msg> =
    match msg with
    | Nop -> m, Cmd.none
    | Log s -> log s m, Cmd.none

    | DepsChecked d ->
        let missing = Probe.describeMissing d
        let m = { m with Deps = DepsReady d }
        (if missing.IsEmpty then log "FFmpeg, ffprobe and Wwise found." m else missing |> List.fold (fun acc s -> log s acc) m), Cmd.none
    | RecheckDeps -> { m with Deps = DepsChecking }, Cmd.OfAsync.perform (fun () -> async { return Probe.all Env.live }) () DepsChecked

    | PickTab -> m, Cmd.OfAsync.perform (fun () -> Dialogs.openFile window "Choose a Guitar Pro file" Dialogs.tabPatterns) () TabPicked
    | TabPicked None -> m, Cmd.none
    | TabPicked(Some path) -> { m with Tab = TabLoading path } |> log $"Loading {Path.GetFileName path}", loadTab path
    | TabLoadFinished(path, Ok s) ->
        let meta =
            { m.Meta with
                Title = fillFromTab m.Meta.Title s.Title
                Artist = fillFromTab m.Meta.Artist s.Artist
                Album = fillFromTab m.Meta.Album s.Album }
        { m with Tab = TabLoaded(path, s); Meta = meta; Roles = suggestRoles s; Tones = Map.empty; Sync = initialModel.Sync }
        |> log $"Loaded {s.Tracks.Length} tracks, {s.Bars.Length} bars (unrolled), {s.InitialBpm} bpm.", Cmd.none
    | TabLoadFinished(path, Error e) ->
        let why =
            match e with
            | FileNotFound _ -> "the file was not found"
            | UnsupportedFormat(_, d) -> $"the format is not supported ({d})"
            | CorruptFile(_, d) -> $"the file could not be read ({d})"
            | NoPlayableTracks _ -> "it contains no playable notes (empty tab, or not a Guitar Pro file)"
        { m with Tab = TabFailed(path, e) } |> log $"Could not load {Path.GetFileName path}: {why}.", Cmd.none

    | PickAudio -> m, Cmd.OfAsync.perform (fun () -> Dialogs.openFile window "Choose the audio file" Dialogs.audioPatterns) () AudioPicked
    | AudioPicked None -> m, Cmd.none
    | AudioPicked(Some path) -> { m with Audio = AudioProbing path } |> log $"Reading {Path.GetFileName path}", probeAudio (Derive.deps m) path
    | AudioProbeFinished(_, Ok a) ->
        let meta =
            { m.Meta with
                Title = fillFromAudio m.Meta.Title a.Tags.Title
                Artist = fillFromAudio m.Meta.Artist a.Tags.Artist
                Album = fillFromAudio m.Meta.Album a.Tags.Album
                Year = fillFromAudio m.Meta.Year (a.Tags.Year |> Option.map string) }
        let codec = defaultArg a.Tags.Codec "unknown codec"
        let conv = if a.NeedsFFmpeg then ", will be converted with FFmpeg" else ""
        let len = Derive.fmtTime a.LengthMs
        { m with Audio = AudioReady a; Meta = meta } |> log $"Audio: {len}, {codec}{conv}.", Cmd.none
    | AudioProbeFinished(path, Error e) -> { m with Audio = AudioFailed(path, e) } |> log $"Could not read {Path.GetFileName path}: {e}", Cmd.none

    | FilesDropped paths ->
        let cmds = paths |> List.choose classifyDrop |> List.map Cmd.ofMsg
        (if cmds.IsEmpty then log "Dropped files were not a Guitar Pro, audio or image file." m else m), Cmd.batch cmds

    | SetRole(i, None) -> { m with Roles = m.Roles.Remove i }, Cmd.none
    | SetRole(i, Some r) -> { m with Roles = m.Roles.Add(i, r) }, Cmd.none
    | SetTone(i, key) -> { m with Tones = m.Tones.Add(i, key) }, Cmd.none

    | SetTitle v -> { m with Meta = { m.Meta with Title = Field.user v } }, Cmd.none
    | SetArtist v -> { m with Meta = { m.Meta with Artist = Field.user v } }, Cmd.none
    | SetAlbum v -> { m with Meta = { m.Meta with Album = Field.user v } }, Cmd.none
    | SetYear v -> { m with Meta = { m.Meta with Year = Field.user v } }, Cmd.none
    | SetTuningPitch v -> { m with Meta = { m.Meta with TuningPitch = v } }, Cmd.none
    | SetCharter v -> { m with Meta = { m.Meta with Charter = v } }, Cmd.none
    | PickAlbumArt -> m, Cmd.OfAsync.perform (fun () -> Dialogs.openFile window "Choose album art" Dialogs.imagePatterns) () AlbumArtPicked
    | AlbumArtPicked p -> { m with Meta = { m.Meta with AlbumArt = (match p with Some x -> Some x | None -> m.Meta.AlbumArt) } }, Cmd.none

    | SetOffset v -> { m with Sync = { m.Sync with OffsetMs = v } }, Cmd.none
    | SetScale v -> { m with Sync = { m.Sync with TempoScale = v } }, Cmd.none
    | ToggleAdvancedSync -> { m with Sync = { m.Sync with Advanced = not m.Sync.Advanced } }, Cmd.none
    | SetNewAnchorBar v -> { m with Sync = { m.Sync with NewAnchor = { m.Sync.NewAnchor with Bar = v } } }, Cmd.none
    | SetNewAnchorSeconds v -> { m with Sync = { m.Sync with NewAnchor = { m.Sync.NewAnchor with AudioSeconds = v } } }, Cmd.none
    | AddAnchor ->
        match Derive.score m with
        | None -> m, Cmd.none
        | Some s ->
            match Derive.parseNewAnchor m s with
            | Error e -> log e m, Cmd.none
            | Ok a ->
                let anchors = (SyncMap.add a (SyncMap.create m.Sync.Anchors)).Anchors
                { m with Sync = { m.Sync with Anchors = anchors; NewAnchor = { Bar = ""; AudioSeconds = "" } } }, Cmd.none
    | RemoveAnchor t -> { m with Sync = { m.Sync with Anchors = m.Sync.Anchors |> List.filter (fun a -> a.Tick <> t) } }, Cmd.none
    | SeedAnchorsFromSource ->
        match Derive.score m with
        | Some s when not s.SourceSyncPoints.IsEmpty ->
            let anchors = (SyncMap.ofSourceSyncPoints s).Anchors
            { m with Sync = { m.Sync with Anchors = anchors; Advanced = true } } |> log $"Seeded {anchors.Length} anchors from the file's own sync points.", Cmd.none
        | _ -> m, Cmd.none
    | ClearAnchors -> { m with Sync = { m.Sync with Anchors = [] } }, Cmd.none

    | TogglePC -> { m with Output = { m.Output with PC = not m.Output.PC } }, Cmd.none
    | ToggleMac -> { m with Output = { m.Output with Mac = not m.Output.Mac } }, Cmd.none
    | PickOutputDir -> m, Cmd.OfAsync.perform (fun () -> Dialogs.openFolder window "Choose the output folder") () OutputDirPicked
    | OutputDirPicked p -> { m with Output = { m.Output with Dir = (match p with Some x -> Some x | None -> m.Output.Dir) } }, Cmd.none
    | SetLevelCount l -> { m with Output = { m.Output with LevelCount = l } }, Cmd.none

    | StartBuild ->
        match Derive.buildRequest m with
        | Error blockers -> blockers |> List.fold (fun acc b -> log b acc) m, Cmd.none
        | Ok(req, audio) ->
            { m with Build = BuildRunning { Stage = Preparing; Percent = 0.0; Message = "Starting" } }
            |> log $"Build started. Work directory: {req.WorkDir}", startBuild m req audio
    | BuildProgressed p ->
        let m = { m with Build = BuildRunning p }
        (match m.Log with
         | last :: _ when last.EndsWith p.Message -> m
         | _ -> log p.Message m), Cmd.none
    | BuildFinished(Ok o) ->
        let m = { m with Build = BuildSucceeded o } |> log $"Built {o.Packages.Length} package(s) as {o.DlcKey}."
        (o.Packages |> List.fold (fun acc p -> log p acc) m), Cmd.none
    | BuildFinished(Error f) -> { m with Build = BuildFailed f } |> log f.Message, Cmd.none

    | Reveal path -> m, Cmd.ofEffect (fun _ -> reveal path)
