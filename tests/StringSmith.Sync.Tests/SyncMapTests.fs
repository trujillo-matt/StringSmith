module StringSmith.Sync.Tests.SyncMapTests

open Expecto
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Sync.Tests.Scores

let close (a: float<ms>) (b: float<ms>) msg = Expect.floatClose Accuracy.medium (float a) (float b) msg
let s120 = constant 120.0 8 // 16 000 ms nominal
let nominal = TempoMap.nominalMs s120

[<Tests>]
let tests =
    testList "SyncMap" [
        test "empty map is the identity on nominal time and reports NotSynced" {
            let f = SyncMap.toAudioMs nominal SyncMap.empty
            close (f 960L<tick>) 500.0<ms> "identity"
            Expect.equal (SyncMap.status SyncMap.empty) NotSynced "status"
        }

        test "one anchor is a constant offset" {
            let m = SyncMap.create [ { Tick = 0L<tick>; AudioMs = 1500.0<ms> } ]
            let f = SyncMap.toAudioMs nominal m
            close (f 0L<tick>) 1500.0<ms> "start"
            close (f barTicks) 3500.0<ms> "bar 0 + offset"
            Expect.equal (SyncMap.status m) OffsetOnly "status"
        }

        test "ofOffsetAndScale stretches the whole tab" {
            // Tab says 120 bpm; recording is actually 118 bpm, so it is longer by 120/118.
            let scale = 120.0 / 118.0
            let m = SyncMap.ofOffsetAndScale nominal s120.EndTick 250.0<ms> scale
            let f = SyncMap.toAudioMs nominal m
            close (f 0L<tick>) 250.0<ms> "offset at start"
            close (f s120.EndTick) (250.0<ms> + 16000.0<ms> * scale) "stretched end"
            close (f (4L * barTicks)) (250.0<ms> + 8000.0<ms> * scale) "midpoint scales linearly"
            Expect.equal (SyncMap.status m) OffsetAndScale "status"
        }

        test "the integer-BPM drift case from the plan: 95 vs 94.3 over ~4 minutes" {
            // ~377 beats of 4/4 at 95 bpm nominal
            let s = constant 95.0 95
            let n = TempoMap.nominalMs s
            let endMs = n s.EndTick
            // unsynced: tab places the end at nominal
            let unsynced = SyncMap.toAudioMs n SyncMap.empty s.EndTick
            // truth: the recording is at 94.3, so it ends later by 95/94.3
            let truthEnd = endMs * (95.0 / 94.3)
            let drift = truthEnd - unsynced
            Expect.isGreaterThan (float drift) 1500.0 "well over a second of drift with offset-only sync"
            // two anchors fix it exactly
            let m = SyncMap.create [ { Tick = 0L<tick>; AudioMs = 0.0<ms> }; { Tick = s.EndTick; AudioMs = truthEnd } ]
            close (SyncMap.toAudioMs n m s.EndTick) truthEnd "end anchored"
        }

        test "anchors map exactly to their audio times" {
            let anchors =
                [ { Tick = 0L<tick>; AudioMs = 100.0<ms> }
                  { Tick = 2L * barTicks; AudioMs = 4300.0<ms> }
                  { Tick = 5L * barTicks; AudioMs = 10900.0<ms> }
                  { Tick = 8L * barTicks; AudioMs = 16800.0<ms> } ]
            let m = SyncMap.create anchors
            let f = SyncMap.toAudioMs nominal m
            for a in anchors do
                close (f a.Tick) a.AudioMs $"anchor at {a.Tick}"
            Expect.equal (SyncMap.status m) (Anchored 4) "status"
        }

        test "between anchors the tempo map's shape is preserved, only stretched" {
            // halving: 120 then 60. Anchor the start, the tempo change, and the end,
            // with the slow half stretched 10% and the fast half exact.
            let n = TempoMap.nominalMs halving
            let m =
                SyncMap.create
                    [ { Tick = 0L<tick>; AudioMs = 0.0<ms> }
                      { Tick = 4L * barTicks; AudioMs = 8000.0<ms> }
                      { Tick = 8L * barTicks; AudioMs = 8000.0<ms> + 16000.0<ms> * 1.1 } ]
            let f = SyncMap.toAudioMs n m
            close (f (2L * barTicks)) 4000.0<ms> "fast half unchanged"
            close (f (6L * barTicks)) (8000.0<ms> + 8000.0<ms> * 1.1) "slow half stretched by 1.1"
        }

        test "extrapolation past the last anchor uses the last segment's stretch" {
            let m = SyncMap.create [ { Tick = 0L<tick>; AudioMs = 0.0<ms> }; { Tick = barTicks; AudioMs = 4000.0<ms> } ] // 2x
            let f = SyncMap.toAudioMs nominal m
            close (f (2L * barTicks)) 8000.0<ms> "doubled beyond the anchors"
        }

        test "duplicate-tick anchors collapse and order does not matter" {
            let a = SyncMap.create [ { Tick = barTicks; AudioMs = 1.0<ms> }; { Tick = 0L<tick>; AudioMs = 0.0<ms> }; { Tick = barTicks; AudioMs = 99.0<ms> } ]
            Expect.equal a.Anchors.Length 2 "two distinct ticks"
            Expect.equal (a.Anchors |> List.map (fun x -> x.Tick)) [ 0L<tick>; barTicks ] "sorted"
        }

        test "add replaces an anchor at the same tick; remove drops it" {
            let m = SyncMap.create [ { Tick = 0L<tick>; AudioMs = 0.0<ms> } ] |> SyncMap.add { Tick = 0L<tick>; AudioMs = 50.0<ms> }
            Expect.equal m.Anchors [ { Tick = 0L<tick>; AudioMs = 50.0<ms> } ] "replaced"
            Expect.equal (SyncMap.remove 0L<tick> m) SyncMap.empty "removed"
        }

        test "ofSourceSyncPoints seeds from the score" {
            let s = { s120 with SourceSyncPoints = [ 0L<tick>, 10.0<ms>; barTicks, 2010.0<ms> ] }
            let m = SyncMap.ofSourceSyncPoints s
            Expect.equal (SyncMap.status m) OffsetAndScale "two seeded anchors"
        }

        testProperty "monotonic for monotonic anchors" <| fun (offsets: uint16 list) ->
            // build strictly increasing anchors across the score
            let steps = offsets |> List.truncate 6 |> List.map (fun o -> float o + 1.0)
            let anchors =
                steps
                |> List.scan (fun (t, a) step -> (t + barTicks, a + step * 1.0<ms>)) (0L<tick>, 0.0<ms>)
                |> List.map (fun (t, a) -> { Tick = t; AudioMs = a })
            let f = SyncMap.toAudioMs nominal (SyncMap.create anchors)
            let ticks = [ 0L .. 480L .. 8L * 3840L ] |> List.map (fun t -> t * 1L<tick>)
            ticks |> List.pairwise |> List.forall (fun (a, b) -> f a <= f b)
    ]

