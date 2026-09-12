module StringSmith.App.Tests.AppTests

open System.IO
open Avalonia.Controls
open Expecto
open Rocksmith2014.XML.Processing
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion
open StringSmith.Audio
open StringSmith.GuitarPro
open StringSmith.Pipeline
open StringSmith.App
open StringSmith.App.Model

/// Only dialog branches dereference the window; none of these tests take them.
let private win : Window = Unchecked.defaultof<Window>
let private up (msg: Msg) (m: Model) = fst (Update.update win msg m)
let private ups (msgs: Msg list) (m: Model) = msgs |> List.fold (fun acc msg -> up msg acc) m
let private render (m: Model) = Views.Main.view win m ignore |> ignore

let private fixture name =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "alphatab", name))

let private score =
    lazy (
        match (AlphaTabSource() :> ITabSource).Load(fixture "full-song.gp5") with
        | Ok s -> s
        | Error e -> failwithf "%A" e)

let private audio : AudioInfo =
    { Path = "/music/song.wav"
      Tags = { AudioTags.Empty with Title = Some "Tagged Title"; Artist = Some "Tagged Artist"; Year = Some 2011; Codec = Some "pcm_s16le" }
      LengthMs = 240_000.0
      NeedsFFmpeg = false }

let private allFound : Dependencies =
    { FFmpeg = Found("/opt/homebrew/bin/ffmpeg", "7.1"); FFprobe = Found("/opt/homebrew/bin/ffprobe", "7.1")
      Wwise = Found("/Applications/Audiokinetic/Wwise2024.1.0/Wwise.app/Contents/Tools/WwiseConsole.sh", "Wwise2024.1.0") }

let private loaded () =
    initialModel |> ups [ DepsChecked allFound; TabLoadFinished(fixture "full-song.gp5", Ok score.Value) ]

let private ready () =
    loaded ()
    |> ups [ AudioProbeFinished(audio.Path, Ok audio)
             AlbumArtPicked(Some "/music/cover.png")
             OutputDirPicked(Some "/music/out") ]

[<Tests>]
let views =
    testList "views render headlessly in every state" [
        test "initial" { render initialModel }
        test "dependencies missing" {
            render (initialModel |> up (DepsChecked { FFmpeg = Missing [ "/a" ]; FFprobe = Missing [ "/b" ]; Wwise = Missing [ "/Applications/Audiokinetic" ] }))
        }
        test "tab loading and failed" {
            render (initialModel |> up (TabPicked(Some "/x.gp5")))
            render (initialModel |> up (TabLoadFinished("/x.gp5", Error(NoPlayableTracks "/x.gp5"))))
            render (initialModel |> up (TabLoadFinished("/x.gp5", Error(CorruptFile("/x.gp5", "bad")))))
        }
        test "tab loaded: tracks, metadata" { render (loaded ()) }
        test "audio failed" { render (loaded () |> up (AudioProbeFinished("/a.mp3", Error "no ffprobe"))) }
        test "everything ready, simple sync" { render (ready ()) }
        test "advanced sync with anchors" {
            render (ready () |> ups [ ToggleAdvancedSync; SetNewAnchorBar "1"; SetNewAnchorSeconds "0.5"; AddAnchor; SetNewAnchorBar "40"; SetNewAnchorSeconds "120"; AddAnchor ])
        }
        test "build running, succeeded, failed" {
            let m = ready ()
            render (m |> up (BuildProgressed { Stage = Packaging; Percent = 57.0; Message = "Packing" }))
            let fakeReport : ArrangementReport =
                { Role = Lead; XmlPath = "/w/arr_lead.xml"; Tuning = Array.zeroCreate 6
                  Warnings = [ NoFingeringInSource; LegatoWithoutAdjacentTarget 3 ]
                  Stats = { Notes = 10; Chords = 5; ChordTemplates = 2; Anchors = 3; HandShapes = 5; Ebeats = 100 }
                  Issues = [ IssueWithTimeCode(FretNumberMoreThan24, 12345) ] }
            let drift = DriftReport.create score.Value SyncMap.empty 240_000.0<ms>
            render (m |> up (BuildFinished(Ok { Packages = [ "/out/k_p.psarc"; "/out/k_m.psarc" ]; DlcKey = "k"; Arrangements = [ fakeReport ]; Drift = drift; WorkDir = "/w" })))
            render (m |> up (BuildFinished(Error { Stage = EncodingAudio; Message = "Wwise failed"; WorkDir = "/w"; Completed = [ fakeReport ] })))
        }
    ]

[<Tests>]
let prefill =
    testList "metadata prefill precedence" [
        test "tab fills empty fields and labels them" {
            let m = loaded ()
            Expect.equal m.Meta.Title.Value "The crow, the owl and the dove" "title from tab"
            Expect.equal m.Meta.Title.Source FromTab "source"
            Expect.equal m.Meta.Artist.Value "Nightwish" "artist"
            Expect.equal m.Meta.Year.Source Unset "tab has no year"
        }
        test "audio tags override tab values and supply the year" {
            let m = ready ()
            Expect.equal (m.Meta.Title.Value, m.Meta.Title.Source) ("Tagged Title", FromAudio) "title from tags wins over tab"
            Expect.equal (m.Meta.Artist.Value, m.Meta.Artist.Source) ("Tagged Artist", FromAudio) "artist"
            Expect.equal (m.Meta.Album.Value, m.Meta.Album.Source) ("Imaginaerum", FromTab) "no album tag: tab value kept"
            Expect.equal (m.Meta.Year.Value, m.Meta.Year.Source) ("2011", FromAudio) "year"
        }
        test "a user edit is never overwritten by a later prefill" {
            let m = loaded () |> up (SetTitle "My Title") |> up (AudioProbeFinished(audio.Path, Ok audio))
            Expect.equal (m.Meta.Title.Value, m.Meta.Title.Source) ("My Title", FromUser) "user wins"
        }
    ]

