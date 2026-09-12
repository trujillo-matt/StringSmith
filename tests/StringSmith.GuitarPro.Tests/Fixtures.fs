module StringSmith.GuitarPro.Tests.Fixtures

open System.IO
open StringSmith.Core
open StringSmith.GuitarPro

/// tests/fixtures/alphatab, resolved from this source file's compile-time location so
/// the tests run headlessly from any working directory.
let dir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "alphatab"))

let path name = Path.Combine(dir, name)

let load name : TabScore =
    match (AlphaTabSource() :> ITabSource).Load(path name) with
    | Ok s -> s
    | Error e -> failwithf "fixture %s failed to load: %A" name e

/// First non-percussion stringed track.
let firstStringed (s: TabScore) =
    s.Tracks |> List.find (fun t -> not t.IsPercussion && not t.TuningMidi.IsEmpty)

let allNotes (t: TabTrack) = t.Beats |> List.collect (fun b -> b.Notes)
