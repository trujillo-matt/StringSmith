namespace StringSmith.Conversion

open System
open Rocksmith2014.XML
open StringSmith.Core

/// Builds Rocksmith notes from Core notes. All the technique mapping lives here.
module internal NoteBuilder =

    /// A Core note with its beat's timing already resolved to audio milliseconds.
    type Timed =
        { Note: TabNote
          Beat: TabBeat
          TimeMs: int
          DurationMs: int }

    let private has (flag: Technique) (n: TabNote) = n.Techniques.HasFlag flag

    /// Does this note need sustain to render its technique at all?
    let needsSustain (t: Timed) =
        let n = t.Note
        not n.BendPoints.IsEmpty
        || n.Vibrato <> NoVibrato
        || n.SlideOut <> NoSlideOut
        || t.Beat.IsTremoloPicked
        || has Technique.LetRing n
        || has Technique.TieOrigin n

    let sustain (opts: ConversionOptions) (t: Timed) =
        let d = max 0 t.DurationMs
        if needsSustain t then d
        elif has Technique.Staccato t.Note then 0
        elif d >= opts.MinSustainMs then d
        else 0

    let private bendValues (timeMs: int) (sustainMs: int) (points: StringSmith.Core.BendPoint list) =
        let values =
            points
            |> List.map (fun p ->
                let offset = Math.Clamp(p.Offset, 0.0, 1.0)
                let t = timeMs + int (Math.Round(offset * float sustainMs))
                BendValue(t, float32 p.Semitones))
            |> List.sortBy (fun b -> b.Time)
            // collapse consecutive identical steps: Rocksmith dislikes redundant points
            |> List.fold
                (fun acc (b: BendValue) ->
                    match acc with
                    | (prev: BendValue) :: _ when prev.Step = b.Step -> acc
                    | (prev: BendValue) :: rest when prev.Time = b.Time -> b :: rest
                    | _ -> b :: acc)
                []
            |> List.rev
        ResizeArray values

    /// Builds one Rocksmith note. `stringIndex` is already remapped for dropped strings.
    let build (opts: ConversionOptions) (stringIndex: int) (t: Timed) : Note =
        let n = t.Note
        let sus = sustain opts t
        let note = Note(Time = t.TimeMs, String = sbyte stringIndex, Fret = sbyte n.Fret, Sustain = sus)

        note.LeftHand <- (match n.LeftHandFinger with Some f -> sbyte f | None -> -1y)

        let mutable mask = NoteMask.None
        let set flag = mask <- mask ||| flag
        if has Technique.HammerOn n then set NoteMask.HammerOn
        if has Technique.PullOff n then set NoteMask.PullOff
        if has Technique.PalmMute n then set NoteMask.PalmMute
        if has Technique.Dead n then set NoteMask.FretHandMute
        if has Technique.Accent n || has Technique.HeavyAccent n then set NoteMask.Accent
        if t.Beat.IsTremoloPicked then set NoteMask.Tremolo
        if t.Beat.IsSlap then set NoteMask.Slap
        if t.Beat.IsPop then set NoteMask.Pluck

        match n.Harmonic with
        | Pinch -> set NoteMask.PinchHarmonic
        | Natural | Artificial | Tap | Semi | Feedback -> set NoteMask.Harmonic
        | NoHarmonic -> ()

        note.Mask <- mask

        if t.Beat.IsTap || has Technique.LeftHandTap n then note.Tap <- 1y

        note.Vibrato <-
            match n.Vibrato with
            | Slight -> opts.VibratoSlight
            | Wide -> opts.VibratoWide
            | NoVibrato -> 0uy

        // Pitched slides: both shift and legato carry the destination fret. Whether a
        // legato becomes linkNext is decided later, once the next note on the string is
        // known (see Convert.linkLegato).
        match n.SlideOut, n.SlideTargetFret with
        | (Shift | Legato), Some target when target <> n.Fret -> note.SlideTo <- sbyte target
        | (OutDown | PickSlideDown), _ when n.Fret > 0 ->
            note.SlideUnpitchTo <- sbyte (max 0 (n.Fret - opts.UnpitchedSlideFrets))
        | (OutUp | PickSlideUp), _ ->
            note.SlideUnpitchTo <- sbyte (min 24 (n.Fret + opts.UnpitchedSlideFrets))
        | _ -> ()

        if not n.BendPoints.IsEmpty && sus > 0 then
            let values = bendValues t.TimeMs sus n.BendPoints
            if values.Count > 0 then
                note.BendValues <- values
                note.MaxBend <- values |> Seq.map (fun b -> b.Step) |> Seq.max

        note