[<Tests>]
let mapping =
    testList "track mapping" [
        test "at most one Lead and one Bass are suggested, from instrument and string count" {
            let m = loaded ()
            let roles = m.Roles |> Map.toList |> List.map snd
            Expect.isLessThanOrEqual (roles |> List.filter ((=) Lead) |> List.length) 1 "one lead"
            Expect.isLessThanOrEqual (roles |> List.filter ((=) Bass) |> List.length) 1 "one bass"
            let bassTrack = score.Value.Tracks |> List.find (fun t -> t.Name = "Marco")
            Expect.equal (m.Roles |> Map.tryFind bassTrack.Index) (Some Bass) "the 4-string bass patch is suggested as Bass"
        }
        test "roles can be set and cleared; a 6-string on Bass warns" {
            let m = loaded ()
            let guitar = score.Value.Tracks |> List.find (fun t -> t.Name = "Emppu(Disto)")
            let m2 = m |> up (SetRole(guitar.Index, Some Bass))
            Expect.isNonEmpty (Derive.trackWarnings m2 guitar) "warned"
            Expect.stringContains (Derive.trackWarnings m2 guitar |> List.head) "4 strings" "says why"
            let m3 = m2 |> up (SetRole(guitar.Index, None))
            Expect.isFalse (m3.Roles.ContainsKey guitar.Index) "cleared"
        }
        test "a vocal line on a guitar staff is flagged by its MIDI program" {
            let m = loaded ()
            let voice = score.Value.Tracks |> List.find (fun t -> t.Name = "Anette voice")
            let m2 = m |> up (SetRole(voice.Index, Some Lead))
            Expect.exists (Derive.trackWarnings m2 voice) (fun w -> w.Contains "MIDI program 73") "flute patch flagged"
        }
    ]

[<Tests>]
let syncModel =
    testList "sync" [
        test "defaults are honestly NotSynced, not a fake two-anchor map" {
            let m = ready ()
            Expect.equal (Derive.drift m).Value.Status NotSynced "defaults"
        }
        test "offset and scale become OffsetAndScale; a bad number falls back" {
            let m = ready () |> ups [ SetOffset "1200"; SetScale "1.0074" ]
            Expect.equal (Derive.drift m).Value.Status OffsetAndScale "two anchors"
            let bad = ready () |> ups [ SetOffset "abc"; SetScale "1.0074" ]
            Expect.equal (Derive.drift bad).Value.Status NotSynced "unparseable input never claims sync"
        }
        test "anchors parse, reject bad input with a logged reason, and seed from nothing when the file has none" {
            let s = score.Value
            let m = ready () |> ups [ ToggleAdvancedSync; SetNewAnchorBar "0"; SetNewAnchorSeconds "1"; AddAnchor ]
            Expect.isEmpty m.Sync.Anchors "bar 0 rejected"
            Expect.stringContains m.Log.Head "Bar must be between 1 and" "reason logged"
            let m2 = m |> ups [ SetNewAnchorBar "1"; SetNewAnchorSeconds "0.5"; AddAnchor; SetNewAnchorBar (string s.Bars.Length); SetNewAnchorSeconds "239"; AddAnchor ]
            Expect.equal m2.Sync.Anchors.Length 2 "two anchors"
            Expect.equal (Derive.drift m2).Value.Status OffsetAndScale "status follows anchor count"
            let m3 = m2 |> up SeedAnchorsFromSource
            Expect.equal m3.Sync.Anchors.Length 2 "no source points in this file: unchanged"
        }
    ]

[<Tests>]
let buildGate =
    testList "build gate" [
        test "blockers are actionable sentences and disappear when everything is set" {
            let m0 = initialModel
            let b0 = Derive.buildBlockers m0
            Expect.contains b0 "Choose a Guitar Pro file." "tab"
            Expect.contains b0 "Choose an audio file." "audio"
            let m = ready ()
            Expect.isEmpty (Derive.buildBlockers m) $"ready: %A{Derive.buildBlockers m}"
            match Derive.buildRequest m with
            | Ok(req, _) ->
                Expect.equal req.Platforms [ Rocksmith2014.Common.PC; Rocksmith2014.Common.Mac ] "both platforms"
                Expect.equal req.Arrangements.Length m.Roles.Count "one arrangement per mapped track"
                Expect.isSome req.WemEncoder "Wwise found: encoder wired"
                Expect.equal req.Meta.Title "Tagged Title" "title"
            | Error e -> failtestf "%A" e
        }
        test "missing Wwise blocks the build; missing FFmpeg blocks only when the audio needs it" {
            let noWwise = { allFound with Wwise = Missing [ "/Applications/Audiokinetic" ] }
            let m = ready () |> up (DepsChecked noWwise)
            Expect.exists (Derive.buildBlockers m) (fun b -> b.Contains "Wwise") "wwise"
            let noFfmpeg = { allFound with FFmpeg = Missing [ "/x" ] }
            let wav = ready () |> up (DepsChecked noFfmpeg)
            Expect.isEmpty (Derive.buildBlockers wav) "wav does not need ffmpeg"
            let mp3 = wav |> up (AudioProbeFinished("/m.mp3", Ok { audio with Path = "/m.mp3"; NeedsFFmpeg = true }))
            Expect.exists (Derive.buildBlockers mp3) (fun b -> b.Contains "FFmpeg") "mp3 does"
        }
        test "unchecking both platforms blocks" {
            let m = ready () |> ups [ TogglePC; ToggleMac ]
            Expect.contains (Derive.buildBlockers m) "Select at least one platform." "platforms"
        }
    ]