[<Tests>]
let drift =
    testList "DriftReport" [
        test "unsynced tab against a longer recording reports the mismatch and warns" {
            let r = DriftReport.create s120 SyncMap.empty 18000.0<ms>
            Expect.equal r.Status NotSynced "status"
            close r.TabEndAudioMs 16000.0<ms> "tab end"
            close r.MismatchMs -2000.0<ms> "tab ends 2 s early"
            Expect.isTrue (DriftReport.isWarning r) "warns"
        }

        test "NotSynced always warns even with a perfect length match" {
            let r = DriftReport.create s120 SyncMap.empty 16000.0<ms>
            Expect.isTrue (DriftReport.isWarning r) "never green on defaults"
        }

        test "two anchors with a small mismatch does not warn" {
            let m = SyncMap.create [ { Tick = 0L<tick>; AudioMs = 0.0<ms> }; { Tick = s120.EndTick; AudioMs = 16400.0<ms> } ]
            let r = DriftReport.create s120 m 16000.0<ms>
            Expect.equal r.Status OffsetAndScale "status"
            Expect.isFalse (DriftReport.isWarning r) "400 ms mismatch under threshold"
        }

        test "ramped bars are reported" {
            let r = DriftReport.create halving SyncMap.empty 24000.0<ms>
            Expect.equal (r.RampedBars |> List.map (fun b -> b.Index)) [ 4 ] "bar 4"
        }
    ]
