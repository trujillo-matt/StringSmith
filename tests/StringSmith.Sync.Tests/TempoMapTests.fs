module StringSmith.Sync.Tests.TempoMapTests

open Expecto
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Sync.Tests.Scores

let close (a: float<ms>) (b: float<ms>) msg = Expect.floatClose Accuracy.high (float a) (float b) msg

[<Tests>]
let tests =
    testList "TempoMap.nominalMs" [
        test "120 bpm: one quarter is 500 ms, one 4/4 bar is 2000 ms" {
            let f = TempoMap.nominalMs (constant 120.0 4)
            close (f 0L<tick>) 0.0<ms> "start"
            close (f 960L<tick>) 500.0<ms> "quarter"
            close (f barTicks) 2000.0<ms> "bar"
            close (f (4L * barTicks)) 8000.0<ms> "four bars"
        }

        test "95 bpm matches the Nightwish spike arithmetic" {
            let f = TempoMap.nominalMs (constant 95.0 1)
            // 60000 / 95 = 631.578... ms per quarter
            close (f 960L<tick>) (60000.0 / 95.0 * 1.0<ms>) "quarter at 95"
        }

        test "a tempo step is integrated piecewise, not averaged" {
            let f = TempoMap.nominalMs halving
            // 4 bars at 120 = 8000 ms; then each bar at 60 = 4000 ms
            close (f (4L * barTicks)) 8000.0<ms> "end of fast half"
            close (f (5L * barTicks)) 12000.0<ms> "one slow bar"
            close (f (8L * barTicks)) 24000.0<ms> "end"
        }

        test "a step mid-bar is honoured at its exact tick" {
            let s =
                score
                    [ { Tick = 0L<tick>; Bpm = 120.0; IsLinearRamp = false }
                      { Tick = 960L<tick>; Bpm = 240.0; IsLinearRamp = false } ]
                    1
            let f = TempoMap.nominalMs s
            close (f 960L<tick>) 500.0<ms> "before the step"
            close (f 1920L<tick>) 750.0<ms> "one quarter at 240 = 250 ms"
        }

        test "missing tick-0 event falls back to the initial tempo" {
            let s = score [ { Tick = barTicks; Bpm = 60.0; IsLinearRamp = false } ] 2
            let f = TempoMap.nominalMs s
            // InitialBpm is the first event's bpm (60) since that's all the map has.
            close (f barTicks) 4000.0<ms> "bar 0 at 60"
        }

        test "negative or zero ticks map to zero" {
            let f = TempoMap.nominalMs (constant 120.0 1)
            close (f -500L<tick>) 0.0<ms> "negative"
        }

        test "non-positive bpm does not divide by zero" {
            let s = score [ { Tick = 0L<tick>; Bpm = 0.0; IsLinearRamp = false } ] 1
            let f = TempoMap.nominalMs s
            Expect.isTrue (System.Double.IsFinite(float (f barTicks))) "finite"
        }

        test "rampedBars names only the bars that carry a ramp flag" {
            let ramped = TempoMap.rampedBars halving
            Expect.equal (ramped |> List.map (fun b -> b.Index)) [ 4 ] "bar 4 carries the ramped event"
            Expect.isEmpty (TempoMap.rampedBars (constant 120.0 4)) "none when no ramps"
        }

        testProperty "monotonic non-decreasing in tick" <| fun (a: int) (b: int) ->
            let f = TempoMap.nominalMs halving
            let ta, tb = int64 (abs a % 100000) * 1L<tick>, int64 (abs b % 100000) * 1L<tick>
            let lo, hi = min ta tb, max ta tb
            f lo <= f hi
    ]
