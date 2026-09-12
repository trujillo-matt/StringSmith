namespace StringSmith.Pipeline

open System
open System.Collections.Generic
open System.IO
open Rocksmith2014.Common
open Rocksmith2014.DD
open Rocksmith2014.DLCProject
open Rocksmith2014.DLCProject.PackageBuilder
open Rocksmith2014.XML
open Rocksmith2014.XML.Processing
open StringSmith.Core
open StringSmith.Sync
open StringSmith.Conversion

module Build =

    let private flatten (e: exn) =
        let rec go (e: exn) =
            match e with
            | :? AggregateException as a -> a.InnerExceptions |> Seq.collect go |> List.ofSeq
            | e ->
                e.Message :: (match e.InnerException with null -> [] | inner -> go inner)
        go e |> List.distinct |> String.concat " / "

    let private arrangementName = function
        | Lead -> ArrangementName.Lead
        | Rhythm -> ArrangementName.Rhythm
        | Bass -> ArrangementName.Bass

    let private routeMask = function
        | Lead -> RouteMask.Lead
        | Rhythm -> RouteMask.Rhythm
        | Bass -> RouteMask.Bass

    /// Stage weights for the overall percentage. Packaging dominates because DD and
    /// PSARC compression do the real work.
    let private weights =
        [ Preparing, 2.0; Converting, 8.0; MeasuringLoudness, 5.0; CreatingPreview, 5.0
          EncodingAudio, 25.0; Packaging, 50.0; Validating, 5.0 ]

    let private percentAt (stage: Stage) (within: float) =
        let before = weights |> List.takeWhile (fun (s, _) -> s <> stage) |> List.sumBy snd
        let own = weights |> List.find (fun (s, _) -> s = stage) |> snd
        before + own * Math.Clamp(within, 0.0, 1.0)

    let private requireWem (audioPath: string) =
        let wem = Path.ChangeExtension(audioPath, "wem")
        if not (File.Exists wem) then
            failwithf "No WEM encoder is available and %s does not exist. Install Wwise, or place a pre-encoded .wem next to the audio." (Path.GetFileName wem)

    /// Runs the whole build. Never throws: every failure is a BuildFailure naming the
    /// stage, in plain language, with the work directory and completed arrangements.
    let run (report: Progress -> unit) (req: BuildRequest) : Async<Result<BuildOutput, BuildFailure>> =
        async {
            let completed = List<ArrangementReport>()
            let mutable current = Preparing
            // PackageBuilder reports from parallel per-platform tasks, so raw reports can
            // arrive a tick out of order. A progress bar must never move backwards: serialise
            // reporting and clamp to the highest value seen.
            let gate = obj ()
            let mutable lastPercent = 0.0
            let stage (s: Stage) within msg =
                lock gate (fun () ->
                    current <- s
                    let pct = max lastPercent (percentAt s within)
                    lastPercent <- pct
                    report { Stage = s; Percent = pct; Message = msg })

            try
                // ---- prepare
                stage Preparing 0.0 Preparing.Label
                Directory.CreateDirectory req.WorkDir |> ignore
                Directory.CreateDirectory req.OutputDir |> ignore
                // Work on a copy so nothing is ever written next to the user's own file
                // (the WEM encoder writes alongside its input).
                let ext = match Path.GetExtension req.AudioPath with null -> ".wav" | e -> e.ToLowerInvariant()
                let workAudio = Path.Combine(req.WorkDir, "song" + ext)
                if Path.GetFullPath workAudio <> Path.GetFullPath req.AudioPath then
                    File.Copy(req.AudioPath, workAudio, true)
                let audioLengthMs = int (Rocksmith2014.Audio.Utils.getLength workAudio).TotalMilliseconds

                // ---- convert
                let nominal = TempoMap.nominalMs req.Score
                let toAudio = SyncMap.toAudioMs nominal req.Sync
                let toMs (t: int64<tick>) = int (Math.Round(float (toAudio t)))
                let drift = DriftReport.create req.Score req.Sync (float audioLengthMs * 1.0<ms>)

                let n = float req.Arrangements.Length
                req.Arrangements
                |> List.iteri (fun i a ->
                    stage Converting (float i / n) $"Converting {a.Role.Name} from track '{a.Track.Name}'"
                    let meta : ArrangementMeta =
                        { Title = req.Meta.Title; Artist = req.Meta.Artist; Album = req.Meta.Album; Year = req.Meta.Year
                          TuningPitchHz = req.Meta.TuningPitchHz; Role = a.Role; Part = 1s; SongLengthMs = audioLengthMs }
                    let r = Convert.toArrangement ConversionOptions.Default meta toMs req.Score a.Track
                    let xmlPath = Path.Combine(req.WorkDir, $"arr_{a.Role.Name.ToLowerInvariant()}.xml")
                    r.Arrangement.Save xmlPath
                    completed.Add
                        { Role = a.Role
                          XmlPath = xmlPath
                          Tuning = Array.copy r.Arrangement.MetaData.Tuning.Strings
                          Warnings = r.Warnings
                          Stats = r.Stats
                          Issues = ArrangementChecker.checkInstrumental r.Arrangement })

                // ---- loudness + preview
                stage MeasuringLoudness 0.0 MeasuringLoudness.Label
                let volume = Rocksmith2014.Audio.Volume.calculate workAudio
                stage CreatingPreview 0.0 CreatingPreview.Label
                let previewPath = Rocksmith2014.Audio.Utils.createPreviewAudioPath workAudio
                Rocksmith2014.Audio.Preview.create workAudio previewPath (TimeSpan.FromMilliseconds(float req.PreviewStartMs))
                let previewVolume = Rocksmith2014.Audio.Volume.calculate previewPath

                // ---- wem
                match req.WemEncoder with
                | Some encode ->
                    stage EncodingAudio 0.0 "Encoding song audio with Wwise"
                    do! encode workAudio
                    stage EncodingAudio 0.5 "Encoding preview audio with Wwise"
                    do! encode previewPath
                | None ->
                    stage EncodingAudio 0.0 "Using pre-encoded WEM files"
                    requireWem workAudio
                    requireWem previewPath

                // ---- package
                stage Packaging 0.0 Packaging.Label
                let dlcKey = DLCKey.create req.Meta.Charter req.Meta.Artist req.Meta.Title
                let project =
                    { DLCProject.Empty with
                        DLCKey = dlcKey
                        Author = Some req.Meta.Charter
                        ArtistName = SortableString.Create req.Meta.Artist
                        Title = SortableString.Create req.Meta.Title
                        AlbumName = SortableString.Create req.Meta.Album
                        Year = req.Meta.Year
                        AlbumArtFile = req.Meta.AlbumArtPath
                        AudioFile = { Path = workAudio; Volume = volume }
                        AudioFileLength = Some(TimeSpan.FromMilliseconds(float audioLengthMs))
                        AudioPreviewFile = { Path = previewPath; Volume = previewVolume }
                        AudioPreviewStartTime = Some(TimeSpan.FromMilliseconds(float req.PreviewStartMs))
                        Tones = req.Tones
                        Arrangements =
                            List.zip req.Arrangements (List.ofSeq completed)
                            |> List.map (fun (a, rep) ->
                                Instrumental
                                    { Instrumental.Empty with
                                        XmlPath = rep.XmlPath
                                        Name = arrangementName a.Role
                                        RouteMask = routeMask a.Role
                                        Tuning = rep.Tuning
                                        TuningPitch = req.Meta.TuningPitchHz
                                        BaseTone = a.ToneKey
                                        MasterId = RandomGenerator.next ()
                                        PersistentId = Guid.NewGuid() }) }

                let config : BuildConfig =
                    { Platforms = req.Platforms
                      BuilderVersion = "StringSmith 0.1"
                      Author = req.Meta.Charter
                      AppId = AppId.CherubRock
                      GenerateDD = true
                      // Our XML has no phrases by design; the library's generator is the one we want.
                      ForcePhraseCreation = true
                      DDConfig = { PhraseSearchThreshold = Some 80; LevelCountGeneration = req.LevelCount }
                      ApplyImprovements = true
                      // Writes <arr>.debug.xml with phrases and DD applied, which Validating reads back.
                      SaveDebugFiles = true
                      AudioConversionTask = async { return () }
                      IdResetConfig = None
                      ProgressReporter =
                        Some { new IProgress<float> with
                                 member _.Report v = stage Packaging (v / 100.0) Packaging.Label } }

                let target = WithoutPlatformOrExtension(Path.Combine(req.OutputDir, dlcKey))
                let! packages, _toneKeys = buildPackages target config project

                // ---- validate what was actually packed
                stage Validating 0.0 Validating.Label
                let reports =
                    completed
                    |> Seq.map (fun rep ->
                        match Path.ChangeExtension(rep.XmlPath, "debug.xml") with
                        | null -> rep
                        | debug when File.Exists debug ->
                            { rep with Issues = ArrangementChecker.checkInstrumental (InstrumentalArrangement.Load debug) }
                        | _ -> rep)
                    |> List.ofSeq
                stage Validating 1.0 "Done"

                return Ok
                    { Packages = List.ofArray packages
                      DlcKey = dlcKey
                      Arrangements = reports
                      Drift = drift
                      WorkDir = req.WorkDir }
            with e ->
                return Error
                    { Stage = current
                      Message = $"{current.Label} failed: {flatten e}"
                      WorkDir = req.WorkDir
                      Completed = List.ofSeq completed }
        }
