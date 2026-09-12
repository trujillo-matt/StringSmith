module StringSmith.Conversion.Tests.ConvertTests

open System.IO
open Expecto
open Rocksmith2014.XML
open StringSmith.Core
open StringSmith.Conversion
open StringSmith.Conversion.Tests.Builders

[<Tests>]
let ebeats =
    testList "ebeats" [
        test "4/4 bars give four beats each, downbeats numbered from 1, weak beats -1" {
            let s = score 2 (track standard [ beat 0L<tick> q [ note 0 3 ] ])
            let r = convert Lead s
            let eb = r.Arrangement.Ebeats |> List.ofSeq
            Expect.equal eb.Length 8 "eight beats"
            Expect.equal (eb |> List.map (fun e -> e.Measure)) [ 1s; -1s; -1s; -1s; 2s; -1s; -1s; -1s ] "measure markers"
            Expect.equal (eb |> List.map (fun e -> e.Time)) [ 1000; 1500; 2000; 2500; 3000; 3500; 4000; 4500 ] "500 ms apart from the 1000 ms offset"
            Expect.equal r.Arrangement.StartBeat 1000 "start beat is the first ebeat"
        }

        test "6/8 gives six beats" {
            let s = score 1 (track standard [ beat 0L<tick> q [ note 0 3 ] ])
            let s = { s with Bars = s.Bars |> List.map (fun b -> { b with TimeSigNumerator = 6; TimeSigDenominator = 8 }) }
            let r = convert Lead s
            Expect.equal r.Arrangement.Ebeats.Count 6 "six eighth-note beats"
        }
    ]

[<Tests>]
let notes =
    testList "single notes" [
        test "a short note has no sustain; a long one keeps its duration" {
            let s = score 2 (track standard [ beat 0L<tick> (q / 2L) [ note 2 5 ]; beat q (2L * q) [ note 2 7 ] ])
            let l = level (convert Lead s)
            Expect.equal l.Notes.Count 2 "two notes"
            let short, long = l.Notes.[0], l.Notes.[1]
            Expect.equal (int short.String, int short.Fret, short.Time, short.Sustain) (2, 5, 1000, 0) "eighth note: no sustain"
            Expect.equal (int long.String, int long.Fret, long.Time, long.Sustain) (2, 7, 1500, 1000) "half note: 1000 ms"
        }

        test "techniques map to Rocksmith masks" {
            let n =
                [ note 0 5 |> withTech Technique.HammerOn
                  note 0 3 |> withTech Technique.PullOff
                  note 1 5 |> withTech Technique.PalmMute
                  note 2 5 |> withTech Technique.Dead
                  note 3 5 |> withTech Technique.Accent ]
            let beats = n |> List.mapi (fun i x -> beat (int64 i * q) q [ x ])
            let l = level (convert Lead (score 2 (track standard beats)))
            let ns = l.Notes |> List.ofSeq
            Expect.isTrue ns.[0].IsHammerOn "hammer-on"
            Expect.isTrue ns.[1].IsPullOff "pull-off"
            Expect.isTrue ns.[2].IsPalmMute "palm mute"
            Expect.isTrue ns.[3].IsFretHandMute "dead"
            Expect.isTrue ns.[4].IsAccent "accent"
        }

        test "harmonics: natural is harmonic, pinch is pinch" {
            let beats = [ beat 0L<tick> q [ { note 0 12 with Harmonic = Natural } ]; beat q q [ { note 0 12 with Harmonic = Pinch } ] ]
            let l = level (convert Lead (score 1 (track standard beats)))
            Expect.isTrue l.Notes.[0].IsHarmonic "natural"
            Expect.isTrue l.Notes.[1].IsPinchHarmonic "pinch"
        }

        test "vibrato forces sustain and sets the speed byte" {
            let l = level (convert Lead (score 1 (track standard [ beat 0L<tick> (q / 4L) [ { note 0 7 with Vibrato = Wide } ] ])))
            Expect.equal l.Notes.[0].Vibrato 120uy "wide"
            Expect.isGreaterThan l.Notes.[0].Sustain 0 "a sixteenth still gets sustain because vibrato needs it"
        }

        test "fingering passes through or stays -1" {
            let beats = [ beat 0L<tick> q [ { note 0 5 with LeftHandFinger = Some 2 } ]; beat q q [ note 0 5 ] ]
            let r = convert Lead (score 1 (track standard beats))
            let l = level r
            Expect.equal l.Notes.[0].LeftHand 2y "given"
            Expect.equal l.Notes.[1].LeftHand -1y "unknown"
            Expect.isFalse (r.Warnings |> List.contains NoFingeringInSource) "some fingering existed"
            let r2 = convert Lead (score 1 (track standard [ beat q q [ note 0 5 ] ]))
            Expect.contains r2.Warnings NoFingeringInSource "none at all: warned"
        }
    ]

