namespace StringSmith.GuitarPro

open AlphaTab.Model
open StringSmith.Core

/// Pure enum-to-enum mapping from AlphaTab's model to the neutral Core model.
/// Every function here is total: unknown AlphaTab values map to the Core "none" case
/// rather than throwing, because a community file with an odd marking must still load.
module internal Mapping =

    let harmonic (h: HarmonicType) =
        match h with
        | HarmonicType.Natural -> Natural
        | HarmonicType.Artificial -> Artificial
        | HarmonicType.Pinch -> Pinch
        | HarmonicType.Tap -> Tap
        | HarmonicType.Semi -> Semi
        | HarmonicType.Feedback -> Feedback
        | _ -> NoHarmonic

    let slideOut (s: SlideOutType) =
        match s with
        | SlideOutType.Shift -> Shift
        | SlideOutType.Legato -> Legato
        | SlideOutType.OutUp -> OutUp
        | SlideOutType.OutDown -> OutDown
        | SlideOutType.PickSlideDown -> PickSlideDown
        | SlideOutType.PickSlideUp -> PickSlideUp
        | _ -> NoSlideOut

    let slideIn (s: SlideInType) =
        match s with
        | SlideInType.IntoFromBelow -> FromBelow
        | SlideInType.IntoFromAbove -> FromAbove
        | _ -> NoSlideIn

    let vibrato (v: VibratoType) =
        match v with
        | VibratoType.Slight -> Slight
        | VibratoType.Wide -> Wide
        | _ -> NoVibrato

    /// AlphaTab has 26 dynamic values including sforzandos and extremes. Rocksmith only
    /// distinguishes accented from not, so collapsing to eight steps loses nothing that
    /// reaches the game.
    let dynamic (d: DynamicValue) =
        match d with
        | DynamicValue.PPPPPP | DynamicValue.PPPPP | DynamicValue.PPPP | DynamicValue.PPP -> PPP
        | DynamicValue.PP -> PP
        | DynamicValue.P | DynamicValue.PF | DynamicValue.FP -> P
        | DynamicValue.MP -> MP
        | DynamicValue.MF | DynamicValue.N -> MF
        | DynamicValue.F | DynamicValue.RF | DynamicValue.RFZ -> F
        | DynamicValue.FF | DynamicValue.SF | DynamicValue.SFP | DynamicValue.SFPP | DynamicValue.SFZ | DynamicValue.SFZP | DynamicValue.FZ -> FF
        | DynamicValue.FFFFFF | DynamicValue.FFFFF | DynamicValue.FFFF | DynamicValue.FFF | DynamicValue.SFFZ -> FFF
        | _ -> MF

    /// Rocksmith fingers: 0 = thumb, 1..4 = index..little. None when the source did not
    /// say. This is the one place fingering enters the pipeline and it is never invented.
    let finger (f: Fingers) =
        match f with
        | Fingers.Thumb -> Some 0
        | Fingers.IndexFinger -> Some 1
        | Fingers.MiddleFinger -> Some 2
        | Fingers.AnnularFinger -> Some 3
        | Fingers.LittleFinger -> Some 4
        | _ -> None

    /// AlphaTab bend points: offset 0..60 across the note, value in quarter-tones.
    /// Core: offset 0..1, semitones. Source of both scales: alphaTab/src/model/BendPoint.ts.
    let bendPoint (p: AlphaTab.Model.BendPoint) : StringSmith.Core.BendPoint =
        { Offset = p.Offset / 60.0
          Semitones = p.Value / 2.0 }

    /// Note-level technique flags. Hammer-on / pull-off deserve a note: Guitar Pro marks
    /// the ORIGIN ("hammer into the next note"), Rocksmith flags the DESTINATION (the note
    /// that is hammered). So the flag lands on the destination, chosen by comparing frets
    /// with the origin. A same-fret HOPO is treated as a hammer-on; Rocksmith's checker
    /// will flag it (HopoIntoSameNote) and the UI surfaces that.
    let techniques (n: Note) =
        let mutable t = Technique.None
        let set flag cond = if cond then t <- t ||| flag
        if n.IsHammerPullDestination then
            match n.HammerPullOrigin with
            | null -> ()
            | origin ->
                if n.Fret >= origin.Fret then set Technique.HammerOn true
                else set Technique.PullOff true
        set Technique.PalmMute n.IsPalmMute
        set Technique.LetRing n.IsLetRing
        set Technique.Staccato n.IsStaccato
        set Technique.Dead n.IsDead
        set Technique.Ghost n.IsGhost
        set Technique.TieDestination n.IsTieDestination
        set Technique.TieOrigin n.IsTieOrigin
        set Technique.LeftHandTap n.IsLeftHandTapped
        match n.Accentuated with
        | AccentuationType.Normal | AccentuationType.Tenuto -> set Technique.Accent true
        | AccentuationType.Heavy -> set Technique.HeavyAccent true
        | _ -> ()
        t
