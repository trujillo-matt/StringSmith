module StringSmith.GuitarPro.Tests.LoadTests

open Expecto
open StringSmith.Core
open StringSmith.GuitarPro
open StringSmith.GuitarPro.Tests.Fixtures

let standardGuitar = [ 40; 45; 50; 55; 59; 64 ]

[<Tests>]
let formatCoverage =
    testList "format coverage" [
        for name in [ "accentuations.gp3"; "full-song.gp5"; "full-song.gpx"; "full-song.gp" ] do
            test $"loads {name}" {
                let s = load name
                Expect.isNonEmpty s.Tracks "has tracks"
                Expect.isNonEmpty s.Bars "has bars"
                Expect.equal s.TicksPerQuarter 960 "AlphaTab resolution"
            }

        test "the same song in GP5, GP6 and GP7 agree on structure" {
            let a, b, c = load "full-song.gp5", load "full-song.gpx", load "full-song.gp"
            Expect.equal a.Title "The crow, the owl and the dove" "title"
            Expect.equal a.Artist "Nightwish" "artist"
            Expect.equal a.Album "Imaginaerum" "album"
            Expect.equal b.Title a.Title "gpx title"
            Expect.equal c.Title a.Title "gp title"
            Expect.equal a.Tracks.Length 11 "gp5 tracks"
            Expect.equal b.Tracks.Length 11 "gpx tracks"
            Expect.equal c.Tracks.Length 11 "gp tracks"
            Expect.equal a.Bars.Length 96 "gp5 bars"
            Expect.equal b.Bars.Length 96 "gpx bars"
            Expect.equal c.Bars.Length 96 "gp bars"
        }

        test "missing file is FileNotFound, not an exception" {
            match (AlphaTabSource() :> ITabSource).Load(path "does-not-exist.gp5") with
            | Error(FileNotFound _) -> ()
            | other -> failtestf "expected FileNotFound, got %A" other
        }

        test "garbage bytes never throw and never yield an Ok score" {
            // AlphaTab accepts arbitrary binary as a default EMPTY score rather than
            // rejecting it (verified: zero, sequential and random bytes all parse to
            // 1 track / 1 bar / 0 notes). The producer turns that into NoPlayableTracks;
            // only pure text reaches UnsupportedFormat. Either way: an Error, never Ok.
            for bytes in [ Array.init 512 byte; Array.zeroCreate 512; Array.create 512 65uy ] do
                let tmp = System.IO.Path.GetTempFileName() + ".gp5"
                System.IO.File.WriteAllBytes(tmp, bytes)
                try
                    match (AlphaTabSource() :> ITabSource).Load tmp with
                    | Error(NoPlayableTracks _) | Error(CorruptFile _) | Error(UnsupportedFormat _) -> ()
                    | Ok _ -> failtest "garbage parsed as a usable score"
                    | other -> failtestf "unexpected %A" other
                finally
                    System.IO.File.Delete tmp
        }

        test "chord names come through when the file has chord diagrams" {
            let s = load "chords.gp5"
            let names = s.Tracks |> List.collect (fun t -> t.Beats) |> List.choose (fun b -> b.ChordName)
            Expect.isNonEmpty names "the chords fixture carries diagram names"
        }
    ]

[<Tests>]
let unrolling =
    testList "repeat unrolling" [
        test "|: A B :| C  ->  A B A B C" {
            let s = load "repeat-close.gp5"
            Expect.equal s.Bars.Length 5 "5 occurrences from 3 written bars"
            Expect.equal (s.Bars |> List.map (fun b -> b.SourceBarIndex)) [ 0; 1; 0; 1; 2 ] "playback order"
            Expect.equal (s.Bars |> List.map (fun b -> b.StartTick)) [ 0L<tick>; 3840L<tick>; 7680L<tick>; 11520L<tick>; 15360L<tick> ] "absolute starts"
        }

        test "x4 repeat plays the section four times" {
            let s = load "repeat-close-multi.gp5"
            Expect.equal s.Bars.Length 9 "9 occurrences"
            Expect.equal (s.Bars |> List.map (fun b -> b.SourceBarIndex)) [ 0; 1; 0; 1; 0; 1; 0; 1; 2 ] "order"
        }

        test "alternate endings route to the right ending per pass" {
            let s = load "repeat-close-alternate-endings.gp5"
            // AlphaTab: bar1 has alt=5 (passes 1 and 3), bar2 alt=2 (pass 2), bar3 closes x4, bar4 after.
            Expect.equal (s.Bars |> List.map (fun b -> b.SourceBarIndex)) [ 0; 1; 0; 2; 3; 0; 1; 0; 4 ] "order"
            Expect.equal s.EndTick 8640L<tick> "end tick"
        }

        test "beats in a repeated bar appear once per pass at shifted ticks" {
            let s = load "repeat-close.gp5"
            let t = firstStringed s
            let barDuration = 3840L<tick>
            let firstPass = t.Beats |> List.filter (fun b -> b.StartTick < barDuration) |> List.map (fun b -> b.StartTick)
            let thirdOcc = t.Beats |> List.filter (fun b -> b.StartTick >= 2L * barDuration && b.StartTick < 3L * barDuration) |> List.map (fun b -> b.StartTick - 2L * barDuration)
            Expect.isNonEmpty firstPass "written bar 0 has beats"
            Expect.equal thirdOcc firstPass "occurrence 2 of bar 0 repeats occurrence 0 exactly"
        }
    ]

