namespace StringSmith.Sync

open StringSmith.Core

/// Nominal time: what the tab CLAIMS the timing is, with no reference to any recording.
/// Ticks are integrated through the stepped tempo map exactly as AlphaTab's own playback
/// does (one tempo per automation at its position, no linear interpolation), so that a
/// tab previewed in any AlphaTab-based player and a tab built here agree to the tick.
module TempoMap =

    /// A resolved tempo segment: from StartTick, at Bpm, with the nominal ms at StartTick.
    type private Segment = { StartTick: int64<tick>; Bpm: float; StartMs: float<ms> }

    let private msPerTick (bpm: float) (ticksPerQuarter: int) : float<ms/tick> =
        // one quarter note = 60000 / bpm ms, spread across ticksPerQuarter ticks
        (60000.0 / (bpm * float ticksPerQuarter)) * 1.0<ms/tick>

    /// Builds a tick -> nominal ms function for the score.
    /// Before the first event, the score's initial tempo applies. Events must be sorted
    /// by tick, which the producer guarantees. A tempo of <= 0 is treated as the initial
    /// tempo rather than dividing by zero; a community file can carry nonsense.
    let nominalMs (score: TabScore) : int64<tick> -> float<ms> =
        let tpq = score.TicksPerQuarter
        let initial = if score.InitialBpm > 0.0 then score.InitialBpm else 120.0

        let events =
            score.TempoMap
            |> List.filter (fun e -> e.Tick >= 0L<tick>)
            |> List.map (fun e -> e.Tick, (if e.Bpm > 0.0 then e.Bpm else initial))
            |> List.sortBy fst

        // Ensure a segment exists at tick 0.
        let events =
            match events with
            | (t, _) :: _ when t = 0L<tick> -> events
            | _ -> (0L<tick>, initial) :: events

        // Resolve the cumulative ms at each segment start.
        let segments =
            events
            |> List.fold
                (fun (acc: Segment list) (tick, bpm) ->
                    match acc with
                    | [] -> [ { StartTick = tick; Bpm = bpm; StartMs = 0.0<ms> } ]
                    | prev :: _ ->
                        let elapsed = float (tick - prev.StartTick) * 1.0<tick> * msPerTick prev.Bpm tpq
                        { StartTick = tick; Bpm = bpm; StartMs = prev.StartMs + elapsed } :: acc)
                []
            |> List.rev
            |> Array.ofList

        fun (t: int64<tick>) ->
            if t <= 0L<tick> then
                0.0<ms>
            else
                // last segment whose StartTick <= t (binary search over a sorted array)
                let mutable lo = 0
                let mutable hi = segments.Length - 1
                while lo < hi do
                    let mid = (lo + hi + 1) / 2
                    if segments.[mid].StartTick <= t then lo <- mid else hi <- mid - 1
                let seg = segments.[lo]
                seg.StartMs + float (t - seg.StartTick) * 1.0<tick> * msPerTick seg.Bpm tpq

    /// Bars whose tempo events are flagged as linear ramps. These are the bars where the
    /// stepped approximation is most likely to disagree with a human performance, so the
    /// UI recommends anchors there.
    let rampedBars (score: TabScore) : BarInfo list =
        let rampTicks = score.TempoMap |> List.filter (fun e -> e.IsLinearRamp) |> List.map (fun e -> e.Tick)
        if rampTicks.IsEmpty then []
        else
            let ends =
                score.Bars
                |> List.pairwise
                |> List.map (fun (a, b) -> a, b.StartTick)
                |> fun pairs -> pairs @ [ List.last score.Bars, score.EndTick ]
            ends
            |> List.filter (fun (bar, endTick) -> rampTicks |> List.exists (fun t -> t >= bar.StartTick && t < endTick))
            |> List.map fst
