namespace StringSmith.Conversion

open Rocksmith2014.XML

/// Fret-hand position markers. Rocksmith requires every note to sit inside an anchor.
///
/// Heuristic, stated plainly: start an anchor at the first fretted event; keep it while
/// every fretted note of an event fits inside [fret, fret + width - 1]; otherwise open a
/// new anchor at that event's time on its lowest fretted note, widening if the event
/// itself spans more than the default width. Open strings never move the hand. The
/// library's AnchorMover improver and checker refine and validate what this produces.
module internal Anchors =

    /// Events as (timeMs, fretted frets excluding 0), already time-ordered.
    let generate (width: int) (events: (int * int list) list) : Anchor list =
        let maxAnchorFret = 21 // fret + width must stay within the neck

        let fits (a: Anchor) (frets: int list) =
            let lo = int a.Fret
            let hi = lo + int a.Width - 1
            frets |> List.forall (fun f -> f >= lo && f <= hi)

        let make time (frets: int list) =
            let lo = max 1 (List.min frets)
            let hi = List.max frets
            let w = max width (hi - lo + 1)
            let fret = min lo maxAnchorFret
            Anchor(sbyte fret, time, sbyte (min w (25 - fret)))

        events
        |> List.fold
            (fun (acc: Anchor list) (time, frets) ->
                match acc, frets with
                | [], [] -> [ Anchor(1y, time, sbyte width) ] // all-open start: park at fret 1
                | [], _ -> [ make time frets ]
                | _, [] -> acc
                | current :: _, _ when fits current frets -> acc
                | _, _ -> make time frets :: acc)
            []
        |> List.rev
