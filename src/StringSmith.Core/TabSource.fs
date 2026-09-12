namespace StringSmith.Core

/// Why a tab could not be loaded. Kept as a closed set so the UI can phrase each one.
type TabLoadError =
    | FileNotFound of path: string
    | UnsupportedFormat of path: string * detail: string
    | CorruptFile of path: string * detail: string
    | NoPlayableTracks of path: string

/// The producer seam.
///
/// StringSmith.GuitarPro implements this over AlphaTab. A future audio-transcription
/// sidecar (Basic Pitch, librosa, Demucs) would implement it over a process boundary
/// with a serialised TabScore. Everything downstream, Sync, Conversion, Pipeline,
/// consumes TabScore and must never reference a producer's own types.
type ITabSource =
    abstract Load : path: string -> Result<TabScore, TabLoadError>
