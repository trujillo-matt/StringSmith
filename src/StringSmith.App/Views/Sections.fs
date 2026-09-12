module StringSmith.App.Views.Sections

open System
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Rocksmith2014.DD
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion
open StringSmith.Audio
open StringSmith.Pipeline
open StringSmith.App
open StringSmith.App.Model
open StringSmith.App.Views.Widgets

// ---------------------------------------------------------------- 1 dependencies

let dependencies (m: Model) (dispatch: Msg -> unit) : IView =
    let body =
        match m.Deps with
        | DepsChecking -> [ dim "Checking for FFmpeg, ffprobe and Wwise…" ]
        | DepsReady d ->
            let row name what status =
                match status with
                | Found(path, version) -> good $"{name} {version}  ({path})"
                | Missing searched ->
                    let where = String.Join(", ", searched)
                    error $"{name} not found. Needed to {what}. Looked in: {where}"
            [ row "FFmpeg" "convert MP3/M4A to WAV (not needed for WAV/OGG/FLAC)" d.FFmpeg
              row "ffprobe" "read the audio file's tags and length" d.FFprobe
              row "Wwise" "encode audio to WEM. Install Wwise 2019-2024 from Audiokinetic into /Applications/Audiokinetic" d.Wwise
              button "Check again" true (fun () -> dispatch RecheckDeps) ]
    section "1. Dependencies" true "" body

// ---------------------------------------------------------------- 2 source files

let sources (m: Model) (dispatch: Msg -> unit) : IView =
    let tabNotes =
        match m.Tab with
        | NoTab -> [ dim "Drop a .gp3 / .gp4 / .gp5 / .gpx / .gp file anywhere in this window, or choose one." ]
        | TabLoading _ -> [ dim "Loading…" ]
        | TabLoaded(_, s) ->
            [ good $"{s.Tracks.Length} tracks, {s.Bars.Length} bars after unrolling repeats, {s.InitialBpm} bpm at the start, {fmtTime (float (TempoMap.nominalMs s s.EndTick))} long by the tab's own tempo map."
              if s.SourceSyncPoints.Length > 0 then good $"This file carries {s.SourceSyncPoints.Length} sync points against a backing track." ]
        | TabFailed(_, e) ->
            [ error (match e with
                     | FileNotFound _ -> "File not found."
                     | UnsupportedFormat(_, d) -> $"Unsupported format: {d}"
                     | CorruptFile(_, d) -> $"Could not read the file: {d}"
                     | NoPlayableTracks _ -> "No playable notes found. This is either an empty tab or not a Guitar Pro file.") ]
    let audioNotes =
        match m.Audio with
        | NoAudio -> [ dim "WAV, OGG, FLAC directly; MP3, M4A and AAC through FFmpeg." ]
        | AudioProbing _ -> [ dim "Reading…" ]
        | AudioReady a ->
            let codec = defaultArg a.Tags.Codec "unknown codec"
            [ good $"{fmtTime a.LengthMs}, {codec}."
              if a.NeedsFFmpeg then dim "Will be converted to WAV with FFmpeg at build time." ]
        | AudioFailed(_, e) -> [ error e ]
    let tabPath = match m.Tab with TabLoaded(p, _) | TabLoading p | TabFailed(p, _) -> Some p | NoTab -> None
    let audioPath = match m.Audio with AudioReady a -> Some a.Path | AudioProbing p | AudioFailed(p, _) -> Some p | NoAudio -> None
    section "2. Source files" true "" [
        filePicker "Guitar Pro file" tabPath "No tab chosen" (fun () -> dispatch PickTab) tabNotes
        filePicker "Audio file" audioPath "No audio chosen" (fun () -> dispatch PickAudio) audioNotes
    ]

// ---------------------------------------------------------------- 3 track mapping

let private roleLabels = [ "Not included"; "Lead"; "Rhythm"; "Bass" ]
let private roleOf = function "Lead" -> Some Lead | "Rhythm" -> Some Rhythm | "Bass" -> Some Bass | _ -> None
let private labelOf = function Some Lead -> "Lead" | Some Rhythm -> "Rhythm" | Some Bass -> "Bass" | None -> "Not included"

