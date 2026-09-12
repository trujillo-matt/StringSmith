module StringSmith.Audio.Tests.ProbeTests

open Expecto
open StringSmith.Audio
open StringSmith.Audio.Tests.FakeEnv

[<Tests>]
let ffmpeg =
    testList "Probe: ffmpeg" [
        test "on macOS, Homebrew dirs are searched before PATH and the version is parsed" {
            let env =
                make [ "/opt/homebrew/bin/ffmpeg" ] Map.empty (Map [ "PATH", "/usr/bin:/bin" ])
                     (fun exe _ -> if exe = "/opt/homebrew/bin/ffmpeg" then Ok "ffmpeg version 7.1.1 Copyright (c) 2000-2025\nbuilt with clang" else Error "nope") true
            Expect.equal (Probe.findFFmpeg env) (Found("/opt/homebrew/bin/ffmpeg", "7.1.1")) "found with version"
            Expect.equal (Probe.candidates env "ffmpeg" |> List.head) "/opt/homebrew/bin/ffmpeg" "homebrew first"
        }

        test "missing tool lists every place it looked" {
            let env = make [] Map.empty (Map [ "PATH", "/usr/bin:/bin" ]) noRun true
            match Probe.findFFmpeg env with
            | Missing searched ->
                Expect.contains searched "/opt/homebrew/bin/ffmpeg" "homebrew arm"
                Expect.contains searched "/usr/local/bin/ffmpeg" "homebrew intel"
                Expect.contains searched "/usr/bin/ffmpeg" "path"
            | Found _ -> failtest "should be missing"
        }

        test "a tool that exists but will not run still counts as found" {
            let env = make [ "/usr/bin/ffprobe" ] Map.empty (Map [ "PATH", "/usr/bin" ]) noRun false
            Expect.equal (Probe.findFFprobe env) (Found("/usr/bin/ffprobe", "unknown version")) "found, version unknown"
        }

        test "version parsing tolerates both ffmpeg and ffprobe banners" {
            Expect.equal (Probe.parseFfmpegVersion "ffprobe version n6.0 Copyright") "n6.0" "ffprobe"
            Expect.equal (Probe.parseFfmpegVersion "ffmpeg version 2024-01-01-git-abc Copyright") "2024-01-01-git-abc" "git build"
        }
    ]

[<Tests>]
let wwise =
    testList "Probe: Wwise" [
        let cli v = $"/Applications/Audiokinetic/{v}/Wwise.app/Contents/Tools/WwiseConsole.sh"

        test "finds the newest installed version, including 2024 which the library's regex rejects" {
            let dirs = Map [ "/Applications/Audiokinetic", [ "/Applications/Audiokinetic/Wwise2023.1.3.8471"; "/Applications/Audiokinetic/Wwise2024.1.0.8669" ] ]
            let env = make [ cli "Wwise2023.1.3.8471"; cli "Wwise2024.1.0.8669" ] dirs Map.empty noRun true
            Expect.equal (Probe.findWwise env) (Found(cli "Wwise2024.1.0.8669", "Wwise2024.1.0.8669")) "2024 wins"
        }

        test "a version directory without the console script is skipped" {
            let dirs = Map [ "/Applications/Audiokinetic", [ "/Applications/Audiokinetic/Wwise2024.1.0.8669"; "/Applications/Audiokinetic/Wwise2021.1.0.7575" ] ]
            let env = make [ cli "Wwise2021.1.0.7575" ] dirs Map.empty noRun true
            Expect.equal (Probe.findWwise env) (Found(cli "Wwise2021.1.0.7575", "Wwise2021.1.0.7575")) "falls back to the one that has it"
        }

        test "WWISEROOT overrides the Applications scan" {
            let root = "/Users/me/wwise/Wwise2022.1.0"
            let env = make [ root + "/Wwise.app/Contents/Tools/WwiseConsole.sh" ] Map.empty (Map [ "WWISEROOT", root ]) noRun true
            Expect.equal (Probe.findWwise env) (Found(root + "/Wwise.app/Contents/Tools/WwiseConsole.sh", "Wwise2022.1.0")) "env root"
        }

        test "non-Wwise folders under Audiokinetic are ignored" {
            let dirs = Map [ "/Applications/Audiokinetic", [ "/Applications/Audiokinetic/Launcher"; "/Applications/Audiokinetic/Wwise2023.1.0" ] ]
            let env = make [ cli "Wwise2023.1.0" ] dirs Map.empty noRun true
            Expect.equal (Probe.findWwise env) (Found(cli "Wwise2023.1.0", "Wwise2023.1.0")) "launcher skipped"
        }

        test "nothing installed: Missing names the Audiokinetic folder" {
            match Probe.findWwise (make [] Map.empty Map.empty noRun true) with
            | Missing searched -> Expect.contains searched "/Applications/Audiokinetic" "where to install"
            | Found _ -> failtest "should be missing"
        }

        test "describeMissing is one plain sentence per missing tool and empty when all present" {
            let none = make [] Map.empty Map.empty noRun true
            let msgs = Probe.describeMissing (Probe.all none)
            Expect.equal msgs.Length 3 "three missing"
            Expect.all msgs (fun m -> m.Contains "not found" && m.Contains "Looked in") "phrasing"
            let full = { FFmpeg = Found("a", "1"); FFprobe = Found("b", "1"); Wwise = Found("c", "2024") }
            Expect.isEmpty (Probe.describeMissing full) "nothing to say"
            Expect.isTrue full.AllPresent "all present"
        }
    ]

[<Tests>]
let normalise =
    testList "Normalise" [
        test "wav/ogg/flac pass through untouched" {
            for f in [ "a.wav"; "b.OGG"; "c.flac" ] do
                Expect.equal (Normalise.toWav (make [] Map.empty Map.empty noRun true) None f "/tmp") (Ok f) f
        }

        test "mp3 without ffmpeg is a plain-language error" {
            match Normalise.toWav (make [] Map.empty Map.empty noRun true) None "song.mp3" "/tmp" with
            | Error e -> Expect.stringContains e "FFmpeg is not available" "says why"
            | Ok _ -> failtest "should fail"
        }

        test "mp3 with ffmpeg runs the expected command and returns the wav" {
            let mutable called = []
            let run exe args = called <- (exe, args) :: called; Ok ""
            let env = make [ "/w/song.wav" ] Map.empty Map.empty run true
            let r = Normalise.toWav env (Some "/opt/homebrew/bin/ffmpeg") "/in/song.mp3" "/w"
            Expect.equal r (Ok "/w/song.wav") "output path"
            Expect.equal called [ "/opt/homebrew/bin/ffmpeg", Normalise.ffmpegArgs "/in/song.mp3" "/w/song.wav" ] "one call, exact args"
            Expect.contains (Normalise.ffmpegArgs "i" "o") "pcm_s16le" "16-bit PCM"
        }
    ]
