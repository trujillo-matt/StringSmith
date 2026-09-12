namespace StringSmith.GuitarPro

open AlphaTab.Midi

/// A sink for AlphaTab's MidiFileGenerator that discards every MIDI event.
///
/// We run the generator not for its MIDI output but for the side effect we actually
/// need: after Generate(), its TickLookup.MasterBars is the UNROLLED playback sequence,
/// one entry per occurrence of each bar, with absolute ticks and per-occurrence tempo
/// changes. AlphaTab's MidiPlaybackController (the thing that walks repeats, alternate
/// endings, and da capo / dal segno directions) is not public in the .NET assembly, so
/// this is the supported way in.
module internal NoOpMidiHandler =
    let create () : IMidiFileHandler =
        { new IMidiFileHandler with
            member _.AddTimeSignature(_, _, _) = ()
            member _.AddRest(_, _, _) = ()
            member _.AddNote(_, _, _, _, _, _) = ()
            member _.AddControlChange(_, _, _, _, _) = ()
            member _.AddProgramChange(_, _, _, _) = ()
            member _.AddTempo(_, _) = ()
            member _.AddNoteBend(_, _, _, _, _) = ()
            member _.AddBend(_, _, _, _) = ()
            member _.FinishTrack(_, _) = ()
            member _.AddTickShift(_) = () }
