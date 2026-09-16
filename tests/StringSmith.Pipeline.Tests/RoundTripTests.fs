module StringSmith.Pipeline.Tests.RoundTripTests

open System
open System.IO
open Expecto
open Rocksmith2014.Common
open Rocksmith2014.DD
open Rocksmith2014.DLCProject
open Rocksmith2014.PSARC
open Rocksmith2014.SNG
open Rocksmith2014.XML
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion
open StringSmith.Pipeline
open StringSmith.Pipeline.Tests.Fixtures

let private request (dir: string) (audio: string) (platforms: Platform list) =
    let score = loadScore "full-song.gp5"
    let lead = score.Tracks |> List.find (fun t -> t.Name = "Emppu(Disto)")
    let bass = score.Tracks |> List.find (fun t -> t.Name = "Marco")
    // Fit the tab to the recording: the product's whole point, and it keeps every note
    // inside the song length so the structural checks are not about drift.
    let audioMs = (Rocksmith2014.Audio.Utils.getLength audio).TotalMilliseconds
    let nominal = TempoMap.nominalMs score
    let scale = (audioMs - 1500.0) / float (nominal score.EndTick)
    let sync = SyncMap.ofOffsetAndScale nominal score.EndTick 500.0<ms> scale
    { Score = score
      Arrangements =
        [ { Track = lead; Role = Lead; ToneKey = DefaultTones.guitarDistortion.Key }
          { Track = bass; Role = Bass; ToneKey = DefaultTones.bass.Key } ]
      Meta =
        { Title = "The Crow, the Owl and the Dove"; Artist = "Nightwish"; Album = "Imaginaerum"; Year = 2011
          TuningPitchHz = 440.0; AlbumArtPath = cover; Charter = "StringSmithTest" }
      Sync = sync
      AudioPath = audio
      PreviewStartMs = 5000
      Platforms = platforms
      OutputDir = Path.Combine(dir, "out")
      WorkDir = dir
      LevelCount = LevelCountGeneration.Simple
      Tones = DefaultTones.all
      WemEncoder = None }

let private has (manifest: string list) (part: string) (ext: string) =
    manifest |> List.filter (fun f -> f.Contains part && f.EndsWith ext) |> List.length

/// A build takes ~1.5 s on this machine. Ten minutes is not a performance assertion, it is
/// a liveness one: if the pipeline ever wedges, the suite must fail with a clear timeout
/// rather than spin forever. It did spin forever once — see the wrong-key test below.
let private buildTimeoutMs = 600_000

/// One build, shared by the checks below. Sequenced because the checks read its output.
let private built =
    lazy (
        let dir, audio = workDir true
        let progress = Collections.Generic.List<Progress>()
        let result =
            try
                Async.RunSynchronously(Build.run progress.Add (request dir audio [ PC; Mac ]), timeout = buildTimeoutMs)
            with :? TimeoutException ->
                failtestf "Build.run did not finish within %d ms" buildTimeoutMs
        dir, progress, result)

let private output () =
    match built.Force() with
    | _, _, Ok o -> o
    | _, _, Error e -> failtestf "build failed at %A: %s" e.Stage e.Message

let private packageFor (o: BuildOutput) (suffix: string) =
    o.Packages |> List.find (fun p -> p.EndsWith(suffix + ".psarc"))