[<Tests>]
let tempo =
    testList "tempo map" [
        test "initial tempo is read" {
            Expect.equal (load "full-song.gp5").InitialBpm 95.0 "Nightwish is 95 bpm"
        }

        test "consecutive duplicate tempo events are collapsed" {
            let s = load "repeat-close.gp5"
            let dupes = s.TempoMap |> List.pairwise |> List.filter (fun (a, b) -> a.Tick = b.Tick && a.Bpm = b.Bpm)
            Expect.isEmpty dupes "no adjacent duplicates"
        }

        test "linear ramps are stepped (matching AlphaTab) and flagged" {
            let s = load "full-song.gp5"
            let ramped = s.TempoMap |> List.filter (fun e -> e.IsLinearRamp)
            Expect.isNonEmpty ramped "the file ramps across bars 85-94"
            // bar 88 has an automation at ratioPosition 0.625: 337920 + 3840*0.625 = 340320
            Expect.contains (s.TempoMap |> List.map (fun e -> e.Tick, e.Bpm)) (340320L<tick>, 85.0) "mid-bar step at the automation position"
            Expect.contains (s.TempoMap |> List.map (fun e -> e.Tick, e.Bpm)) (357120L<tick>, 60.0) "bar 93 steps to 60"
        }
    ]

[<Tests>]
let tuningAndStrings =
    testList "tuning and strings" [
        test "standard tuning is low-E first as MIDI notes" {
            let s = load "full-song.gp5"
            let t = s.Tracks |> List.find (fun t -> t.Name = "Anette voice")
            Expect.equal t.TuningMidi standardGuitar "reversed from AlphaTab's high-first list"
        }

        test "drop-C style tuning survives" {
            let s = load "full-song.gp5"
            let t = s.Tracks |> List.find (fun t -> t.Name = "Emppu(Disto)")
            Expect.equal t.TuningMidi [ 36; 43; 48; 53; 57; 62 ] "C G C F A D"
        }

        test "4-string bass has four strings" {
            let s = load "full-song.gp5"
            let t = s.Tracks |> List.find (fun t -> t.Name = "Marco")
            Expect.equal t.StringCount 4 "bass"
            Expect.equal t.TuningMidi [ 24; 31; 36; 41 ] "low first"
        }

        test "percussion track has no tuning and no beats but keeps its name" {
            let s = load "full-song.gp5"
            let t = s.Tracks |> List.find (fun t -> t.IsPercussion)
            Expect.equal t.Name "Jukka" "name kept for the track list"
            Expect.isEmpty t.TuningMidi "no tuning"
            Expect.isEmpty t.Beats "no beats"
        }

        test "strings are 0-based from the lowest string and fret+tuning = pitch" {
            let s = load "full-song.gp5"
            let t = s.Tracks |> List.find (fun t -> t.Name = "Anette voice")
            let notes = allNotes t
            Expect.isNonEmpty notes "has notes"
            for n in notes do
                Expect.isTrue (n.String >= 0 && n.String < t.StringCount) $"string {n.String} in range"
            // Spike observation: first note was str=5 fret=8 realVal=67 in AlphaTab's 1-based scheme.
            let first = notes.Head
            Expect.equal (first.String, first.Fret) (4, 8) "B string, 8th fret"
            Expect.equal (t.TuningMidi.[first.String] + first.Fret) 67 "G4"
        }

        test "MIDI program is exposed for role suggestion" {
            let s = load "full-song.gp5"
            let voice = s.Tracks |> List.find (fun t -> t.Name = "Anette voice")
            Expect.equal voice.MidiProgram 73 "a flute patch on a guitar staff: the tab-quality smell"
        }
    ]

