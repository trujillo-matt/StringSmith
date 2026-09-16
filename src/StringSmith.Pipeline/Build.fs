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

/// Stable identities for a package and its arrangements.
///
/// Rocksmith keys a profile's score data on an arrangement's PersistentID and its song
/// list on the MasterID. Upstream treats changing either as a destructive act: it
/// regenerates them only when `PhraseLevelComparer` finds the DD levels came out easier
/// than what the profile already recorded, and then only after asking the user
/// (`PackageBuilder.IdResetConfig`). DLC Builder can afford that because it keeps the IDs
/// in the project file.
///
/// StringSmith has no project file, so it derives the IDs from the package identity
/// instead. Rebuilding the same tab, by the same charter, for the same role produces the
/// same IDs, so reinstalling replaces the song in the profile rather than adding another
/// copy of it. Before this, every build minted fresh IDs and every install accumulated.
module ArrangementIdentity =

    /// A stable 16-byte digest. MD5 is used here as a hash, not as a security primitive:
    /// what matters is that it is fixed across runs, machines and framework versions,
    /// which `GetHashCode` explicitly is not.
    let private digest (seed: string) =
        Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes seed)

    /// Versioned so the derivation can be changed later without silently colliding with
    /// IDs already written into someone's profile.
    let private seed (dlcKey: string) (role: ArrangementRole) (occurrence: int) =
        let key = dlcKey.ToLowerInvariant()
        let name = role.Name.ToLowerInvariant()
        $"stringsmith/id/v1/%s{key}/%s{name}/%d{occurrence}"

    /// Replaces `Guid.NewGuid()`. `occurrence` is 0 for the first arrangement in a role and
    /// counts up from there, so two tracks mapped to the same role still get distinct IDs.
    let persistentId dlcKey role occurrence =
        Guid(digest (seed dlcKey role occurrence))

    /// Replaces `RandomGenerator.next ()`, which returns a non-negative Int32. The sign bit
    /// is cleared to match that, and 0 is avoided because the manifest writes -1 and 0 is
    /// close enough to a sentinel to be worth stepping around.
    let masterId dlcKey role occurrence =
        let d = digest (seed dlcKey role occurrence + "/master")
        match BitConverter.ToInt32(d, 0) &&& 0x7FFFFFFF with
        | 0 -> 1
        | v -> v

    /// The DLC key, derived the way `DLCKey.create` derives it, with one difference: where
    /// upstream falls back to `RandomGenerator` (a charter name with fewer than two
    /// alphanumeric characters, or a key that comes out shorter than the minimum) this pads
    /// from the digest. A random tail would change the whole package identity on every
    /// rebuild, which is the thing this module exists to stop. For any metadata that gives
    /// upstream enough characters to work with, the two agree exactly.
    let dlcKey (charter: string) (artist: string) (title: string) =
        let part (s: string) =
            let v = StringValidator.dlcKey s
            v.Substring(0, min 5 v.Length)

        let prefix =
            let name = StringValidator.dlcKey charter
            if name.Length >= 2 then name.Substring(0, 2) else "ss"

        let key = prefix + part artist + part title

        if key.Length >= DLCKey.MinimumLength then
            key
        else
            let letters =
                digest $"stringsmith/key/v1/%s{charter}/%s{artist}/%s{title}"
                |> Array.map (fun b -> char (int 'a' + int b % 26))
                |> String
            key + letters.Substring(0, DLCKey.MinimumLength - key.Length)

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
                let dlcKey = ArrangementIdentity.dlcKey req.Meta.Charter req.Meta.Artist req.Meta.Title
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
                            let seen = Dictionary<ArrangementRole, int>()
                            List.zip req.Arrangements (List.ofSeq completed)
                            |> List.map (fun (a, rep) ->
                                // Position within this role, so a second track mapped to the
                                // same role gets its own identity instead of colliding.
                                let occurrence =
                                    match seen.TryGetValue a.Role with
                                    | true, n -> n
                                    | _ -> 0
                                seen[a.Role] <- occurrence + 1

                                Instrumental
                                    { Instrumental.Empty with
                                        XmlPath = rep.XmlPath
                                        Name = arrangementName a.Role
                                        RouteMask = routeMask a.Role
                                        Tuning = rep.Tuning
                                        TuningPitch = req.Meta.TuningPitchHz
                                        BaseTone = a.ToneKey
                                        MasterId = ArrangementIdentity.masterId dlcKey a.Role occurrence
                                        PersistentId = ArrangementIdentity.persistentId dlcKey a.Role occurrence }) }

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
