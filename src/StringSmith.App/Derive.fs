/// Everything the views and the build need that is computed from the model rather than
/// stored in it. Pure; no Avalonia.
module StringSmith.App.Derive

open System
open System.Globalization
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion
open StringSmith.Audio
open StringSmith.Pipeline
open StringSmith.App.Model

/// m:ss.mmm
let fmtTime (ms: float) =
    let t = TimeSpan.FromMilliseconds ms
    let mins = int t.TotalMinutes
    let secs = t.Seconds.ToString("D2")
    let millis = t.Milliseconds.ToString("D3")
    $"{mins}:{secs}.{millis}"

let score (m: Model) =
    match m.Tab with
    | TabLoaded(_, s) -> Some s
    | _ -> None

let audio (m: Model) =
    match m.Audio with
    | AudioReady a -> Some a
    | _ -> None

let deps (m: Model) =
    match m.Deps with
    | DepsReady d -> Some d
    | _ -> None

let private parseFloat (s: string) =
    match Double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let private parseInt (s: string) =
    match Int32.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let offsetMs (m: Model) = parseFloat m.Sync.OffsetMs
let tempoScale (m: Model) = parseFloat m.Sync.TempoScale
let tuningPitch (m: Model) = parseFloat m.Meta.TuningPitch
let year (m: Model) = parseInt m.Meta.Year.Value

/// The sync map the build will use. Untouched defaults are honestly NotSynced: two
/// anchors at (0 ms, scale 1.0) would claim a precision nobody has established.
let syncMap (m: Model) (s: TabScore) : SyncMap =
    if m.Sync.Advanced && not m.Sync.Anchors.IsEmpty then
        SyncMap.create m.Sync.Anchors
    else
        match offsetMs m, tempoScale m with
        | Some 0.0, Some 1.0 -> SyncMap.empty
        | Some off, Some scale when scale > 0.0 ->
            SyncMap.ofOffsetAndScale (TempoMap.nominalMs s) s.EndTick (off * 1.0<ms>) scale
        | Some off, None -> SyncMap.create [ { Tick = 0L<tick>; AudioMs = off * 1.0<ms> } ]
        | _ -> SyncMap.empty

let drift (m: Model) : DriftReport option =
    match score m, audio m with
    | Some s, Some a -> Some(DriftReport.create s (syncMap m s) (a.LengthMs * 1.0<ms>))
    | _ -> None

/// Tick of a 1-based unrolled bar number, for anchor entry.
let tickOfBar (s: TabScore) (bar: int) =
    s.Bars |> List.tryItem (bar - 1) |> Option.map (fun b -> b.StartTick)

let parseNewAnchor (m: Model) (s: TabScore) : Result<SyncAnchor, string> =
    match parseInt m.Sync.NewAnchor.Bar, parseFloat m.Sync.NewAnchor.AudioSeconds with
    | None, _ -> Error "Bar must be a whole number"
    | Some b, _ when b < 1 || b > s.Bars.Length -> Error $"Bar must be between 1 and {s.Bars.Length}"
    | _, None -> Error "Audio time must be seconds, e.g. 12.345"
    | Some b, Some sec ->
        match tickOfBar s b with
        | Some t -> Ok { Tick = t; AudioMs = sec * 1000.0<ms> }
        | None -> Error "Bar out of range"

let mappedTracks (m: Model) (s: TabScore) : (TabTrack * ArrangementRole) list =
    s.Tracks
    |> List.choose (fun t -> m.Roles |> Map.tryFind t.Index |> Option.map (fun r -> t, r))

/// Suggests a role from the track's instrument, for the first-open default. Nothing is
/// applied silently: the user sees and can change every row.
let suggestRole (t: TabTrack) : ArrangementRole option =
    if t.IsPercussion || t.TuningMidi.IsEmpty then None
    else
        match t.MidiProgram, t.StringCount with
        | p, _ when p >= 32 && p <= 39 -> Some Bass          // GM bass patches
        | _, n when n <= 5 -> Some Bass
        | p, _ when p >= 24 && p <= 31 -> Some Lead          // GM guitar patches
        | _ -> None

