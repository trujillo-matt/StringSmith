namespace StringSmith.Sync

open StringSmith.Core

/// One user- or file-supplied correspondence: "tab tick T happens at audio time A".
type SyncAnchor = { Tick: int64<tick>; AudioMs: float<ms> }

/// How much the user has told us. Never reported as better than it is.
type SyncStatus =
    /// Default: nominal time is used unchanged. The chart is NOT aligned to the recording.
    | NotSynced
    /// One anchor: a constant start offset. Integer-BPM drift is uncorrected.
    | OffsetOnly
    /// Two anchors: offset plus global tempo scale. Corrects rounded BPM; not human drift.
    | OffsetAndScale
    /// Three or more anchors: piecewise correction.
    | Anchored of anchorCount: int

/// The sync model. One representation; the simple "offset + tempo scale" controls in the
/// UI are a two-anchor projection of it, so there is no rework when anchors arrive.
///
/// Mapping is a piecewise-linear WARP OF NOMINAL TIME, not of ticks: between two anchors
/// the tempo map's own shape is preserved and merely stretched to fit, so a tempo change
/// between anchors is still honoured. Outside the anchors the nearest segment's stretch is
/// extrapolated.
type SyncMap = { Anchors: SyncAnchor list }

module SyncMap =

    /// No anchors: audio time equals nominal time. The honest default.
    let empty : SyncMap = { Anchors = [] }

    let private normalise (anchors: SyncAnchor list) =
        anchors |> List.distinctBy (fun a -> a.Tick) |> List.sortBy (fun a -> a.Tick)

    let create (anchors: SyncAnchor list) : SyncMap = { Anchors = normalise anchors }

    let add (anchor: SyncAnchor) (map: SyncMap) : SyncMap =
        create (anchor :: (map.Anchors |> List.filter (fun a -> a.Tick <> anchor.Tick)))

    let remove (tick: int64<tick>) (map: SyncMap) : SyncMap =
        { Anchors = map.Anchors |> List.filter (fun a -> a.Tick <> tick) }

    let status (map: SyncMap) : SyncStatus =
        match map.Anchors.Length with
        | 0 -> NotSynced
        | 1 -> OffsetOnly
        | 2 -> OffsetAndScale
        | n -> Anchored n

    /// The two-anchor projection: tick 0 lands at offsetMs, and the nominal length is
    /// multiplied by tempoScale. tempoScale 1.0 means "the tab's BPM is exactly right".
    let ofOffsetAndScale (nominal: int64<tick> -> float<ms>) (endTick: int64<tick>) (offsetMs: float<ms>) (tempoScale: float) : SyncMap =
        let endNominal = nominal endTick
        create
            [ { Tick = 0L<tick>; AudioMs = offsetMs }
              { Tick = endTick; AudioMs = offsetMs + endNominal * tempoScale } ]

    /// Seeds anchors from sync points the source file itself carried (GP7/GP8 backing
    /// track alignment). They are a starting point the user can edit, not a verdict.
    let ofSourceSyncPoints (score: TabScore) : SyncMap =
        create (score.SourceSyncPoints |> List.map (fun (t, a) -> { Tick = t; AudioMs = a }))

    /// Maps a tick to audio time.
    let toAudioMs (nominal: int64<tick> -> float<ms>) (map: SyncMap) : int64<tick> -> float<ms> =
        match map.Anchors with
        | [] -> nominal
        | [ single ] ->
            // constant offset
            let delta = single.AudioMs - nominal single.Tick
            fun t -> nominal t + delta
        | anchors ->
            let arr = anchors |> Array.ofList
            let nominalAt = arr |> Array.map (fun a -> nominal a.Tick)
            // slope of segment i (between anchor i and i+1) in audio-ms per nominal-ms
            let slope i =
                let dn = nominalAt.[i + 1] - nominalAt.[i]
                let da = arr.[i + 1].AudioMs - arr.[i].AudioMs
                if dn <= 0.0<ms> then 1.0 else float (da / dn)
            fun (t: int64<tick>) ->
                let n = nominal t
                // find segment: last anchor whose nominal <= n, clamped to [0, len-2]
                let mutable i = 0
                while i < arr.Length - 2 && nominalAt.[i + 1] <= n do
                    i <- i + 1
                arr.[i].AudioMs + (n - nominalAt.[i]) * slope i

/// What the user must be shown before trusting a build.
type DriftReport =
    {
        Status: SyncStatus
        /// Where the tab's last bar ends, in audio time, under the current map.
        TabEndAudioMs: float<ms>
        /// Length of the user's recording.
        AudioLengthMs: float<ms>
        /// TabEndAudioMs - AudioLengthMs. Positive: tab runs past the audio.
        MismatchMs: float<ms>
        /// Bars carrying linear tempo ramps, where anchors are most needed.
        RampedBars: BarInfo list
    }

module DriftReport =
    /// Threshold above which the mismatch is surfaced as a warning, not just a number.
    let warnThresholdMs = 1000.0<ms>

    let create (score: TabScore) (map: SyncMap) (audioLengthMs: float<ms>) : DriftReport =
        let nominal = TempoMap.nominalMs score
        let toAudio = SyncMap.toAudioMs nominal map
        let tabEnd = toAudio score.EndTick
        { Status = SyncMap.status map
          TabEndAudioMs = tabEnd
          AudioLengthMs = audioLengthMs
          MismatchMs = tabEnd - audioLengthMs
          RampedBars = TempoMap.rampedBars score }

    let isWarning (r: DriftReport) =
        r.Status = NotSynced || abs (float r.MismatchMs) > float warnThresholdMs
