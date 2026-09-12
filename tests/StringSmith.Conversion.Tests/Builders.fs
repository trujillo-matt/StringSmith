/// Hand-built Core data. Conversion is tested without a single Guitar Pro file.
module StringSmith.Conversion.Tests.Builders

open StringSmith.Core
open StringSmith.Conversion

let tpq = 960
let q = int64 tpq * 1L<tick>          // one quarter note in ticks
let bar = 4L * q                       // 4/4 bar

/// 120 bpm linear time: 960 ticks = 500 ms. Plus an offset so the first beat is not at 0,
/// which is also what a real recording looks like.
let offsetMs = 1000
let toMs (t: int64<tick>) = offsetMs + int (float t * 500.0 / 960.0)

let bars n =
    [ for i in 0 .. n - 1 ->
        { Index = i; SourceBarIndex = i; StartTick = int64 i * bar
          TimeSigNumerator = 4; TimeSigDenominator = 4; IsAnacrusis = false } ]

let note s f : TabNote =
    { String = s; Fret = f; Techniques = Technique.None; Harmonic = NoHarmonic; HarmonicFret = None
      SlideOut = NoSlideOut; SlideTargetFret = None; SlideIn = NoSlideIn; BendPoints = []
      Vibrato = NoVibrato; TrillFret = None; LeftHandFinger = None }

let withTech t (n: TabNote) = { n with Techniques = n.Techniques ||| t }

let beat (start: int64<tick>) (dur: int64<tick>) (notes: TabNote list) : TabBeat =
    { StartTick = start; DurationTicks = dur; Notes = notes; ChordName = None; Dynamic = MF
      IsTremoloPicked = false; IsTap = false; IsSlap = false; IsPop = false; IsGrace = false
      BrushDown = false; BrushUp = false }

let track (tuning: int list) (beats: TabBeat list) : TabTrack =
    { Index = 0; Name = "t"; MidiProgram = 25; IsPercussion = false; TuningMidi = tuning
      Capo = 0; StaffCount = 1; Beats = beats |> List.sortBy (fun b -> b.StartTick) }

let standard = [ 40; 45; 50; 55; 59; 64 ]
let bassStandard = [ 28; 33; 38; 43 ]

let score (nBars: int) (t: TabTrack) : TabScore =
    { Title = "Song"; Artist = "Artist"; Album = "Album"; Year = None; TicksPerQuarter = tpq
      TempoMap = [ { Tick = 0L<tick>; Bpm = 120.0; IsLinearRamp = false } ]
      Bars = bars nBars; Tracks = [ t ]; SourceSyncPoints = [] }

let meta role : ArrangementMeta =
    { Title = "Song"; Artist = "Artist"; Album = "Album"; Year = 2024; TuningPitchHz = 440.0
      Role = role; Part = 1s; SongLengthMs = 60_000 }

let convert role (s: TabScore) =
    Convert.toArrangement ConversionOptions.Default (meta role) toMs s s.Tracks.Head

let level (r: ConversionResult) = r.Arrangement.Levels.[0]
