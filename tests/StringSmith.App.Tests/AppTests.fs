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
open StringSmith.App.Controls
open StringSmith.App.Model
open StringSmith.App.Views

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

/// Walks a FuncUI view tree and records the number of children at every node, in order.
/// Two models whose trees have the same shape cannot make FuncUI recycle a control into a
/// different slot, because FuncUI matches children by index.
module private Shape =
    open Avalonia.FuncUI.Types

    /// (path, child count) for every multi-child node, depth first. Stops at a
    /// VariableChildren node: content inside one of those is meant to vary, and it occupies
    /// exactly one slot in its parent however long it gets.
    let rec private walk (path: string) (view: IView) : (string * int) list =
        if view.ViewType = typeof<VariableChildren> then [] else

        view.Attrs
        |> List.collect (fun attr ->
            match attr.Content with
            | ValueNone -> []
            | ValueSome content ->
                match content.Content with
                | ViewContent.Single (Some child) -> walk (path + "/.") child
                | ViewContent.Single None -> []
                | ViewContent.Multiple children ->
                    (path, children.Length)
                    :: (children |> List.mapi (fun i c -> walk $"{path}/{i}" c) |> List.concat))

    let of' (view: IView) = walk (string (view.GetType().Name)) view

[<Tests>]
let viewShape =
    // The bug this guards against, twice shipped: FuncUI matches children by index, so a
    // children list that changes length between renders shifts every sibling after it.
    // The control that was rendering field N then gets field N+1's view while keeping its
    // own change callback, so typing into one field edits its neighbour and a field's
    // displayed text stops matching the model. Every children list must therefore keep a
    // constant length across model states; anything variable belongs inside a container.
    testList "view tree shape is invariant across model states" [
        let states () =
            let noDeps = initialModel
            let deps = initialModel |> up (DepsChecked allFound)
            let depsMissing = initialModel |> up (DepsChecked { FFmpeg = Missing [ "/a" ]; FFprobe = Missing [ "/b" ]; Wwise = Missing [ "/c" ] })
            let loaded = loaded ()
            let ready = ready ()
            let readyBadYear = ready |> up (SetYear "abc")
            let readySynced = ready |> ups [ SetOffset "1200"; SetScale "1.0074" ]
            let advanced = ready |> ups [ ToggleAdvancedSync; SetNewAnchorBar "1"; SetNewAnchorSeconds "0.5"; AddAnchor ]
            let noPlatforms = ready |> ups [ TogglePC; ToggleMac ]
            [ "no deps", noDeps; "deps ok", deps; "deps missing", depsMissing
              "tab loaded", loaded; "ready", ready; "invalid year", readyBadYear
              "synced", readySynced; "advanced sync", advanced; "no platforms", noPlatforms ]

        // Sections whose content list is the same in both branches, so a shape change can
        // only come from a conditional child. These are the ones that bit us.
        //
        // `tracks` and `sync` render two structurally different branches depending on
        // whether a tab/audio is loaded; crossing that boundary replaces the subtree
        // wholesale rather than shifting siblings, so they are checked only across the
        // states where their real branch is live.
        let withScore () = states () |> List.filter (fun (l, _) -> l <> "no deps" && l <> "deps ok" && l <> "deps missing")
        let withAudio () = withScore () |> List.filter (fun (l, _) -> l <> "tab loaded")

        // Not checked as a whole window: `tracks` and `sync` swap between a short disabled
        // layout and a full one, which APPENDS children rather than inserting among them.
        // Appending is harmless — only a change in the middle of a list shifts the indices
        // of siblings after it — but a flat whole-tree comparison cannot tell the two apart.
        // The per-section checks below are where the real invariant lives, and they are the
        // ones that catch the bug (verified by reintroducing it).
        for (name, render, pick) in
            [ "metadata", Sections.metadata, states
              "output", Sections.output, states
              "sources", Sections.sources, states
              "build", Sections.build, states
              "dependencies", Sections.dependencies, states
              "tracks", Sections.tracks, withScore
              "sync", Sections.sync, withAudio ] do
            test $"{name} keeps its shape" {
                let shapes = pick () |> List.map (fun (label, m) -> label, Shape.of' (render m ignore))
                let (baseLabel, baseShape) = shapes.Head
                for (label, shape) in shapes.Tail do
                    if shape <> baseShape then
                        let diff =
                            List.zip
                                (baseShape |> List.truncate (min baseShape.Length shape.Length))
                                (shape |> List.truncate (min baseShape.Length shape.Length))
                            |> List.filter (fun (a, b) -> a <> b)
                            |> List.truncate 4
                        failtestf
                            "%s: shape differs from '%s' (%d nodes vs %d). First differences: %A"
                            label baseLabel baseShape.Length shape.Length diff
            }
    ]

[<Tests>]
let guardedTextBox =
    // Regression test for the bug the first macOS run found: the Year field displayed
    // "440", the Tuning frequency value from the row below it. Cause was that Avalonia's
    // Text setter raises a change notification, so every render that assigned Text
    // dispatched a "user edited this" message; with a recycled control that message went
    // to the wrong field's handler. GuardedTextBox suppresses notifications originating
    // from the view. This exercises that mechanism directly.
    testList "GuardedTextBox suppresses view-originated changes" [
        test "the text attribute does not fire the callback, but a direct edit does" {
            let fired = System.Collections.Generic.List<string>()
            let box = GuardedTextBox()
            box.OnTextChangedCallback <- fired.Add

            // What the view does every render. Must be silent.
            let applyFromView (value: string) =
                box.Suppress <- true
                box.Text <- value
                box.Suppress <- false

            applyFromView "440"
            applyFromView "2011"
            applyFromView "440"
            Expect.isEmpty fired $"view-originated assignments must not dispatch, got %A{List.ofSeq fired}"

            // What a user typing does: the same property, without the suppression flag.
            box.Text <- "2012"
            Expect.equal (List.ofSeq fired) [ "2012" ] "a real edit must dispatch exactly once"

            applyFromView "1999"
            Expect.equal (List.ofSeq fired) [ "2012" ] "a later view assignment must stay silent"
        }

        test "a combo box likewise ignores view-set selections but reports user ones" {
            // The track-mapping combos capture a track index in their handlers, so the same
            // two hazards apply: a view-originated selection must not dispatch, and the
            // callback must be replaceable so a recycled control cannot target another track.
            let fired = System.Collections.Generic.List<obj>()
            let box = GuardedComboBox()
            box.OnSelectionChangedCallback <- fired.Add
            box.ItemsSource <- [ "Lead"; "Rhythm"; "Bass" ]

            box.Suppress <- true
            box.SelectedItem <- "Rhythm"
            box.Suppress <- false
            Expect.isEmpty fired "view-originated selection must not dispatch"

            box.SelectedItem <- "Bass"
            Expect.equal (List.ofSeq fired) [ box.SelectedItem ] "a user selection dispatches once"

            let second = System.Collections.Generic.List<obj>()
            box.OnSelectionChangedCallback <- second.Add
            box.SelectedItem <- "Lead"
            Expect.equal (List.ofSeq second) [ box.SelectedItem ] "the callback can be swapped, so recycling retargets correctly"
        }

        test "a null text reaches the callback as an empty string" {
            let fired = System.Collections.Generic.List<string>()
            let box = GuardedTextBox()
            box.OnTextChangedCallback <- fired.Add
            box.Text <- "x"
            box.Text <- null
            Expect.equal (List.ofSeq fired) [ "x"; "" ] "null is normalised, not passed through"
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
        test "typing a year clears the Year build blocker" {
            // Reported from the video: Year visibly read 2025 while the build stayed blocked
            // on "Year must be a number." The displayed text had drifted from the model
            // because the keystrokes were dispatched to another field's handler. This pins
            // the model-level contract that the UI now honours.
            let m = ready ()
            let cleared = { m with Meta = { m.Meta with Year = Field.empty } }
            Expect.contains (Derive.buildBlockers cleared) "Year must be a number." "blocked while empty"

            let typed = cleared |> up (SetYear "2025")
            Expect.equal typed.Meta.Year.Value "2025" "the model took the year"
            Expect.equal (Derive.year typed) (Some 2025) "and parses it"
            Expect.isFalse (Derive.buildBlockers typed |> List.contains "Year must be a number.") "blocker gone"
            Expect.isEmpty (Derive.buildBlockers typed) $"nothing else blocks: %A{Derive.buildBlockers typed}"
        }

        test "each metadata setter touches only its own field" {
            // The reported symptom was "typing into a field edits the field above".
            let m = ready ()
            let check name msg (get: Model -> string) =
                let after = m |> up msg
                let changed =
                    [ "Title", (fun (x: Model) -> x.Meta.Title.Value); "Artist", (fun x -> x.Meta.Artist.Value)
                      "Album", (fun x -> x.Meta.Album.Value); "Year", (fun x -> x.Meta.Year.Value)
                      "TuningPitch", (fun x -> x.Meta.TuningPitch); "Charter", (fun x -> x.Meta.Charter) ]
                    |> List.filter (fun (_, f) -> f after <> f m)
                    |> List.map fst
                Expect.equal changed [ name ] $"{name}: exactly one field should change"
                Expect.equal (get after) "zz" $"{name} took the value"
            check "Title" (SetTitle "zz") (fun x -> x.Meta.Title.Value)
            check "Artist" (SetArtist "zz") (fun x -> x.Meta.Artist.Value)
            check "Album" (SetAlbum "zz") (fun x -> x.Meta.Album.Value)
            check "Year" (SetYear "zz") (fun x -> x.Meta.Year.Value)
            check "TuningPitch" (SetTuningPitch "zz") (fun x -> x.Meta.TuningPitch)
            check "Charter" (SetCharter "zz") (fun x -> x.Meta.Charter)
        }

        test "editing the tuning frequency never touches the Year field" {
            let m = ready ()
            let before = m.Meta.Year
            let after = m |> ups [ SetTuningPitch "432"; SetTuningPitch "440" ]
            Expect.equal after.Meta.Year before "Year is independent of tuning frequency"
            Expect.equal after.Meta.TuningPitch "440" "tuning frequency took the edit"
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
