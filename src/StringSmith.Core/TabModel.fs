namespace StringSmith.Core

open System

/// A tempo event on the unrolled timeline.
type TempoEvent =
    {
        /// Absolute unrolled tick at which this tempo takes effect.
        Tick: int64<tick>
        /// Quarter notes per minute.
        Bpm: float
        /// When true, tempo ramps linearly from this event to the next one rather than
        /// stepping. Real community files do this (the Nightwish test file ramps across
        /// bars 85-94), so consumers must integrate ramps, not assume steps.
        IsLinearRamp: bool
    }

/// One playback occurrence of a bar on the unrolled timeline.
///
/// Guitar Pro writes a repeated section once and marks it with repeat signs; the
/// recording plays it twice. A model that kept the written order would misalign
/// everything after the first repeat, so producers unroll. SourceBarIndex points back
/// at the written bar for diagnostics and UI.
type BarInfo =
    {
        /// 0-based index on the unrolled timeline.
        Index: int
        /// 0-based index of the written bar this occurrence came from.
        SourceBarIndex: int
        StartTick: int64<tick>
        TimeSigNumerator: int
        TimeSigDenominator: int
        /// A pickup bar shorter than its time signature implies.
        IsAnacrusis: bool
    }

/// Note-level techniques. Flags, because several combine on one note.
[<Flags>]
type Technique =
    | None = 0
    | HammerOn = 1
    | PullOff = 2
    | PalmMute = 4
    | LetRing = 8
    | Staccato = 16
    /// Fret-hand mute / dead note ("x" in tab).
    | Dead = 32
    | Accent = 64
    | HeavyAccent = 128
    | Ghost = 256
    /// This note continues the previous note on the same string without a new attack.
    | TieDestination = 512
    /// This note is tied into the next note on the same string.
    | TieOrigin = 1024
    | LeftHandTap = 2048

type HarmonicKind =
    | NoHarmonic
    | Natural
    | Artificial
    | Pinch
    | Tap
    | Semi
    | Feedback

type SlideOut =
    | NoSlideOut
    /// Pitched slide to the next note, with a new attack on arrival.
    | Shift
    /// Pitched slide to the next note, no new attack (legato).
    | Legato
    | OutUp
    | OutDown
    | PickSlideDown
    | PickSlideUp

type SlideIn =
    | NoSlideIn
    | FromBelow
    | FromAbove

type Vibrato =
    | NoVibrato
    | Slight
    | Wide

type Dynamic =
    | PPP | PP | P | MP | MF | F | FF | FFF

/// One point on a bend curve.
type BendPoint =
    {
        /// Position across the note's duration, 0.0 at the attack, 1.0 at the end.
        /// Producers convert from their native scale (AlphaTab uses 0-60).
        Offset: float
        /// Pitch offset in semitones. Producers convert from their native scale
        /// (AlphaTab uses quarter-tones, so value / 2.0).
        Semitones: float
    }

/// A single fretted (or open) note.
type TabNote =
    {
        /// 0-based, 0 = the lowest-pitched string. This is Rocksmith's convention.
        /// AlphaTab is 1-based from the lowest string, so its producer subtracts one.
        String: int
        Fret: int
        Techniques: Technique
        Harmonic: HarmonicKind
        /// For artificial/tapped harmonics, the fret the harmonic is produced at.
        HarmonicFret: float option
        SlideOut: SlideOut
        /// Destination fret for a pitched slide, when known.
        SlideTargetFret: int option
        SlideIn: SlideIn
        /// Empty when the note does not bend. Semitones, not quarter-tones.
        BendPoints: BendPoint list
        Vibrato: Vibrato
        /// Fret trilled with, when the note is a trill.
        TrillFret: int option
        /// Left-hand finger 1-4 (T = 0) when the source carries it. None means the
        /// source had no fingering. This is never inferred or fabricated downstream;
        /// Rocksmith's -1 is the honest representation of "unknown".
        LeftHandFinger: int option
    }

/// A beat: one or more notes struck together, or a rest.
///
/// Grouping notes by beat is what preserves chords; a beat with several notes IS a
/// chord. Beat-level techniques (tremolo picking, strum direction, tap/slap/pop,
/// grace notes) live here because that is where both Guitar Pro and Rocksmith put them.
type TabBeat =
    {
        /// Absolute unrolled tick.
        StartTick: int64<tick>
        DurationTicks: int64<tick>
        /// Empty for a rest.
        Notes: TabNote list
        Dynamic: Dynamic
        IsTremoloPicked: bool
        IsTap: bool
        IsSlap: bool
        IsPop: bool
        /// A grace note belonging to the following beat.
        IsGrace: bool
        /// Strummed down (towards the floor) rather than plucked.
        BrushDown: bool
        BrushUp: bool
    }

    member this.IsRest = List.isEmpty this.Notes
    member this.IsChord = this.Notes.Length > 1

/// One instrument part.
type TabTrack =
    {
        /// Index within the source file.
        Index: int
        Name: string
        /// General MIDI program number. Useful for suggesting an arrangement role and
        /// for catching a vocal line charted onto a guitar staff (program 73, flute,
        /// in the Nightwish test file).
        MidiProgram: int
        IsPercussion: bool
        /// MIDI note numbers, lowest-pitched string first (index 0 = string 0).
        /// AlphaTab lists high-first, so its producer reverses.
        TuningMidi: int list
        Capo: int
        /// Number of staves in the source track. Only staff 0 is converted; more than
        /// one is surfaced to the user as a warning.
        StaffCount: int
        /// All voices flattened and sorted by StartTick.
        Beats: TabBeat list
    }

    member this.StringCount = this.TuningMidi.Length
    member this.NoteCount = this.Beats |> List.sumBy (fun b -> b.Notes.Length)

/// A parsed score on a single unrolled timeline.
type TabScore =
    {
        Title: string
        Artist: string
        Album: string
        /// Guitar Pro carries no year. Audio tags may.
        Year: int option
        /// Ticks per quarter note, as the producer uses them.
        TicksPerQuarter: int
        TempoMap: TempoEvent list
        Bars: BarInfo list
        Tracks: TabTrack list
    }

    /// The first tempo, or 120 if the score has none.
    member this.InitialBpm =
        match this.TempoMap with
        | first :: _ -> first.Bpm
        | [] -> 120.0

    /// The tick at which the last bar ends, i.e. the unrolled length of the score.
    member this.EndTick : int64<tick> =
        match List.tryLast this.Bars with
        | None -> 0L<tick>
        | Some last ->
            let quartersPerBar =
                float last.TimeSigNumerator * 4.0 / float last.TimeSigDenominator
            last.StartTick + int64 (quartersPerBar * float this.TicksPerQuarter) * 1L<tick>
