/// Hand-built TabScores. No Guitar Pro files needed: that is the point of the Core seam.
module StringSmith.Sync.Tests.Scores

open StringSmith.Core

let tpq = 960
let barTicks = 4L * int64 tpq * 1L<tick> // 4/4

let bars n =
    [ for i in 0 .. n - 1 ->
        { Index = i; SourceBarIndex = i; StartTick = int64 i * barTicks
          TimeSigNumerator = 4; TimeSigDenominator = 4; IsAnacrusis = false } ]

let score (tempo: TempoEvent list) (nBars: int) : TabScore =
    { Title = "t"; Artist = "a"; Album = "al"; Year = None
      TicksPerQuarter = tpq
      TempoMap = tempo
      Bars = bars nBars
      Tracks = []
      SourceSyncPoints = [] }

let constant bpm nBars = score [ { Tick = 0L<tick>; Bpm = bpm; IsLinearRamp = false } ] nBars

/// 120 for 4 bars then 60 for 4 bars.
let halving =
    score
        [ { Tick = 0L<tick>; Bpm = 120.0; IsLinearRamp = false }
          { Tick = 4L * barTicks; Bpm = 60.0; IsLinearRamp = true } ]
        8