let tracks (m: Model) (dispatch: Msg -> unit) : IView =
    match Derive.score m with
    | None -> section "3. Track mapping" false "Choose a Guitar Pro file first." []
    | Some s ->
        let toneKeys = DefaultTones.all |> List.map (fun t -> t.Key)
        let header =
            Grid.create [
                Grid.columnDefinitions "*,130,70,70,70,140,170"
                Grid.children [
                    for (i, h) in List.indexed [ "Track"; "Tuning"; "Strings"; "MIDI"; "Notes"; "Arrangement"; "Tone" ] do
                        TextBlock.create [ Grid.column i; TextBlock.text h; TextBlock.foreground "#9a9a9a"; TextBlock.fontSize 12.0 ]
                ]
            ] :> IView
        let row (t: TabTrack) : IView =
            let role = m.Roles |> Map.tryFind t.Index
            let tone = m.Tones |> Map.tryFind t.Index |> Option.defaultValue (match role with Some r -> (DefaultTones.forRole r).Key | None -> toneKeys.Head)
            let mappable = not t.IsPercussion && not t.TuningMidi.IsEmpty
            vstack 2.0 [
                Grid.create [
                    Grid.columnDefinitions "*,130,70,70,70,140,170"
                    Grid.children [
                        TextBlock.create [ Grid.column 0; TextBlock.text t.Name; TextBlock.verticalAlignment VerticalAlignment.Center; TextBlock.textTrimming Media.TextTrimming.CharacterEllipsis ]
                        TextBlock.create [ Grid.column 1; TextBlock.text (Derive.tuningLabel t); TextBlock.verticalAlignment VerticalAlignment.Center ]
                        TextBlock.create [ Grid.column 2; TextBlock.text (if t.IsPercussion then "drums" else string t.StringCount); TextBlock.verticalAlignment VerticalAlignment.Center ]
                        TextBlock.create [ Grid.column 3; TextBlock.text (string t.MidiProgram); TextBlock.verticalAlignment VerticalAlignment.Center ]
                        TextBlock.create [ Grid.column 4; TextBlock.text (string t.NoteCount); TextBlock.verticalAlignment VerticalAlignment.Center ]
                        if mappable then
                            ComboBox.create [
                                Grid.column 5
                                ComboBox.dataItems roleLabels
                                ComboBox.selectedItem (labelOf role)
                                ComboBox.width 130.0
                                ComboBox.onSelectedItemChanged (fun o -> match o with :? string as l -> dispatch (SetRole(t.Index, roleOf l)) | _ -> ())
                            ]
                        else
                            TextBlock.create [ Grid.column 5; TextBlock.text "—"; TextBlock.foreground "#9a9a9a"; TextBlock.verticalAlignment VerticalAlignment.Center ]
                        if role.IsSome then
                            ComboBox.create [
                                Grid.column 6
                                ComboBox.dataItems toneKeys
                                ComboBox.selectedItem tone
                                ComboBox.width 160.0
                                ComboBox.onSelectedItemChanged (fun o -> match o with :? string as k -> dispatch (SetTone(t.Index, k)) | _ -> ())
                            ]
                    ]
                ]
                yield! Derive.trackWarnings m t |> List.map warn
            ]
        section "3. Track mapping" true "" [
            dim "Suggested roles come from each track's MIDI instrument and string count. Check them; nothing is applied silently."
            header
            yield! s.Tracks |> List.map row
        ]

// ---------------------------------------------------------------- 4 metadata