[<Tests>]
let slidesAndBends =
    testList "slides and bends" [
        test "legato with an adjacent target becomes slideTo + linkNext" {
            let beats = [ beat 0L<tick> q [ { note 0 5 with SlideOut = Legato; SlideTargetFret = Some 7 } ]; beat q q [ note 0 7 ] ]
            let r = convert Lead (score 1 (track standard beats))
            let n = (level r).Notes.[0]
            Expect.equal n.SlideTo 7y "slide target"
            Expect.isTrue n.IsLinkNext "linked: next note on the string starts at time + sustain"
            Expect.isEmpty (r.Warnings |> List.filter (function LegatoWithoutAdjacentTarget _ -> true | _ -> false)) "no warning"
        }

        test "legato with a gap before the target is a plain slide and warns" {
            let beats = [ beat 0L<tick> q [ { note 0 5 with SlideOut = Legato; SlideTargetFret = Some 7 } ]; beat (3L * q) q [ note 0 7 ] ]
            let r = convert Lead (score 1 (track standard beats))
            let n = (level r).Notes.[0]
            Expect.equal n.SlideTo 7y "still pitched"
            Expect.isFalse n.IsLinkNext "not linked"
            Expect.contains r.Warnings (LegatoWithoutAdjacentTarget 1) "warned"
        }

        test "shift slide is pitched without linkNext" {
            let beats = [ beat 0L<tick> q [ { note 0 5 with SlideOut = Shift; SlideTargetFret = Some 9 } ]; beat q q [ note 0 9 ] ]
            let n = (level (convert Lead (score 1 (track standard beats)))).Notes.[0]
            Expect.equal (n.SlideTo, n.IsLinkNext) (9y, false) "shift"
        }

        test "unpitched slides travel a fixed distance and never below 0" {
            let beats = [ beat 0L<tick> q [ { note 0 7 with SlideOut = OutDown } ]; beat q q [ { note 0 2 with SlideOut = OutDown } ]; beat (2L * q) q [ { note 0 22 with SlideOut = OutUp } ]; beat (3L * q) q [ { note 0 0 with SlideOut = OutDown } ] ]
            let ns = (level (convert Lead (score 1 (track standard beats)))).Notes
            Expect.equal ns.[0].SlideUnpitchTo 3y "7 -> 3"
            Expect.equal ns.[1].SlideUnpitchTo 0y "2 -> 0, clamped"
            Expect.equal ns.[2].SlideUnpitchTo 24y "22 -> 24, clamped"
            Expect.equal ns.[3].SlideUnpitchTo -1y "open string cannot slide down"
        }

        test "bends: semitones, times inside the sustain, maxBend, sustain forced" {
            let pts = [ { Offset = 0.0; Semitones = 0.0 }; { Offset = 0.5; Semitones = 1.0 }; { Offset = 1.0; Semitones = 2.0 } ]
            let l = level (convert Lead (score 1 (track standard [ beat 0L<tick> (q / 4L) [ { note 2 7 with BendPoints = pts } ] ])))
            let n = l.Notes.[0]
            Expect.isTrue n.IsBend "is bend"
            Expect.isGreaterThan n.Sustain 0 "sustain forced for the bend"
            Expect.equal n.MaxBend 2.0f "two semitones"
            match n.BendValues with
            | null -> failtest "bend values missing"
            | bv ->
                for b in bv do
                    Expect.isTrue (b.Time >= n.Time && b.Time <= n.Time + n.Sustain) "inside sustain"
                Expect.equal (bv |> Seq.map (fun b -> b.Step) |> List.ofSeq) [ 0.0f; 1.0f; 2.0f ] "steps"
        }
    ]