/// Inline warnings for one track row. Plain sentences, no codes.
let trackWarnings (m: Model) (t: TabTrack) : string list =
    let role = m.Roles |> Map.tryFind t.Index
    [ match role with
      | Some Bass when t.StringCount > 4 ->
          yield $"Bass supports 4 strings; this track has {t.StringCount}. The lowest {t.StringCount - 4} will be dropped."
      | Some(Lead | Rhythm) when t.StringCount > 6 ->
          yield $"Guitar supports 6 strings; this track has {t.StringCount}. The lowest {t.StringCount - 6} will be dropped."
      | Some(Lead | Rhythm) when t.StringCount <= 4 ->
          yield "This looks like a bass track (4 strings) but is mapped to a guitar path."
      | Some Bass when t.StringCount = 6 && not (t.MidiProgram >= 32 && t.MidiProgram <= 39) ->
          yield "A 6-string track mapped to Bass: Rocksmith bass has 4 strings."
      | _ -> ()
      if role.IsSome && not (t.MidiProgram >= 24 && t.MidiProgram <= 39) then
          yield $"MIDI program {t.MidiProgram} is not a guitar or bass patch. Community tabs sometimes chart vocals or keys on a guitar staff."
      if role.IsSome && t.StaffCount > 1 then
          yield $"Track has {t.StaffCount} staves; only the first is converted."
      if role.IsSome && t.NoteCount = 0 then
          yield "This track has no notes." ]

let tuningLabel (t: TabTrack) =
    if t.TuningMidi.IsEmpty then "-"
    else
        let names = [| "C"; "C#"; "D"; "D#"; "E"; "F"; "F#"; "G"; "G#"; "A"; "A#"; "B" |]
        t.TuningMidi |> List.map (fun n -> names.[((n % 12) + 12) % 12]) |> String.concat " "

/// Why the Build button is disabled, or Ok. Every reason is a sentence the user can act on.
let buildBlockers (m: Model) : string list =
    [ match m.Deps with
      | DepsChecking -> yield "Still checking for FFmpeg and Wwise."
      | DepsReady d ->
          match d.Wwise with
          | Missing _ -> yield "Wwise was not found. It is required to encode audio."
          | Found _ -> ()
          match audio m, d.FFmpeg with
          | Some a, Missing _ when a.NeedsFFmpeg -> yield "This audio format needs FFmpeg to convert it to WAV."
          | _ -> ()
      match score m with
      | None -> yield "Choose a Guitar Pro file."
      | Some s -> if (mappedTracks m s).IsEmpty then yield "Map at least one track to an arrangement."
      if (audio m).IsNone then yield "Choose an audio file."
      if m.Meta.Title.Value.Trim() = "" then yield "Title is required."
      if m.Meta.Artist.Value.Trim() = "" then yield "Artist is required."
      if (year m).IsNone then yield "Year must be a number."
      if (tuningPitch m).IsNone then yield "Tuning frequency must be a number (Hz)."
      if m.Meta.AlbumArt.IsNone then yield "Album art is required (PNG or JPEG)."
      if m.Meta.Charter.Trim() = "" then yield "Charter name is required."
      if not (m.Output.PC || m.Output.Mac) then yield "Select at least one platform."
      if m.Output.Dir.IsNone then yield "Choose an output folder."
      match m.Build with
      | BuildRunning _ -> yield "A build is already running."
      | _ -> () ]

let buildRequest (m: Model) : Result<BuildRequest * AudioInfo, string list> =
    match buildBlockers m with
    | [] ->
        let s = (score m).Value
        let a = (audio m).Value
        let d = (deps m).Value
        let wwise = match d.Wwise with Found(p, _) -> Some p | Missing _ -> None
        let work = IO.Path.Combine(IO.Path.GetTempPath(), "StringSmith", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
        let req =
            { Score = s
              Arrangements =
                mappedTracks m s
                |> List.map (fun (t, role) ->
                    { Track = t
                      Role = role
                      ToneKey = m.Tones |> Map.tryFind t.Index |> Option.defaultValue (DefaultTones.forRole role).Key })
              Meta =
                { Title = m.Meta.Title.Value.Trim(); Artist = m.Meta.Artist.Value.Trim(); Album = m.Meta.Album.Value.Trim()
                  Year = (year m).Value; TuningPitchHz = (tuningPitch m).Value
                  AlbumArtPath = m.Meta.AlbumArt.Value; Charter = m.Meta.Charter.Trim() }
              Sync = syncMap m s
              AudioPath = a.Path
              PreviewStartMs = 10_000
              Platforms = [ if m.Output.PC then Rocksmith2014.Common.PC; if m.Output.Mac then Rocksmith2014.Common.Mac ]
              OutputDir = m.Output.Dir.Value
              WorkDir = work
              LevelCount = m.Output.LevelCount
              Tones = DefaultTones.all
              WemEncoder = wwise |> Option.map Wem.encode }
        Ok(req, a)
    | blockers -> Error blockers