let metadata (m: Model) (dispatch: Msg -> unit) : IView =
    let enabled = (Derive.score m).IsSome
    let yearNote = if m.Meta.Year.Value <> "" && (Derive.year m).IsNone then [ warn "Year must be a number." ] else []
    let pitchNote =
        match Derive.tuningPitch m with
        | None -> [ warn "Enter the A4 reference in Hz, e.g. 440." ]
        | Some p when abs (p - 440.0) > 0.01 ->
            let cents = (Tuning.centOffset p).ToString("F2")
            [ dim $"centOffset {cents}" ]
        | _ -> []
    let tuningNotes =
        match Derive.score m with
        | Some s ->
            Derive.mappedTracks m s
            |> List.map (fun (t, role) ->
                let offs = Tuning.offsets role t.TuningMidi
                let label = if Tuning.isStandard offs then "standard" else String.Join(" ", offs |> Array.truncate (Tuning.maxStrings role) |> Array.map (fun o -> if o >= 0s then $"+{o}" else string o))
                dim $"{role.Name} ({t.Name}): {Derive.tuningLabel t}, {label}")
        | None -> []
    section "4. Metadata" enabled "Choose a Guitar Pro file first." [
        dim "Prefilled from the tab and the audio file's tags where available. Every field is editable; the label beside each says where its value came from."
        textField "Title" m.Meta.Title.Value m.Meta.Title.Source.Label (SetTitle >> dispatch) []
        textField "Artist" m.Meta.Artist.Value m.Meta.Artist.Source.Label (SetArtist >> dispatch) []
        textField "Album" m.Meta.Album.Value m.Meta.Album.Source.Label (SetAlbum >> dispatch) []
        textField "Year" m.Meta.Year.Value m.Meta.Year.Source.Label (SetYear >> dispatch) yearNote
        textField "Tuning frequency (Hz)" m.Meta.TuningPitch "" (SetTuningPitch >> dispatch) pitchNote
        labelled "Tuning" (vstack 2.0 (if tuningNotes.IsEmpty then [ dim "Taken from each mapped track; see Track mapping." ] else tuningNotes)) []
        filePicker "Album art" m.Meta.AlbumArt "No image chosen (PNG or JPEG; becomes 64/128/256 DDS)" (fun () -> dispatch PickAlbumArt) []
        textField "Charter name" m.Meta.Charter "" (SetCharter >> dispatch) [ dim "Used for the package author and the DLC key prefix." ]
    ]

// ---------------------------------------------------------------- 5 sync

let sync (m: Model) (dispatch: Msg -> unit) : IView =
    match Derive.score m, Derive.audio m, Derive.drift m with
    | Some s, Some _, Some report ->
        let statusChip =
            match report.Status with
            | NotSynced -> chip "NOT SYNCED" "#7a3b3b"
            | OffsetOnly -> chip "OFFSET ONLY" "#7a5a2b"
            | OffsetAndScale -> chip "OFFSET + TEMPO SCALE" "#5a5a2b"
            | Anchored n -> chip $"ANCHORED ({n} points)" "#2b5a3b"
        let tabEnd = fmtTime (float report.TabEndAudioMs)
        let audioLen = fmtTime (float report.AudioLengthMs)
        let mismatchSec = float report.MismatchMs / 1000.0
        let mismatchText = (if mismatchSec >= 0.0 then "+" else "") + mismatchSec.ToString("F2")
        let offBy = (abs mismatchSec).ToString("F1")
        let rampedBars = String.Join(", ", report.RampedBars |> List.map (fun b -> string (b.Index + 1)) |> List.truncate 12)
        let rampedMore = if report.RampedBars.Length > 12 then ", …" else ""
        let readout =
            [ text $"Tab ends at {tabEnd} under the current sync. Audio is {audioLen} long. Mismatch {mismatchText} s."
              if report.Status = NotSynced then
                  warn "The chart is NOT aligned to this recording. A tab's tempo map is beats and whole-number BPM; against a real recording it drifts. Set at least a start offset and tempo scale."
              elif abs (float report.MismatchMs) > float DriftReport.warnThresholdMs then
                  warn $"The tab and the audio disagree by {offBy} s. Notes will be out of time. Adjust the tempo scale, or add anchors."
              else
                  good "Tab length matches the recording to within a second. This checks overall length only, not every note."
              if not report.RampedBars.IsEmpty then
                  warn $"Bars {rampedBars}{rampedMore} carry gradual tempo changes, which the tab format only approximates. Anchors near them are strongly recommended." ]
        let simple =
            [ textField "Start offset (ms)" m.Sync.OffsetMs "" (SetOffset >> dispatch)
                  [ dim "Where the tab's first beat lands in the recording."
                    if (Derive.offsetMs m).IsNone then warn "Must be a number." ]
              textField "Tempo scale" m.Sync.TempoScale "" (SetScale >> dispatch)
                  [ dim "1.000 means the tab's BPM is exactly right. A tab at 95 bpm against a 94.3 bpm recording needs 95 / 94.3 = 1.0074; over four minutes that is almost two seconds."
                    if (Derive.tempoScale m).IsNone then warn "Must be a number." ] ]
        let barOf (t: int64<tick>) =
            s.Bars |> List.tryFind (fun b -> b.StartTick = t) |> Option.map (fun b -> $"bar {b.Index + 1}") |> Option.defaultValue $"tick {t}"
        let advanced =
            [ dim "Anchors pin a bar's first beat to a time in the recording. Between anchors the tab's own tempo shape is kept and stretched to fit."
              if m.Sync.Anchors.IsEmpty then dim "No anchors yet."
              for a in m.Sync.Anchors do
                  hstack 8.0 [
                      text $"{barOf a.Tick} → {fmtTime (float a.AudioMs)}"
                      button "Remove" true (fun () -> dispatch (RemoveAnchor a.Tick)) ]
              hstack 8.0 [
                  TextBox.create [ TextBox.width 90.0; TextBox.watermark "Bar"; TextBox.text m.Sync.NewAnchor.Bar; TextBox.onTextChanged (SetNewAnchorBar >> dispatch) ]
                  TextBox.create [ TextBox.width 140.0; TextBox.watermark "Audio time (s)"; TextBox.text m.Sync.NewAnchor.AudioSeconds; TextBox.onTextChanged (SetNewAnchorSeconds >> dispatch) ]
                  button "Add anchor" true (fun () -> dispatch AddAnchor)
                  button "Seed from file's sync points" (not s.SourceSyncPoints.IsEmpty) (fun () -> dispatch SeedAnchorsFromSource)
                  button "Clear" (not m.Sync.Anchors.IsEmpty) (fun () -> dispatch ClearAnchors) ] ]
        section "5. Audio sync" true "" [
            statusChip
            yield! readout
            CheckBox.create [ CheckBox.content "Use anchor points (advanced)"; CheckBox.isChecked m.Sync.Advanced; CheckBox.onChecked (fun _ -> if not m.Sync.Advanced then dispatch ToggleAdvancedSync); CheckBox.onUnchecked (fun _ -> if m.Sync.Advanced then dispatch ToggleAdvancedSync) ]
            yield! (if m.Sync.Advanced then advanced else simple)
        ]
    | _ -> section "5. Audio sync" false "Choose both a Guitar Pro file and an audio file first." []

