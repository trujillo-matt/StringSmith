namespace StringSmith.GuitarPro

open System
open System.IO
open AlphaTab
open AlphaTab.Importer
open AlphaTab.Midi
open AlphaTab.Model
open StringSmith.Core

/// Loads Guitar Pro files (.gp3 .gp4 .gp5 .gpx .gp) through AlphaTab into a TabScore
/// on a single unrolled timeline.
///
/// Verified behaviours this relies on (see CLAUDE.md, "AlphaTab to Rocksmith unit
/// conversions"):
///   - tick resolution is 960 per quarter (MidiUtils.QuarterTime)
///   - Note.String is 1-based from the lowest-pitched string; Staff.Tuning is listed
///     high string first
///   - Beat.PlaybackStart is bar-relative; the unrolled absolute tick of a beat is
///     occurrence.Start + beat.PlaybackStart
///   - tempo automations are STEPPED by AlphaTab's own generator, never integrated,
///     even when IsLinear is set
type AlphaTabSource() =
    /// AlphaTab keeps static mutable state (Score.ResetIds() is a static, and importers
    /// share caches). Two loads on different threads corrupt each other: observed as
    /// "Collection was modified; enumeration operation may not execute" under Expecto's
    /// parallel runner, failing a different fixture each run. Every parse and generate
    /// call therefore goes through this gate. Loads are fast; serialising them costs
    /// nothing a user would notice.
    static let gate = obj ()


    /// AlphaTab's fixed MIDI resolution.
    static member TicksPerQuarter = 960

    static member private LoadScore(path: string) : Result<Score, TabLoadError> =
        if not (File.Exists path) then
            Error(FileNotFound path)
        else
            try
                let bytes = File.ReadAllBytes path
                let data = AlphaTab.Core.EcmaScript.Uint8Array.op_Implicit bytes
                Ok(ScoreLoader.LoadScoreFromBytes(data, Settings()))
            with e ->
                // AlphaTab's own error types are not stable public API across versions;
                // classify by name so a rename cannot break loading.
                if e.GetType().Name.Contains("UnsupportedFormat", StringComparison.OrdinalIgnoreCase) then
                    Error(UnsupportedFormat(path, e.Message))
                else
                    Error(CorruptFile(path, e.Message))

    /// Runs AlphaTab's generator purely to obtain the unrolled bar sequence.
    static member private Unroll(score: Score) : MidiFileGenerator =
        let gen = MidiFileGenerator(score, Settings(), NoOpMidiHandler.create ())
        gen.Generate()
        gen

    static member private Bars(gen: MidiFileGenerator) : BarInfo list =
        gen.TickLookup.MasterBars
        |> Seq.mapi (fun i occ ->
            let mb = occ.MasterBar
            { Index = i
              SourceBarIndex = int mb.Index
              StartTick = int64 occ.Start * 1L<tick>
              TimeSigNumerator = int mb.TimeSignatureNumerator
              TimeSigDenominator = int mb.TimeSignatureDenominator
              IsAnacrusis = mb.IsAnacrusis })
        |> List.ofSeq

    /// Per-occurrence tempo changes as AlphaTab emits them (stepped), with consecutive
    /// duplicates removed. The first occurrence carries the initial tempo twice.
    static member private TempoMap(gen: MidiFileGenerator) : TempoEvent list =
        gen.TickLookup.MasterBars
        |> Seq.collect (fun occ ->
            let ramps = occ.MasterBar.TempoAutomations |> Seq.exists (fun a -> a.IsLinear)
            occ.TempoChanges
            |> Seq.map (fun tc ->
                { Tick = int64 tc.Tick * 1L<tick>
                  Bpm = tc.Tempo
                  IsLinearRamp = ramps }))
        |> Seq.fold
            (fun acc ev ->
                match acc with
                | prev :: _ when prev.Tick = ev.Tick && prev.Bpm = ev.Bpm -> acc
                | _ -> ev :: acc)
            []
        |> List.rev

    static member private Note(n: AlphaTab.Model.Note) : TabNote =
        { String = int n.String - 1
          Fret = int n.Fret
          Techniques = Mapping.techniques n
          Harmonic = Mapping.harmonic n.HarmonicType
          HarmonicFret =
            if n.IsHarmonic && n.HarmonicType <> HarmonicType.Natural then Some n.HarmonicValue else None
          SlideOut = Mapping.slideOut n.SlideOutType
          SlideTargetFret =
            match n.SlideTarget with
            | null -> None
            | target -> Some(int target.Fret)
          SlideIn = Mapping.slideIn n.SlideInType
          BendPoints =
            match n.BendPoints with
            | null -> []
            | points when n.HasBend -> points |> Seq.map Mapping.bendPoint |> List.ofSeq
            | _ -> []
          Vibrato = Mapping.vibrato n.Vibrato
          TrillFret = if n.IsTrill then Some(int n.TrillFret) else None
          LeftHandFinger = Mapping.finger n.LeftHandFinger }

    static member private Beat(occStart: float) (b: AlphaTab.Model.Beat) : TabBeat =
        { StartTick = int64 (occStart + b.PlaybackStart) * 1L<tick>
          DurationTicks = int64 b.PlaybackDuration * 1L<tick>
          Notes =
            if b.IsRest then []
            else b.Notes |> Seq.filter (fun n -> n.IsStringed) |> Seq.map AlphaTabSource.Note |> List.ofSeq
          ChordName =
            if b.HasChord then
                match b.Chord with
                | null -> None
                | c when System.String.IsNullOrWhiteSpace c.Name -> None
                | c -> Some c.Name
            else
                None
          Dynamic = Mapping.dynamic b.Dynamics
          IsTremoloPicked = b.IsTremolo
          IsTap = b.Tap
          IsSlap = b.Slap
          IsPop = b.Pop
          IsGrace = b.GraceType <> GraceType.None
          BrushDown = (b.BrushType = BrushType.BrushDown || b.BrushType = BrushType.ArpeggioDown)
          BrushUp = (b.BrushType = BrushType.BrushUp || b.BrushType = BrushType.ArpeggioUp) }

    /// All voices of staff 0, walked once per unrolled occurrence, flattened and
    /// sorted. Same-tick beats from different voices are kept separate on purpose:
    /// chord grouping is Conversion's decision, not the parser's.
    static member private Beats(gen: MidiFileGenerator) (track: Track) : TabBeat list =
        let staff = track.Staves.[0]
        gen.TickLookup.MasterBars
        |> Seq.collect (fun occ ->
            let bar = staff.Bars.[int occ.MasterBar.Index]
            bar.Voices
            |> Seq.collect (fun v -> v.Beats)
            |> Seq.map (AlphaTabSource.Beat occ.Start))
        |> Seq.sortBy (fun b -> b.StartTick)
        |> List.ofSeq

    static member private Track(gen: MidiFileGenerator) (t: Track) : TabTrack =
        let staff = t.Staves.[0]
        let stringed = staff.IsStringed && not t.IsPercussion
        { Index = int t.Index
          Name = t.Name
          MidiProgram = int t.PlaybackInfo.Program
          IsPercussion = t.IsPercussion
          TuningMidi =
            if stringed then staff.Tuning |> Seq.map int |> List.ofSeq |> List.rev else []
          Capo = int staff.Capo
          StaffCount = t.Staves.Count
          Beats = if stringed then AlphaTabSource.Beats gen t else [] }

    /// Native sync points the file carried, resolved by the generator to
    /// (unrolled tick, audio ms). Empty for the overwhelming majority of files.
    static member private SourceSyncPoints(gen: MidiFileGenerator) =
        gen.SyncPoints
        |> Seq.map (fun sp -> int64 sp.SynthTick * 1L<tick>, sp.SyncTime * 1.0<ms>)
        |> List.ofSeq

    member _.Load(path: string) : Result<TabScore, TabLoadError> =
      lock gate (fun () ->
        AlphaTabSource.LoadScore path
        |> Result.bind (fun score ->
            let gen = AlphaTabSource.Unroll score
            let tracks = score.Tracks |> Seq.map (AlphaTabSource.Track gen) |> List.ofSeq
            // AlphaTab's binary readers accept arbitrary bytes and yield a default empty
            // score (1 track, 1 bar, 0 notes) rather than throwing; only text input is
            // rejected. Verified with zero, sequential and random byte inputs. A file with
            // no notes anywhere is therefore unusable whether it is garbage or an empty
            // tab, and we cannot tell which. Say so rather than guess.
            if tracks |> List.forall (fun t -> t.IsPercussion || t.TuningMidi.IsEmpty || t.NoteCount = 0) then
                Error(NoPlayableTracks path)
            else
                Ok
                    { Title = score.Title
                      Artist = score.Artist
                      Album = score.Album
                      Year = None
                      TicksPerQuarter = AlphaTabSource.TicksPerQuarter
                      TempoMap = AlphaTabSource.TempoMap gen
                      Bars = AlphaTabSource.Bars gen
                      Tracks = tracks
                      SourceSyncPoints = AlphaTabSource.SourceSyncPoints gen }))

    interface ITabSource with
        member this.Load path = this.Load path