[<Tests>]
let chords =
    testList "chords" [
        test "simultaneous notes become one chord with a template, chordNotes and a handshape" {
            let s = score 1 (track standard [ { beat 0L<tick> (2L * q) [ note 0 3; note 1 5; note 2 5 ] with ChordName = Some "C5" } ])
            let r = convert Rhythm s
            let l = level r
            Expect.equal l.Notes.Count 0 "no singles"
            Expect.equal l.Chords.Count 1 "one chord"
            let c = l.Chords.[0]
            Expect.equal (match c.ChordNotes with null -> 0 | cn -> cn.Count) 3 "three chord notes"
            Expect.equal r.Arrangement.ChordTemplates.Count 1 "one template"
            let t = r.Arrangement.ChordTemplates.[0]
            Expect.equal t.Name "C5" "name"
            Expect.equal (List.ofArray t.Frets) [ 3y; 5y; 5y; -1y; -1y; -1y ] "frets per string, -1 unused"
            Expect.equal (List.ofArray t.Fingers) [ -1y; -1y; -1y; -1y; -1y; -1y ] "fingers never fabricated"
            Expect.equal l.HandShapes.Count 1 "handshape"
            let hs = l.HandShapes.[0]
            Expect.equal (hs.ChordId, hs.StartTime) (0s, 1000) "covers the chord"
            Expect.isGreaterThan hs.EndTime hs.StartTime "non-empty"
            Expect.isTrue r.Arrangement.MetaData.ArrangementProperties.PathRhythm "rhythm path"
        }

        test "identical chords share a template; adjacent same-chord handshapes merge" {
            let c = [ note 0 3; note 1 5; note 2 5 ]
            let s = score 1 (track standard [ beat 0L<tick> q c; beat q q c; beat (2L * q) q c ])
            let r = convert Rhythm s
            Expect.equal r.Arrangement.ChordTemplates.Count 1 "shared"
            Expect.equal (level r).Chords.Count 3 "three strums"
            Expect.equal (level r).HandShapes.Count 1 "one merged handshape"
        }

        test "two voices hitting the same string at the same time: first wins, warned" {
            let s = score 1 (track standard [ beat 0L<tick> q [ note 0 3 ]; beat 0L<tick> q [ note 0 5 ] ])
            let r = convert Lead s
            Expect.equal (level r).Notes.Count 1 "one note"
            Expect.equal (level r).Notes.[0].Fret 3y "first"
            Expect.contains r.Warnings (DuplicateStringNotes 1) "warned"
        }

        test "chord-level palm mute only when every note is muted" {
            let s = score 1 (track standard [ beat 0L<tick> q [ note 0 3 |> withTech Technique.PalmMute; note 1 5 |> withTech Technique.PalmMute ]; beat q q [ note 0 3 |> withTech Technique.PalmMute; note 1 5 ] ])
            let l = level (convert Rhythm s)
            Expect.isTrue l.Chords.[0].IsPalmMute "all muted"
            Expect.isFalse l.Chords.[1].IsPalmMute "mixed"
        }
    ]