// ---------------------------------------------------------------- 6 output

let output (m: Model) (dispatch: Msg -> unit) : IView =
    let enabled = (Derive.score m |> Option.map (fun s -> not (Derive.mappedTracks m s).IsEmpty) |> Option.defaultValue false)
    section "6. Output" enabled "Map at least one track to an arrangement first." [
        labelled "Platforms" (hstack 16.0 [
            CheckBox.create [ CheckBox.content "PC  (_p.psarc)"; CheckBox.isChecked m.Output.PC; CheckBox.onChecked (fun _ -> if not m.Output.PC then dispatch TogglePC); CheckBox.onUnchecked (fun _ -> if m.Output.PC then dispatch TogglePC) ]
            CheckBox.create [ CheckBox.content "Mac  (_m.psarc)"; CheckBox.isChecked m.Output.Mac; CheckBox.onChecked (fun _ -> if not m.Output.Mac then dispatch ToggleMac); CheckBox.onUnchecked (fun _ -> if m.Output.Mac then dispatch ToggleMac) ]
        ]) [ dim "Both are built from one set of assets; only paths and the SNG key differ." ]
        filePicker "Output folder" m.Output.Dir "No folder chosen" (fun () -> dispatch PickOutputDir) []
        labelled "Difficulty levels" (hstack 16.0 [
            RadioButton.create [ RadioButton.content "Simple (DLC Builder default)"; RadioButton.groupName "lc"; RadioButton.isChecked (m.Output.LevelCount = LevelCountGeneration.Simple); RadioButton.onClick (fun _ -> dispatch (SetLevelCount LevelCountGeneration.Simple)) ]
            RadioButton.create [ RadioButton.content "ML model"; RadioButton.groupName "lc"; RadioButton.isChecked (m.Output.LevelCount = LevelCountGeneration.MLModel); RadioButton.onClick (fun _ -> dispatch (SetLevelCount LevelCountGeneration.MLModel)) ]
        ]) [ dim "How many Dynamic Difficulty levels each phrase gets." ]
    ]

