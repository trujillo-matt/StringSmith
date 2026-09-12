namespace StringSmith.Conversion

open StringSmith.Core

/// Tuning as Rocksmith wants it: six semitone offsets from standard, low string first.
module Tuning =

    /// E2 A2 D3 G3 B3 E4
    let standardGuitarMidi = [ 40; 45; 50; 55; 59; 64 ]
    /// E1 A1 D2 G2
    let standardBassMidi = [ 28; 33; 38; 43 ]

    /// Rocksmith string capacity per role.
    let maxStrings (role: ArrangementRole) =
        match role with
        | Bass -> 4
        | Lead | Rhythm -> 6

    /// Chooses which source strings survive. Rocksmith has no 7-string guitar or 5-string
    /// bass; the convention is to drop the LOWEST strings (the extended-range ones) and
    /// keep the top set, remapping indices. Returns (offsetToSubtractFromStringIndex,
    /// keptCount).
    let stringPlan (role: ArrangementRole) (sourceStrings: int) =
        let keep = min sourceStrings (maxStrings role)
        let drop = sourceStrings - keep
        drop, keep

    /// Six offsets. For a 4-string bass, slots 4 and 5 are 0. For fewer than the standard
    /// string count (a 5-string guitar is rare but legal), missing slots are 0.
    let offsets (role: ArrangementRole) (tuningMidi: int list) : int16[] =
        let drop, keep = stringPlan role tuningMidi.Length
        let kept = tuningMidi |> List.skip drop |> List.truncate keep
        let reference =
            match role with
            | Bass -> standardBassMidi
            | Lead | Rhythm -> standardGuitarMidi
        Array.init 6 (fun i ->
            if i < kept.Length && i < reference.Length then int16 (kept.[i] - reference.[i]) else 0s)

    let isStandard (offsets: int16[]) = offsets |> Array.forall (fun o -> o = 0s)

    /// Cents away from A440, as Rocksmith's centOffset.
    let centOffset (pitchHz: float) : float32 =
        if pitchHz <= 0.0 then 0.0f
        else float32 (1200.0 * System.Math.Log2(pitchHz / 440.0))