[<Tests>]
let roundTrip =
    testSequenced <| testList "headless round trip" [
        test "the build succeeds and produces one package per platform" {
            let o = output ()
            Expect.equal o.Packages.Length 2 "two packages"
            Expect.isTrue (File.Exists(packageFor o "_p")) "PC package on disk"
            Expect.isTrue (File.Exists(packageFor o "_m")) "Mac package on disk"
            Expect.isTrue (o.DlcKey.Length >= 5) "dlc key"
            Expect.equal o.Arrangements.Length 2 "two arrangement reports"
        }

        test "progress covered every stage in order and ended at 100" {
            let _, progress, _ = built.Force()
            let stages = progress |> Seq.map (fun p -> p.Stage) |> Seq.distinct |> List.ofSeq
            Expect.equal stages [ Preparing; Converting; MeasuringLoudness; CreatingPreview; EncodingAudio; Packaging; Validating ] "stage order"
            Expect.isTrue (progress |> Seq.forall (fun p -> p.Percent >= 0.0 && p.Percent <= 100.0)) "bounded"
            Expect.floatClose Accuracy.low (progress |> Seq.last).Percent 100.0 "done"
            Expect.isTrue (progress |> Seq.pairwise |> Seq.forall (fun (a, b) -> b.Percent >= a.Percent - 0.01)) "monotonic"
        }

        test "drift is reported as OffsetAndScale with the tab fitted to the audio" {
            let o = output ()
            Expect.equal o.Drift.Status OffsetAndScale "two anchors"
            Expect.isLessThan (abs (float o.Drift.MismatchMs)) 2000.0 "fitted within the margin we left"
            Expect.isNonEmpty o.Drift.RampedBars "Nightwish ramps bars 85-94"
        }

        test "arrangement reports carry stats, warnings and the library's coded issues" {
            let o = output ()
            for r in o.Arrangements do
                // Stats split singles from chords; a distorted rhythm part can be all chords.
                Expect.isGreaterThan (r.Stats.Notes + r.Stats.Chords) 0 $"{r.Role.Name} has content"
                Expect.isGreaterThan r.Stats.Ebeats 100 $"{r.Role.Name} has a beat map"
                Expect.isTrue (File.Exists r.XmlPath) "xml on disk"
                Expect.contains r.Warnings NoFingeringInSource "community file: no fingering, and we said so"
            let bass = o.Arrangements |> List.find (fun r -> r.Role = Bass)
            // Marco's bass is C G C F (MIDI 24 31 36 41): drop-C to match the drop-C guitars.
            // Against standard bass E A D G (28 33 38 43) that is -4 -2 -2 -2; slots 4 and 5 unused.
            Expect.equal (List.ofArray bass.Tuning) [ -4s; -2s; -2s; -2s; 0s; 0s ] "Marco's bass is drop-C, as the file says"
        }

        testAsync "PC package carries the PC paths and nothing Mac" {
            let o = output ()
            use psarc = PSARC.OpenFile(packageFor o "_p")
            let m = psarc.Manifest
            Expect.equal (has m "songs/bin/generic/" ".sng") 2 "two SNGs under generic"
            Expect.equal (has m "songs/bin/macos/" ".sng") 0 "nothing under macos"
            Expect.equal (has m "audio/windows/" ".wem") 2 "song + preview wem"
            Expect.equal (has m "audio/windows/" ".bnk") 2 "two soundbanks"
            Expect.equal (has m "audio/mac/" ".wem") 0 "no mac audio"
            Expect.equal (has m "gfxassets/album_art/" ".dds") 3 "64/128/256 cover art"
            Expect.equal (has m "manifests/" ".json") 2 "one manifest per arrangement"
            Expect.equal (has m "manifests/" ".hsan") 1 "header"
            Expect.equal (has m "gamexblocks/" ".xblock") 1 "xblock"
            Expect.equal (has m "" "_aggregategraph.nt") 1 "aggregate graph"
            Expect.equal (has m "songs/arr/" "_showlights.xml") 1 "generated showlights"
            Expect.contains m "flatmodels/rs/rsenumerable_song.flat" "flat model (from the vendor shim's embedded resource)"
            Expect.contains m "appid.appid" "app id"
            Expect.contains m "toolkit.version" "toolkit version"
        }

        testAsync "Mac package carries the Mac paths and nothing PC" {
            let o = output ()
            use psarc = PSARC.OpenFile(packageFor o "_m")
            let m = psarc.Manifest
            Expect.equal (has m "songs/bin/macos/" ".sng") 2 "two SNGs under macos"
            Expect.equal (has m "songs/bin/generic/" ".sng") 0 "nothing under generic"
            Expect.equal (has m "audio/mac/" ".wem") 2 "mac audio"
            Expect.equal (has m "audio/mac/" ".bnk") 2 "mac soundbanks"
            Expect.equal (has m "audio/windows/" ".wem") 0 "no windows audio"
        }

        testAsync "each platform's SNG decrypts with its own key, has DD levels, and its hardest level is exactly what was packed" {
            let o = output ()
            let lead = o.Arrangements |> List.find (fun r -> r.Role = Lead)
            // The XML PackageBuilder actually compiled: phrases + DD applied (SaveDebugFiles).
            let debugXml =
                match Path.ChangeExtension(lead.XmlPath, "debug.xml") with
                | null -> failtest "no debug xml path"
                | d -> d
            let packed = InstrumentalArrangement.Load debugXml
            Expect.isGreaterThan packed.Levels.Count 1 "DD produced levels in the XML"
            Expect.isGreaterThan packed.Phrases.Count 2 "COUNT, content phrases, END"
            let hardest = packed.Levels.[packed.Levels.Count - 1]
            let expectedEntities = hardest.Notes.Count + hardest.Chords.Count // a chord is one SNG note entry

            for platform, suffix, dir in [ PC, "_p", "generic"; Mac, "_m", "macos" ] do
                use psarc = PSARC.OpenFile(packageFor o suffix)
                let entry = psarc.Manifest |> List.find (fun f -> f.StartsWith $"songs/bin/{dir}/" && f.EndsWith "_lead.sng")
                use stream = psarc.GetEntryStream entry |> Async.AwaitTask |> Async.RunSynchronously
                let sng = SNG.fromStream stream platform |> Async.RunSynchronously
                Expect.equal sng.Levels.Length packed.Levels.Count $"{suffix}: level count matches the packed XML"
                Expect.isGreaterThan sng.Levels.Length 1 $"{suffix}: DD levels in the SNG"
                let top = sng.Levels.[sng.Levels.Length - 1]
                Expect.equal top.Notes.Length expectedEntities $"{suffix}: hardest level has every note and chord we packed"
                Expect.exists sng.Phrases (fun p -> p.Name = "COUNT") $"{suffix}: COUNT phrase"
                Expect.exists sng.Phrases (fun p -> p.Name = "END") $"{suffix}: END phrase"
                // first note time agrees to the millisecond (SNG stores seconds as float32)
                let firstXml = Seq.append (hardest.Notes |> Seq.map (fun n -> n.Time)) (hardest.Chords |> Seq.map (fun c -> c.Time)) |> Seq.min
                let firstSng = top.Notes |> Array.map (fun n -> n.Time) |> Array.min
                Expect.floatClose Accuracy.medium (float firstSng) (float firstXml / 1000.0) $"{suffix}: first note time"
        }

        test "the two platforms' SNGs hold the same plaintext under different keys" {
            // This replaces an earlier test that decrypted the Mac SNG with the PC key and
            // asserted the result did not parse. That test was unsound and it hung: SNG's
            // reader does `Array.init (reader.ReadInt32())` (BinaryHelpers.fs:28), so a
            // garbage length field asks for up to 2^31 elements. On Linux the allocation
            // threw quickly and the test passed; on a memory-constrained arm64 Mac the same
            // bytes sent the process into an unbounded GC thrash that ran over 20 minutes
            // at 100% CPU and never returned. Never hand a wrong key to a binary parser.
            //
            // The property that actually matters is provable without parsing anything:
            // identical plaintext encrypted under two different keys must differ. The
            // header and IV are written in the clear, so the divergence starts right after
            // them, which also re-confirms the 16-byte zero IV documented in CLAUDE.md.
            let o = output ()
            let sngBytes (suffix: string) =
                use psarc = PSARC.OpenFile(packageFor o suffix)
                let name = psarc.Manifest |> List.find (fun f -> f.EndsWith "_lead.sng")
                use mem = new MemoryStream()
                psarc.InflateFile(name, mem).Wait()
                mem.ToArray()

            let pc = sngBytes "_p"
            let mac = sngBytes "_m"

            Expect.equal mac.Length pc.Length "same plaintext, so same ciphertext length"
            Expect.isGreaterThan pc.Length 1000 "a real SNG, not an empty entry"

            // 8-byte header (uint32 magic 0x4A, uint32 header) then the 16-byte IV.
            let headerAndIv = 24
            Expect.equal pc.[0] 0x4Auy "SNG magic"
            Expect.sequenceEqual (Array.sub mac 0 headerAndIv) (Array.sub pc 0 headerAndIv) "header and IV are plaintext and identical"
            Expect.sequenceEqual (Array.sub pc 8 16) (Array.zeroCreate 16) "the IV is 16 zero bytes"

            Expect.notEqual (Array.sub mac headerAndIv (mac.Length - headerAndIv)) (Array.sub pc headerAndIv (pc.Length - headerAndIv))
                "ciphertext must differ: the per-platform key was applied"

            // Not a one-byte fluke: a key change should scramble essentially everything.
            let differing =
                Seq.zip (Seq.skip headerAndIv pc) (Seq.skip headerAndIv mac)
                |> Seq.filter (fun (a, b) -> a <> b)
                |> Seq.length
            let ratio = float differing / float (pc.Length - headerAndIv)
            Expect.isGreaterThan ratio 0.9 $"expected almost every ciphertext byte to differ, got %.1f{ratio * 100.0}%%"
        }

        test "an independent reader agrees the container is well formed" {
            // Every other check here reads the package back with the same library that wrote
            // it, so a writer and reader that are wrong together would both pass. This one
            // shells out to scripts/inspect-psarc.py, which parses the container straight from
            // the on-disk format and decrypts the table of contents with the openssl CLI: no
            // shared code, no shared AES. It verifies the header geometry, that entry data
            // tiles the file with no gap, overlap or tail, that every block size table slot
            // belongs to exactly one entry, that every entry inflates to its declared length,
            // that the name digests match, and that the header manifest carries the attribute
            // set a shipped package carries. It is calibrated against a package built from
            // Rocksmith2014.NET's own integration-test project, on which it reports zero.
            let script =
                Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "scripts", "inspect-psarc.py"))
            Expect.isTrue (File.Exists script) "the inspector script is in the repository"

            let run (fileName: string) (args: string) =
                try
                    let psi = Diagnostics.ProcessStartInfo(fileName, args,
                                                           RedirectStandardOutput = true,
                                                           RedirectStandardError = true)
                    match Diagnostics.Process.Start psi with
                    | null -> None
                    | proc ->
                        use proc = proc
                        let out = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()
                        proc.WaitForExit()
                        Some(proc.ExitCode, out)
                with _ ->
                    None

            match run "python3" "--version", run "openssl" "version" with
            | None, _ | _, None ->
                skiptest "python3 or the openssl CLI is not available"
            | _ ->
                let o = output ()
                // One package at a time: the PC and Mac builds of one song deliberately share
                // a DLC key, which the inspector reports as a collision when given both.
                for package in o.Packages do
                    match run "python3" $"\"{script}\" \"{package}\"" with
                    | None ->
                        skiptest "python3 could not be started"
                    | Some(0, _) ->
                        ()
                    | Some(_, out) ->
                        failtestf "%s failed the independent structural check:\n%s"
                            (Path.GetFileName package) out
        }
    ]

