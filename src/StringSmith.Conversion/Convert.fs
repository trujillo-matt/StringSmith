namespace StringSmith.Conversion

open System
open System.Collections.Generic
open Rocksmith2014.XML
open StringSmith.Core
open StringSmith.Conversion.NoteBuilder

module Convert =

    /// Unrolled tick -> audio milliseconds. Supplied by the caller (the Sync layer in the
    /// app, a plain linear function in tests).
    type TimeMap = int64<tick> -> int

    // ---------------------------------------------------------------- ebeats

    /// Downbeats carry the 1-based bar number; every other beat is -1. This list is the
    /// only structural input PhraseGenerator has, so it must be right.
    let ebeats (toMs: TimeMap) (score: TabScore) : Ebeat list =
        let tpq = int64 score.TicksPerQuarter
        score.Bars
        |> List.collect (fun bar ->
            let barTicks = (int64 bar.TimeSigNumerator * 4L * tpq) / int64 bar.TimeSigDenominator
            let beats = max 1 bar.TimeSigNumerator
            let beatTicks = barTicks / int64 beats
            [ for k in 0 .. beats - 1 ->
                let tick = bar.StartTick + int64 k * beatTicks * 1L<tick>
                Ebeat(toMs tick, (if k = 0 then int16 (bar.Index + 1) else -1s)) ])
        |> List.distinctBy (fun e -> e.Time)
        |> List.sortBy (fun e -> e.Time)

    // ---------------------------------------------------------------- events

    type private Event =
        | SingleNote of Note
        | ChordEvent of Chord * durationMs: int

    let private timeOf = function
        | SingleNote n -> n.Time
        | ChordEvent(c, _) -> c.Time

    let private frettedOf = function
        | SingleNote n -> if n.Fret > 0y then [ int n.Fret ] else []
        | ChordEvent(c, _) ->
            match c.ChordNotes with
            | null -> []
            | cn -> cn |> Seq.filter (fun n -> n.Fret > 0y) |> Seq.map (fun n -> int n.Fret) |> List.ofSeq

    /// After all notes exist: a legato slide becomes linkNext when the next note on the
    /// same string starts exactly where this one's sustain ends. Otherwise it stays a
    /// pitched slide with a re-pick, and we count it for the warning.
    let private linkLegato (notesByString: Dictionary<int, List<Note * TabNote>>) =
        let mutable unlinked = 0
        for KeyValue(_, notes) in notesByString do
            let arr = notes.ToArray()
            for i in 0 .. arr.Length - 1 do
                let (rsNote, core) = arr.[i]
                if core.SlideOut = Legato && rsNote.SlideTo <> -1y then
                    let linked =
                        i + 1 < arr.Length
                        && (fst arr.[i + 1]).Time = rsNote.Time + rsNote.Sustain
                        && (fst arr.[i + 1]).Fret = rsNote.SlideTo
                    if linked then rsNote.IsLinkNext <- true else unlinked <- unlinked + 1
        unlinked

    // ---------------------------------------------------------------- properties

    let private arrangementProperties (role: ArrangementRole) (standard: bool) (notes: Note seq) (chords: Chord seq) =
        let all = Seq.append notes (chords |> Seq.collect (fun c -> match c.ChordNotes with null -> Seq.empty | cn -> cn :> Note seq)) |> Array.ofSeq
        let any f = all |> Array.exists f
        let p = ArrangementProperties()
        p.Represent <- true
        p.StandardTuning <- standard
        p.PathLead <- (role = Lead)
        p.PathRhythm <- (role = Rhythm)
        p.PathBass <- (role = Bass)
        p.PalmMutes <- any (fun n -> n.IsPalmMute)
        p.Harmonics <- any (fun n -> n.IsHarmonic)
        p.PinchHarmonics <- any (fun n -> n.IsPinchHarmonic)
        p.Hopo <- any (fun n -> n.IsHammerOn || n.IsPullOff)
        p.Tremolo <- any (fun n -> n.IsTremolo)
        p.Slides <- any (fun n -> n.IsSlide)
        p.UnpitchedSlides <- any (fun n -> n.IsUnpitchedSlide)
        p.Bends <- any (fun n -> n.IsBend)
        p.Tapping <- any (fun n -> n.IsTap)
        p.Vibrato <- any (fun n -> n.IsVibrato)
        p.FretHandMutes <- any (fun n -> n.IsFretHandMute)
        p.SlapPop <- any (fun n -> n.IsSlap || n.IsPluck)
        p.Sustain <- any (fun n -> n.Sustain > 0)
        p.OpenChords <- chords |> Seq.exists (fun c -> match c.ChordNotes with null -> false | cn -> cn |> Seq.exists (fun n -> n.Fret = 0y))
        p.DoubleStops <- chords |> Seq.exists (fun c -> match c.ChordNotes with null -> false | cn -> cn.Count = 2)
        p

    // ---------------------------------------------------------------- main

    /// Converts one track of a score into a single-level arrangement ready for
    /// PhraseGenerator and DD. Never throws on odd content; it warns.
    let toArrangement (opts: ConversionOptions) (meta: ArrangementMeta) (toMs: TimeMap) (score: TabScore) (track: TabTrack) : ConversionResult =
        let warnings = List<ConversionWarning>()

        // -- strings
        let drop, keep = Tuning.stringPlan meta.Role track.StringCount
        let tuningOffsets = Tuning.offsets meta.Role track.TuningMidi

        // -- time the beats
        let timed =
            track.Beats
            |> List.filter (fun b -> not b.IsRest)
            |> List.map (fun b ->
                let start = toMs b.StartTick
                let stop = toMs (b.StartTick + b.DurationTicks)
                b, start, max 0 (stop - start))

        // -- group by time; dedupe strings; drop out-of-range strings
        let mutable droppedNotes = 0
        let mutable duplicateNotes = 0
        let mutable fretsAbove24 = 0
        let groups =
            timed
            |> List.groupBy (fun (_, start, _) -> start)
            |> List.sortBy fst
            |> List.map (fun (start, beats) ->
                let seen = HashSet<int>()
                let notes =
                    [ for (beat, _, dur) in beats do
                        for n in beat.Notes do
                            let s = n.String - drop
                            if s < 0 || s >= keep then droppedNotes <- droppedNotes + 1
                            elif not (seen.Add s) then duplicateNotes <- duplicateNotes + 1
                            else
                                if n.Fret > 24 then fretsAbove24 <- fretsAbove24 + 1
                                yield s, { Note = n; Beat = beat; TimeMs = start; DurationMs = dur } ]
                let chordName = beats |> List.tryPick (fun (b, _, _) -> b.ChordName)
                start, notes, chordName)
            |> List.filter (fun (_, notes, _) -> not notes.IsEmpty)

        // -- build notes, merging ties into the previous note on the string
        let notesByString = Dictionary<int, List<Note * TabNote>>()
        let lastOnString = Dictionary<int, Note>()
        let templates = List<ChordTemplate>()
        let templateIndex = Dictionary<string, int16>()
        let singles = List<Note>()
        let chords = List<Chord>()
        let handShapes = List<HandShape>()
        let events = List<Event>()

        let register s (rs: Note) (core: TabNote) =
            if not (notesByString.ContainsKey s) then notesByString.[s] <- List()
            notesByString.[s].Add((rs, core))
            lastOnString.[s] <- rs

        let templateFor (name: string option) (frets: (int * int) list) =
            let fretArr = Array.create 6 -1y
            for (s, f) in frets do fretArr.[s] <- sbyte f
            let key = String.Join(",", fretArr) + "|" + defaultArg name ""
            match templateIndex.TryGetValue key with
            | true, id -> id
            | _ ->
                let id = int16 templates.Count
                let display = defaultArg name ""
                templates.Add(ChordTemplate(display, display, Array.create 6 -1y, fretArr))
                templateIndex.[key] <- id
                id

        for (start, notes, chordName) in groups do
            // tie continuations extend the previous note and are not emitted
            let continuing, fresh =
                notes |> List.partition (fun (s, t) ->
                    t.Note.Techniques.HasFlag Technique.TieDestination && lastOnString.ContainsKey s)
            for (s, t) in continuing do
                let prev = lastOnString.[s]
                prev.Sustain <- max prev.Sustain (t.TimeMs + t.DurationMs - prev.Time)

            match fresh with
            | [] -> ()
            | [ (s, t) ] ->
                let rs = NoteBuilder.build opts s t
                singles.Add rs
                register s rs t.Note
                events.Add(SingleNote rs)
            | many ->
                let built = many |> List.map (fun (s, t) -> s, t, NoteBuilder.build opts s t)
                let chordNotes = ResizeArray(built |> List.map (fun (_, _, rs) -> rs))
                let id = templateFor chordName (built |> List.map (fun (s, t, _) -> s, t.Note.Fret))
                let dur = built |> List.map (fun (_, t, _) -> t.DurationMs) |> List.max
                let chord = Chord(Time = start, ChordId = id, ChordNotes = chordNotes)
                chord.IsPalmMute <- chordNotes |> Seq.forall (fun n -> n.IsPalmMute)
                chord.IsFretHandMute <- chordNotes |> Seq.forall (fun n -> n.IsFretHandMute)
                chord.IsAccent <- chordNotes |> Seq.forall (fun n -> n.IsAccent)
                chords.Add chord
                for (s, t, rs) in built do register s rs t.Note
                events.Add(ChordEvent(chord, dur))

        // -- legato linkNext, now that string neighbours are known
        let unlinked = linkLegato notesByString
        chords |> Seq.iter (fun c ->
            match c.ChordNotes with
            | null -> ()
            | cn -> c.IsLinkNext <- cn |> Seq.exists (fun n -> n.IsLinkNext))

        // -- handshapes: one per chord, bounded by the next event, merged when adjacent
        let ordered = events |> Seq.sortBy timeOf |> Array.ofSeq
        for i in 0 .. ordered.Length - 1 do
            match ordered.[i] with
            | ChordEvent(c, dur) ->
                let nextTime = if i + 1 < ordered.Length then timeOf ordered.[i + 1] else Int32.MaxValue
                let sustainEnd =
                    match c.ChordNotes with
                    | null -> c.Time + dur
                    | cn -> cn |> Seq.map (fun n -> n.Time + n.Sustain) |> Seq.fold max (c.Time + dur)
                let endTime = max (c.Time + 1) (min sustainEnd nextTime)
                match handShapes |> Seq.tryLast with
                | Some last when last.ChordId = c.ChordId && last.EndTime >= c.Time ->
                    last.EndTime <- max last.EndTime endTime
                | _ -> handShapes.Add(HandShape(c.ChordId, c.Time, endTime))
            | SingleNote _ -> ()

        // -- anchors
        let anchors =
            ordered
            |> Array.map (fun e -> timeOf e, frettedOf e)
            |> List.ofArray
            |> Anchors.generate opts.AnchorWidth

        // -- warnings
        if drop > 0 then warnings.Add(StringsDropped(track.StringCount, keep, droppedNotes))
        if fretsAbove24 > 0 then warnings.Add(FretsAbove24 fretsAbove24)
        if unlinked > 0 then warnings.Add(LegatoWithoutAdjacentTarget unlinked)
        if duplicateNotes > 0 then warnings.Add(DuplicateStringNotes duplicateNotes)
        if track.StaffCount > 1 then warnings.Add(ExtraStavesIgnored track.StaffCount)
        if singles.Count + chords.Count > 0
           && notesByString.Values |> Seq.forall (fun l -> l |> Seq.forall (fun (rs, _) -> rs.LeftHand = -1y)) then
            warnings.Add NoFingeringInSource

        // -- assemble
        let arr = InstrumentalArrangement()
        arr.Version <- 8uy
        let beats = ebeats toMs score
        arr.Ebeats <- ResizeArray beats
        arr.ChordTemplates <- ResizeArray templates
        let sortedNotes = singles |> Seq.sortBy (fun n -> n.Time, n.String) |> ResizeArray
        let sortedChords = chords |> Seq.sortBy (fun c -> c.Time) |> ResizeArray
        arr.Levels <- ResizeArray [ Level(0y, sortedNotes, sortedChords, ResizeArray anchors, handShapes) ]

        let m = arr.MetaData
        m.Title <- meta.Title
        m.ArtistName <- meta.Artist
        m.AlbumName <- meta.Album
        m.AlbumYear <- meta.Year
        m.Arrangement <- meta.Role.Name
        m.Part <- meta.Part
        m.CentOffset <- Tuning.centOffset meta.TuningPitchHz
        m.SongLength <- meta.SongLengthMs
        m.Capo <- sbyte track.Capo
        m.Tuning.SetTuning(tuningOffsets.[0], tuningOffsets.[1], tuningOffsets.[2], tuningOffsets.[3], tuningOffsets.[4], tuningOffsets.[5])
        m.LastConversionDateTime <- DateTime.Now.ToString("MM-dd-yy HH:mm", Globalization.CultureInfo.InvariantCulture)
        m.AverageTempo <-
            let quarters = float score.EndTick / float score.TicksPerQuarter
            let minutes = float (toMs score.EndTick - toMs 0L<tick>) / 60000.0
            if minutes > 0.0 && quarters > 0.0 then float32 (quarters / minutes) else float32 score.InitialBpm
        m.ArrangementProperties <- arrangementProperties meta.Role (Tuning.isStandard tuningOffsets) sortedNotes sortedChords

        { Arrangement = arr
          Warnings = List.ofSeq warnings
          Stats =
            { Notes = sortedNotes.Count
              Chords = sortedChords.Count
              ChordTemplates = templates.Count
              Anchors = anchors.Length
              HandShapes = handShapes.Count
              Ebeats = beats.Length } }
