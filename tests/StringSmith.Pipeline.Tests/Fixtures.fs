module StringSmith.Pipeline.Tests.Fixtures

open System
open System.IO
open StringSmith.Core
open StringSmith.GuitarPro

let private here = __SOURCE_DIRECTORY__
let rsTests = Path.GetFullPath(Path.Combine(here, "..", "..", "external", "Rocksmith2014.NET", "tests"))
let gp name = Path.GetFullPath(Path.Combine(here, "..", "fixtures", "alphatab", name))

/// Real recording (Bach, BWV 573) and its Wwise-encoded WEMs from the library's own tests.
let wav = Path.Combine(rsTests, "Rocksmith2014.Audio.Tests", "BWV0573_wave.wav")
let wemMain = Path.Combine(rsTests, "Rocksmith2014.IntegrationTests", "project", "BWV0573.wem")
let wemPreview = Path.Combine(rsTests, "Rocksmith2014.IntegrationTests", "project", "BWV0573_preview.wem")
let cover = Path.Combine(rsTests, "Rocksmith2014.IntegrationTests", "project", "cover.png")

let loadScore name : TabScore =
    match (AlphaTabSource() :> ITabSource).Load(gp name) with
    | Ok s -> s
    | Error e -> failwithf "%s: %A" name e

/// A fresh work directory holding the audio and, optionally, pre-encoded WEMs.
let workDir (withWems: bool) =
    let dir = Path.Combine(Path.GetTempPath(), "stringsmith-pipeline-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let audio = Path.Combine(dir, "song.wav")
    File.Copy(wav, audio)
    if withWems then
        File.Copy(wemMain, Path.Combine(dir, "song.wem"))
        File.Copy(wemPreview, Path.Combine(dir, "song_preview.wem"))
    dir, audio