[<Tests>]
let techniques =
    testList "technique extraction" [
        test "bends arrive in semitones with 0..1 offsets" {
            let s = load "bends.gp5"
            let notes = allNotes (firstStringed s) |> List.filter (fun n -> not n.BendPoints.IsEmpty)
            Expect.isNonEmpty notes "bend notes"
            let first = notes.Head
            // Spike: first note bendType=Bend, 2 points, max 4 quarter-tones = 2 semitones.
            Expect.equal first.BendPoints.Length 2 "two points"
            Expect.equal (first.BendPoints |> List.map (fun p -> p.Semitones) |> List.max) 2.0 "whole-tone bend"
            for n in notes do
                for p in n.BendPoints do
                    Expect.isTrue (p.Offset >= 0.0 && p.Offset <= 1.0) "offset normalised"
                    Expect.isTrue (p.Semitones >= 0.0 && p.Semitones <= 6.0) "AlphaTab max is 12 quarter-tones"
            let maxAll = notes |> List.collect (fun n -> n.BendPoints) |> List.map (fun p -> p.Semitones) |> List.max
            Expect.equal maxAll 6.0 "the fixture exercises the maximum bend"
        }

        test "slide types and targets" {
            let s = load "slides.gp5"
            let notes = allNotes (firstStringed s)
            let legato = notes |> List.filter (fun n -> n.SlideOut = Legato)
            let shift = notes |> List.filter (fun n -> n.SlideOut = Shift)
            Expect.isNonEmpty legato "legato slides"
            Expect.isNonEmpty shift "shift slides"
            for n in legato @ shift do
                Expect.isSome n.SlideTargetFret "pitched slides know their destination"
            // Spike: first note str=5(1-based) fret=1 legato -> fret 2.
            Expect.equal (legato.Head.Fret, legato.Head.SlideTargetFret) (1, Some 2) "1 -> 2"
        }

        test "harmonic kinds" {
            let s = load "harmonic-types.gp5"
            let kinds = allNotes (firstStringed s) |> List.map (fun n -> n.Harmonic)
            for k in [ Natural; Artificial; Tap; Pinch; Semi ] do
                Expect.contains kinds k $"%A{k}"
            let art = allNotes (firstStringed s) |> List.find (fun n -> n.Harmonic = Artificial)
            Expect.equal art.HarmonicFret (Some 17.0) "artificial harmonic fret from spike"
            let nat = allNotes (firstStringed s) |> List.find (fun n -> n.Harmonic = Natural)
            Expect.isNone nat.HarmonicFret "natural harmonics carry no touch fret"
        }

        test "vibrato and ties" {
            let s = load "full-song.gp5"
            let notes = allNotes (s.Tracks |> List.find (fun t -> t.Name = "Anette voice"))
            Expect.exists notes (fun n -> n.Vibrato = Slight) "spike saw vib=Slight"
            Expect.exists notes (fun n -> n.Techniques.HasFlag Technique.TieOrigin) "tie origin"
            Expect.exists notes (fun n -> n.Techniques.HasFlag Technique.TieDestination) "tie destination"
        }

        test "fingering is never fabricated" {
            let s = load "full-song.gp5"
            let notes = s.Tracks |> List.collect allNotes
            Expect.isTrue (notes |> List.forall (fun n -> n.LeftHandFinger.IsNone)) "community file carries no fingering"
        }

        test "trill fret is only set when the note trills" {
            let s = load "full-song.gp5"
            let notes = s.Tracks |> List.collect allNotes
            // The sentinel (-60 etc.) must never leak through as a fret.
            Expect.isTrue (notes |> List.forall (fun n -> n.TrillFret |> Option.forall (fun f -> f >= 0 && f <= 30))) "no sentinels"
        }
    ]

[<Tests>]
let syncPoints =
    testList "native sync points" [
        test "GP7 file with embedded sync points exposes them as tick/ms pairs" {
            let s = load "syncpoints-testfile.gp"
            Expect.isNonEmpty s.SourceSyncPoints "the fixture carries sync points"
            let ticks = s.SourceSyncPoints |> List.map fst
            Expect.equal ticks (List.sort ticks) "monotonic in tick"
        }

        test "ordinary files have none" {
            Expect.isEmpty (load "full-song.gp5").SourceSyncPoints "no sync points"
        }
    ]