// ---------------------------------------------------------------- 7 build

let private arrangementReport (r: ArrangementReport) : IView =
    let warningText = function
        | StringsDropped(from, kept, lost) -> $"Track had {from} strings; kept the top {kept}, dropped {lost} note(s) on the lowest."
        | FretsAbove24 n -> $"{n} note(s) above fret 24, kept as written."
        | LegatoWithoutAdjacentTarget n -> $"{n} legato slide(s) had no adjacent target note and were written as re-picked slides."
        | DuplicateStringNotes n -> $"{n} note(s) on the same string at the same time (two voices); the first was kept."
        | ExtraStavesIgnored n -> $"Track has {n} staves; only the first was converted."
        | NoFingeringInSource -> "The tab carries no fingering. Rocksmith will show none rather than a guess."
    let issues = r.Issues |> List.sortBy (fun i -> i.TimeCode |> Option.defaultValue -1)
    vstack 3.0 [
        text $"{r.Role.Name}: {r.Stats.Notes} notes, {r.Stats.Chords} chords, {r.Stats.ChordTemplates} chord shapes, {r.Stats.Anchors} anchors, {r.Stats.Ebeats} beats."
        yield! r.Warnings |> List.map (warningText >> warn)
        if issues.IsEmpty then good "The library's arrangement checker found no issues."
        else
            warn $"Arrangement checker: {issues.Length} issue(s) on the packed arrangement."
            Expander.create [
                Expander.header $"Show {min issues.Length 40} of {issues.Length}"
                Expander.content (vstack 1.0 [ for i in issues |> List.truncate 40 -> mono (IssueText.line i) ])
            ]
    ]

let build (m: Model) (dispatch: Msg -> unit) : IView =
    let blockers = Derive.buildBlockers m
    let body =
        match m.Build with
        | BuildIdle ->
            [ if blockers.IsEmpty then good "Ready to build."
              else
                  dim "Build is disabled until:"
                  yield! blockers |> List.map dim ]
        | BuildRunning p ->
            [ text p.Stage.Label
              ProgressBar.create [ ProgressBar.minimum 0.0; ProgressBar.maximum 100.0; ProgressBar.value p.Percent; ProgressBar.height 14.0 ]
              dim p.Message ]
        | BuildSucceeded o ->
            [ good $"Built {o.Packages.Length} package(s). DLC key {o.DlcKey}."
              for p in o.Packages do
                  hstack 8.0 [ mono (Path.GetFileName p); button "Reveal in Finder" true (fun () -> dispatch (Reveal p)) ]
              (match o.Drift.Status with
               | NotSynced -> warn "This package was built UNSYNCED. It will play, but the notes are not aligned to this recording."
               | st ->
                   let mism = (float o.Drift.MismatchMs / 1000.0).ToString("F2")
                   dim $"Sync: {st}. Tab/audio mismatch {mism} s.")
              yield! o.Arrangements |> List.map arrangementReport
              hstack 8.0 [ dim $"Work files kept in {o.WorkDir}"; button "Reveal" true (fun () -> dispatch (Reveal o.WorkDir)) ]
              warn "Next step is yours: copy the package into Rocksmith's dlc folder and confirm it loads and plays with Dynamic Difficulty. Nothing here can verify in-game behaviour." ]
        | BuildFailed f ->
            [ error f.Message
              if not f.Completed.IsEmpty then
                  dim $"{f.Completed.Length} arrangement(s) were converted before the failure; their XML is kept in the work folder, so a retry does not start from nothing."
              hstack 8.0 [ dim $"Work folder: {f.WorkDir}"; button "Reveal" true (fun () -> dispatch (Reveal f.WorkDir)) ] ]
    section "7. Build" true "" [
        yield! body
        Expander.create [
            Expander.header "Log"
            Expander.content (ScrollViewer.create [ ScrollViewer.maxHeight 220.0; ScrollViewer.content (vstack 0.0 [ for l in m.Log |> List.truncate 200 -> mono l ]) ])
        ]
    ]
