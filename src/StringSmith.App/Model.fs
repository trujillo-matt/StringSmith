module StringSmith.App.Model

open Rocksmith2014.DD
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion
open StringSmith.Audio
open StringSmith.Pipeline

type DepsState =
    | DepsChecking
    | DepsReady of Dependencies

type TabState =
    | NoTab
    | TabLoading of path: string
    | TabLoaded of path: string * TabScore
    | TabFailed of path: string * TabLoadError

type AudioInfo =
    { Path: string
      Tags: AudioTags
      LengthMs: float
      /// Not WAV/OGG/FLAC: FFmpeg will convert it at build time.
      NeedsFFmpeg: bool }

type AudioState =
    | NoAudio
    | AudioProbing of path: string
    | AudioReady of AudioInfo
    | AudioFailed of path: string * message: string

/// Where a metadata value came from. Shown beside every prefilled field; a user edit
/// is never overwritten by a later prefill.
type Source =
    | Unset
    | FromTab
    | FromAudio
    | FromUser

    member this.Label =
        match this with
        | Unset -> ""
        | FromTab -> "from tab"
        | FromAudio -> "from audio tags"
        | FromUser -> "edited"

type Field = { Value: string; Source: Source }

module Field =
    let empty = { Value = ""; Source = Unset }
    let user v = { Value = v; Source = FromUser }

type Meta =
    { Title: Field
      Artist: Field
      Album: Field
      Year: Field
      /// A4 in Hz, as text so a half-typed value never snaps back.
      TuningPitch: string
      Charter: string
      AlbumArt: string option }

type AnchorInput = { Bar: string; AudioSeconds: string }

type SyncModel =
    { OffsetMs: string
      TempoScale: string
      /// Show the anchor table instead of offset + scale.
      Advanced: bool
      Anchors: SyncAnchor list
      NewAnchor: AnchorInput }

type OutputModel =
    { PC: bool
      Mac: bool
      Dir: string option
      LevelCount: LevelCountGeneration }

type BuildState =
    | BuildIdle
    | BuildRunning of Progress
    | BuildSucceeded of BuildOutput
    | BuildFailed of BuildFailure

type Model =
    { Deps: DepsState
      Tab: TabState
      Audio: AudioState
      /// GP track index -> role. Absent means "not included".
      Roles: Map<int, ArrangementRole>
      /// GP track index -> tone key.
      Tones: Map<int, string>
      Meta: Meta
      Sync: SyncModel
      Output: OutputModel
      Build: BuildState
      /// Newest first.
      Log: string list }

type Msg =
    | DepsChecked of Dependencies
    | RecheckDeps
    | PickTab
    | TabPicked of string option
    | TabLoadFinished of path: string * Result<TabScore, TabLoadError>
    | PickAudio
    | AudioPicked of string option
    | AudioProbeFinished of path: string * Result<AudioInfo, string>
    | FilesDropped of string list
    | SetRole of trackIndex: int * ArrangementRole option
    | SetTone of trackIndex: int * toneKey: string
    | SetTitle of string
    | SetArtist of string
    | SetAlbum of string
    | SetYear of string
    | SetTuningPitch of string
    | SetCharter of string
    | PickAlbumArt
    | AlbumArtPicked of string option
    | SetOffset of string
    | SetScale of string
    | ToggleAdvancedSync
    | SetNewAnchorBar of string
    | SetNewAnchorSeconds of string
    | AddAnchor
    | RemoveAnchor of int64<tick>
    | SeedAnchorsFromSource
    | ClearAnchors
    | TogglePC
    | ToggleMac
    | PickOutputDir
    | OutputDirPicked of string option
    | SetLevelCount of LevelCountGeneration
    | StartBuild
    | BuildProgressed of Progress
    | BuildFinished of Result<BuildOutput, BuildFailure>
    | Reveal of string
    | Log of string
    | Nop

let initialModel : Model =
    { Deps = DepsChecking
      Tab = NoTab
      Audio = NoAudio
      Roles = Map.empty
      Tones = Map.empty
      Meta =
        { Title = Field.empty; Artist = Field.empty; Album = Field.empty; Year = Field.empty
          TuningPitch = "440"; Charter = System.Environment.UserName; AlbumArt = None }
      Sync = { OffsetMs = "0"; TempoScale = "1.0"; Advanced = false; Anchors = []; NewAnchor = { Bar = ""; AudioSeconds = "" } }
      Output = { PC = true; Mac = true; Dir = None; LevelCount = LevelCountGeneration.Simple }
      Build = BuildIdle
      Log = [] }
