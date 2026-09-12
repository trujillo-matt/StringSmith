namespace StringSmith.Conversion

open StringSmith.Core

/// Which Rocksmith path the arrangement is for. Combo is out of scope for Milestone 1.
type ArrangementRole =
    | Lead
    | Rhythm
    | Bass

    member this.Name =
        match this with
        | Lead -> "Lead"
        | Rhythm -> "Rhythm"
        | Bass -> "Bass"

/// Tunable conventions. Every value here is a judgement call, not a fact, and is
/// documented as such in CLAUDE.md.
type ConversionOptions =
    {
        /// Notes shorter than this get no sustain unless a technique needs one (bend,
        /// vibrato, slide, tremolo, let ring, tie). Guitar Pro gives every note its full
        /// rhythmic slot; Rocksmith charts only hold notes that are actually held.
        MinSustainMs: int
        /// Rocksmith Note.Vibrato is a byte "speed". Convention: slight 80, wide 120.
        VibratoSlight: byte
        VibratoWide: byte
        /// How far an "out" (unpitched) slide travels, in frets.
        UnpitchedSlideFrets: int
        /// Default fret-hand anchor width.
        AnchorWidth: int
    }

    static member Default =
        { MinSustainMs = 400
          VibratoSlight = 80uy
          VibratoWide = 120uy
          UnpitchedSlideFrets = 4
          AnchorWidth = 4 }

/// Metadata for the arrangement header. Prefilled by the UI from the tab and audio tags,
/// then confirmed by the user. Nothing here is trusted without being shown.
type ArrangementMeta =
    {
        Title: string
        Artist: string
        Album: string
        Year: int
        /// A4 reference in Hz. 440.0 is standard; anything else becomes centOffset.
        TuningPitchHz: float
        Role: ArrangementRole
        /// 1 for the main arrangement of a path.
        Part: int16
        /// Length of the user's audio, which is the song length Rocksmith uses.
        SongLengthMs: int
    }

/// Things the conversion had to decide or drop. Shown inline in the UI.
type ConversionWarning =
    /// The track has more strings than Rocksmith supports for this role; the lowest ones
    /// were dropped along with their notes.
    | StringsDropped of trackStrings: int * kept: int * notesDropped: int
    /// Frets above 24 were kept as written; Rocksmith's checker will flag them too.
    | FretsAbove24 of count: int
    /// A legato slide whose target note was not adjacent on the same string was emitted
    /// as a re-picked (shift) slide instead.
    | LegatoWithoutAdjacentTarget of count: int
    /// Notes on the same string at the same time from different voices; the first won.
    | DuplicateStringNotes of count: int
    /// The source track has more than one staff; only the first was converted.
    | ExtraStavesIgnored of staffCount: int
    /// No notes carried fingering. Rocksmith shows -1 (unknown). Nothing was invented.
    | NoFingeringInSource

type ConversionStats =
    { Notes: int
      Chords: int
      ChordTemplates: int
      Anchors: int
      HandShapes: int
      Ebeats: int }

type ConversionResult =
    { Arrangement: Rocksmith2014.XML.InstrumentalArrangement
      Warnings: ConversionWarning list
      Stats: ConversionStats }
