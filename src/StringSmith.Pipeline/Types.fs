namespace StringSmith.Pipeline

open Rocksmith2014.Common
open Rocksmith2014.Common.Manifest
open Rocksmith2014.DD
open Rocksmith2014.XML.Processing
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion

/// Song-level metadata as confirmed by the user.
type SongMeta =
    { Title: string
      Artist: string
      Album: string
      Year: int
      TuningPitchHz: float
      /// PNG or JPEG; becomes 64/128/256 DDS.
      AlbumArtPath: string
      /// Used for the DLC key prefix and the package author field.
      Charter: string }

/// One GP track mapped to one Rocksmith path, with the tone it plays through.
type ArrangementRequest =
    { Track: TabTrack
      Role: ArrangementRole
      ToneKey: string }

type BuildRequest =
    {
        Score: TabScore
        Arrangements: ArrangementRequest list
        Meta: SongMeta
        Sync: SyncMap
        /// WAV, OGG or FLAC. The Audio layer has already normalised anything else.
        AudioPath: string
        PreviewStartMs: int
        Platforms: Platform list
        OutputDir: string
        /// Scratch directory. Everything intermediate lands here and stays there on
        /// failure so the user can inspect or retry.
        WorkDir: string
        LevelCount: LevelCountGeneration
        Tones: Tone list
        /// Encodes a WAV/OGG/FLAC at the given path to <same name>.wem. None means the
        /// .wem files are expected to already exist next to the audio (headless tests,
        /// or a re-run after a packaging-only failure).
        WemEncoder: (string -> Async<unit>) option
    }

type Stage =
    | Preparing
    | Converting
    | MeasuringLoudness
    | CreatingPreview
    | EncodingAudio
    | Packaging
    | Validating

    member this.Label =
        match this with
        | Preparing -> "Preparing work directory"
        | Converting -> "Converting tab to arrangement XML"
        | MeasuringLoudness -> "Measuring loudness"
        | CreatingPreview -> "Creating preview clip"
        | EncodingAudio -> "Encoding audio with Wwise"
        | Packaging -> "Generating phrases, Dynamic Difficulty, and packing"
        | Validating -> "Checking the built arrangements"

type Progress =
    { Stage: Stage
      /// 0..100 within the whole build.
      Percent: float
      Message: string }

type ArrangementReport =
    { Role: ArrangementRole
      XmlPath: string
      Tuning: int16[]
      Warnings: ConversionWarning list
      Stats: ConversionStats
      /// The library's coded issues (I01..) on the arrangement AFTER phrase generation
      /// and DD, when that ran; before it otherwise.
      Issues: Issue list }

type BuildOutput =
    { Packages: string list
      DlcKey: string
      Arrangements: ArrangementReport list
      Drift: DriftReport
      WorkDir: string }

type BuildFailure =
    { Stage: Stage
      Message: string
      WorkDir: string
      /// Arrangements that were fully converted before the failure. Their XML is on disk.
      Completed: ArrangementReport list }