[<Tests>]
let ties =
    testList "ties" [
        test "a tied continuation extends the previous note and is not emitted" {
            let s = score 2 (track standard [ beat 0L<tick> q [ note 0 5 |> withTech Technique.TieOrigin ]; beat q q [ note 0 5 |> withTech Technique.TieDestination ] ])
            let l = level (convert Lead s)
            Expect.equal l.Notes.Count 1 "merged"
            Expect.equal l.Notes.[0].Sustain 1000 "two quarters"
        }
    ]

[<Tests>]
let anchors =
    testList "anchors" [
        test "notes within a 4-fret span share one anchor; a jump opens a new one" {
            let beats = [ beat 0L<tick> q [ note 0 5 ]; beat q q [ note 1 7 ]; beat (2L * q) q [ note 2 8 ]; beat (3L * q) q [ note 3 12 ] ]
            let l = level (convert Lead (score 1 (track standard beats)))
            let a = l.Anchors |> List.ofSeq
            Expect.equal a.Length 2 "two anchors"
            Expect.equal (a.[0].Fret, a.[0].Time, a.[0].Width) (5y, 1000, 4y) "first at 5"
            Expect.equal (a.[1].Fret, a.[1].Time) (12y, 2500) "jump to 12"
        }

        test "open strings do not move the hand" {
            let beats = [ beat 0L<tick> q [ note 0 5 ]; beat q q [ note 0 0 ]; beat (2L * q) q [ note 0 6 ] ]
            let l = level (convert Lead (score 1 (track standard beats)))
            Expect.equal l.Anchors.Count 1 "one anchor"
        }

        test "a wide chord widens the anchor" {
            let l = level (convert Lead (score 1 (track standard [ beat 0L<tick> q [ note 0 5; note 5 10 ] ])))
            Expect.equal (l.Anchors.[0].Fret, l.Anchors.[0].Width) (5y, 6y) "5..10 is six frets"
        }
    ]

[<Tests>]
let tuningTests =
    testList "tuning and metadata" [
        test "standard tuning is all zeros and flagged standard" {
            let r = convert Lead (score 1 (track standard [ beat 0L<tick> q [ note 0 3 ] ]))
            Expect.equal (List.ofArray r.Arrangement.MetaData.Tuning.Strings) [ 0s; 0s; 0s; 0s; 0s; 0s ] "zeros"
            Expect.isTrue r.Arrangement.MetaData.ArrangementProperties.StandardTuning "standard"
        }

        test "drop D is -2 on the low string" {
            let r = convert Lead (score 1 (track [ 38; 45; 50; 55; 59; 64 ] [ beat 0L<tick> q [ note 0 3 ] ]))
            Expect.equal (List.ofArray r.Arrangement.MetaData.Tuning.Strings) [ -2s; 0s; 0s; 0s; 0s; 0s ] "drop D"
            Expect.isFalse r.Arrangement.MetaData.ArrangementProperties.StandardTuning "not standard"
        }

        test "4-string bass standard is zeros relative to bass standard, and PathBass" {
            let r = convert Bass (score 1 (track bassStandard [ beat 0L<tick> q [ note 0 3 ] ]))
            Expect.equal (List.ofArray r.Arrangement.MetaData.Tuning.Strings) [ 0s; 0s; 0s; 0s; 0s; 0s ] "bass standard"
            Expect.isTrue r.Arrangement.MetaData.ArrangementProperties.PathBass "bass"
            Expect.equal r.Arrangement.MetaData.Arrangement "Bass" "name"
        }

        test "7-string guitar drops the low string, remaps, and warns" {
            let seven = 35 :: standard // B1 below low E
            let beats = [ beat 0L<tick> q [ note 0 3 ]; beat q q [ note 1 5 ]; beat (2L * q) q [ note 6 7 ] ]
            let r = convert Lead (score 1 (track seven beats))
            let l = level r
            Expect.equal l.Notes.Count 2 "the low-B note was dropped"
            Expect.equal (l.Notes |> Seq.map (fun n -> int n.String) |> List.ofSeq) [ 0; 5 ] "remapped down by one"
            Expect.contains r.Warnings (StringsDropped(7, 6, 1)) "warned with the count"
            Expect.equal (List.ofArray r.Arrangement.MetaData.Tuning.Strings) [ 0s; 0s; 0s; 0s; 0s; 0s ] "kept strings are standard"
        }

        test "5-string bass drops the low B" {
            let r = convert Bass (score 1 (track (23 :: bassStandard) [ beat 0L<tick> q [ note 1 3 ] ]))
            Expect.equal (level r).Notes.[0].String 0y "E string remapped to 0"
            Expect.contains r.Warnings (StringsDropped(5, 4, 0)) "no notes lost this time"
        }

        test "tuning pitch becomes centOffset" {
            let s = score 1 (track standard [ beat 0L<tick> q [ note 0 3 ] ])
            let r = Convert.toArrangement ConversionOptions.Default { meta Lead with TuningPitchHz = 432.0 } toMs s s.Tracks.Head
            Expect.floatClose Accuracy.low (float r.Arrangement.MetaData.CentOffset) -31.77 "432 Hz"
            Expect.equal (convert Lead s).Arrangement.MetaData.CentOffset 0.0f "440 is zero"
        }

        test "metadata and average tempo" {
            let r = convert Lead (score 4 (track standard [ beat 0L<tick> q [ note 0 3 ] ]))
            let m = r.Arrangement.MetaData
            Expect.equal (m.Title, m.ArtistName, m.AlbumName, m.AlbumYear, m.Part, m.SongLength) ("Song", "Artist", "Album", 2024, 1s, 60_000) "header"
            Expect.floatClose Accuracy.low (float m.AverageTempo) 120.0 "120 bpm over the score"
            Expect.equal r.Arrangement.Levels.Count 1 "single level for DD to expand"
            Expect.isEmpty r.Arrangement.Phrases "phrases are the library's job"
        }
    ]