[<Tests>]
let identity =
    testSequenced <| testList "package identity is stable across rebuilds" [
        test "a second build of the same song produces the same key and the same arrangement IDs" {
            // Rocksmith keys profile score data on PersistentID. Before this was made
            // deterministic every build minted fresh IDs, so every reinstall was a new song
            // to the profile and the old one stayed behind.
            let first = output ()

            let dir, audio = workDir true
            let second =
                match Async.RunSynchronously(Build.run ignore (request dir audio [ PC ]), timeout = buildTimeoutMs) with
                | Ok o -> o
                | Error e -> failtestf "second build failed at %A: %s" e.Stage e.Message

            Expect.equal second.DlcKey first.DlcKey "the DLC key is derived, not random"

            let identities (o: BuildOutput) =
                use psarc = PSARC.OpenFile(packageFor o "_p")
                let name = psarc.Manifest |> List.find (fun f -> f.EndsWith ".hsan")
                use stream = psarc.GetEntryStream name |> Async.AwaitTask |> Async.RunSynchronously
                use doc = Text.Json.JsonDocument.Parse(stream)
                [ for entry in doc.RootElement.GetProperty("Entries").EnumerateObject() ->
                    let attrs = entry.Value.GetProperty("Attributes")
                    attrs.GetProperty("ArrangementName").GetString(),
                    entry.Name,
                    attrs.GetProperty("MasterID_RDV").GetInt32() ]
                |> List.sort

            let a = identities first
            let b = identities second
            Expect.equal a.Length 2 "two arrangements"
            Expect.equal b a "arrangement name, PersistentID and MasterID all survive a rebuild"

            let persistentIds = a |> List.map (fun (_, pid, _) -> pid)
            Expect.equal (List.distinct persistentIds).Length 2 "the two arrangements do not share an ID"
            let masterIds = a |> List.map (fun (_, _, mid) -> mid)
            Expect.isTrue (masterIds |> List.forall (fun m -> m > 0)) "MasterIDs are positive, as RandomGenerator.next would give"
        }

        test "the derived IDs match what the package actually carries" {
            let o = output ()
            for role in [ Lead; Bass ] do
                let expectedPid = ArrangementIdentity.persistentId o.DlcKey role 0
                let expectedMid = ArrangementIdentity.masterId o.DlcKey role 0
                use psarc = PSARC.OpenFile(packageFor o "_p")
                let name = psarc.Manifest |> List.find (fun f -> f.EndsWith ".hsan")
                use stream = psarc.GetEntryStream name |> Async.AwaitTask |> Async.RunSynchronously
                use doc = Text.Json.JsonDocument.Parse(stream)
                let entry =
                    doc.RootElement.GetProperty("Entries").EnumerateObject()
                    |> Seq.find (fun e -> e.Value.GetProperty("Attributes").GetProperty("ArrangementName").GetString() = role.Name)
                Expect.equal (Guid(entry.Name)) expectedPid $"{role.Name}: PersistentID is the derived one"
                Expect.equal (entry.Value.GetProperty("Attributes").GetProperty("MasterID_RDV").GetInt32()) expectedMid $"{role.Name}: MasterID is the derived one"
        }

        test "an occurrence counter keeps two tracks in one role apart" {
            let a = ArrangementIdentity.persistentId "TestKey" Rhythm 0
            let b = ArrangementIdentity.persistentId "TestKey" Rhythm 1
            Expect.notEqual a b "the second Rhythm track gets its own PersistentID"
            Expect.notEqual (ArrangementIdentity.masterId "TestKey" Rhythm 0)
                            (ArrangementIdentity.masterId "TestKey" Rhythm 1) "and its own MasterID"
            Expect.notEqual (ArrangementIdentity.persistentId "TestKey" Lead 0)
                            (ArrangementIdentity.persistentId "TestKey" Rhythm 0) "roles differ"
            Expect.notEqual (ArrangementIdentity.persistentId "OtherKey" Lead 0)
                            (ArrangementIdentity.persistentId "TestKey" Lead 0) "songs differ"
        }

        test "the derived DLC key agrees with the library's for ordinary metadata" {
            // The derivation only diverges from DLCKey.create where upstream would reach for
            // RandomGenerator: a charter name with fewer than two alphanumeric characters, or
            // a key shorter than the minimum. Anything else must come out identical, or
            // installing over an older StringSmith package would silently miss.
            for charter, artist, title in
                [ "StringSmithTest", "Nightwish", "The Crow, the Owl and the Dove"
                  "matt", "Metallica", "Master of Puppets"
                  "AB", "AC/DC", "T.N.T."
                  "xy", "Rush", "2112" ] do
                Expect.equal
                    (ArrangementIdentity.dlcKey charter artist title)
                    (DLCKey.create charter artist title)
                    $"{charter}/{artist}/{title}"

            // Where upstream reaches for RandomGenerator this pads from the digest, so the
            // two deliberately differ. What is asserted is that the result is long enough
            // and, unlike upstream's, the same every time. "Charter"/"a"/"b" yields "Chab",
            // one short of the minimum, which is exactly this case.
            for charter, artist, title in [ "!", "", ""; "Charter", "a", "b"; "", "Q", "Z" ] do
                let key () = ArrangementIdentity.dlcKey charter artist title
                Expect.equal (key ()) (key ()) $"{charter}/{artist}/{title}: stable"
                Expect.isGreaterThanOrEqual (key ()).Length DLCKey.MinimumLength
                    $"{charter}/{artist}/{title}: long enough"
                Expect.isTrue (key () |> Seq.forall Char.IsLetterOrDigit)
                    $"{charter}/{artist}/{title}: alphanumeric, as the game requires"
        }
    ]

[<Tests>]
let failure =
    testList "failure leaves work recoverable" [
        test "missing WEMs with no encoder fails at EncodingAudio, after both arrangements were converted to disk" {
            let dir, audio = workDir false
            let progress = Collections.Generic.List<Progress>()
            match Async.RunSynchronously(Build.run progress.Add (request dir audio [ PC ]), timeout = buildTimeoutMs) with
            | Ok _ -> failtest "should have failed"
            | Error e ->
                Expect.equal e.Stage EncodingAudio "stage named"
                Expect.stringContains e.Message "song.wem" "says which file"
                Expect.stringContains e.Message "Wwise" "says what to install"
                Expect.equal e.Completed.Length 2 "both arrangements completed"
                for r in e.Completed do
                    Expect.isTrue (File.Exists r.XmlPath) "converted XML kept on disk"
                Expect.equal e.WorkDir dir "work dir reported"
        }
    ]