[<Tests>]
let roundTrip =
    testList "xml round trip" [
        test "save then load preserves notes, chords, templates, beats and anchors" {
            let beats =
                [ beat 0L<tick> q [ note 0 5 |> withTech Technique.HammerOn ]
                  { beat q (2L * q) [ note 0 3; note 1 5; note 2 5 ] with ChordName = Some "C5" }
                  beat (3L * q) q [ { note 2 7 with BendPoints = [ { Offset = 0.0; Semitones = 0.0 }; { Offset = 1.0; Semitones = 1.0 } ] } ] ]
            let r = convert Lead (score 2 (track standard beats))
            let path = Path.Combine(Path.GetTempPath(), $"stringsmith-{System.Guid.NewGuid()}.xml")
            try
                r.Arrangement.Save path
                let back = InstrumentalArrangement.Load path
                let a, b = r.Arrangement.Levels.[0], back.Levels.[0]
                Expect.equal b.Notes.Count a.Notes.Count "notes"
                Expect.equal b.Chords.Count a.Chords.Count "chords"
                Expect.equal b.Anchors.Count a.Anchors.Count "anchors"
                Expect.equal b.HandShapes.Count a.HandShapes.Count "handshapes"
                Expect.equal back.ChordTemplates.Count r.Arrangement.ChordTemplates.Count "templates"
                Expect.equal back.Ebeats.Count r.Arrangement.Ebeats.Count "ebeats"
                Expect.equal (b.Notes |> Seq.map (fun n -> n.Time, n.String, n.Fret, n.Sustain, n.Mask) |> List.ofSeq)
                             (a.Notes |> Seq.map (fun n -> n.Time, n.String, n.Fret, n.Sustain, n.Mask) |> List.ofSeq) "note content"
                Expect.equal back.MetaData.Title "Song" "metadata"
                Expect.isTrue (b.Notes |> Seq.exists (fun n -> n.IsBend)) "bend survived"
            finally
                File.Delete path
        }
    ]
