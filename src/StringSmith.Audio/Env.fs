namespace StringSmith.Audio

open System
open System.Diagnostics
open System.IO

/// The process and filesystem boundary, as data, so everything above it is testable.
type Env =
    {
        FileExists: string -> bool
        DirectoryExists: string -> bool
        /// Immediate subdirectories (full paths) of a directory, or [] if absent.
        SubDirectories: string -> string list
        EnvVar: string -> string option
        /// Runs a process to completion. Ok stdout, or Error with exit code and stderr.
        Run: string -> string list -> Result<string, string>
        IsMacOS: bool
    }

module Env =
    let private run (exe: string) (args: string list) : Result<string, string> =
        try
            let psi = ProcessStartInfo(exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true)
            for a in args do psi.ArgumentList.Add a
            use p = Process.Start psi
            match p with
            | null -> Error $"could not start {exe}"
            | p ->
                let out = p.StandardOutput.ReadToEnd()
                let err = p.StandardError.ReadToEnd()
                p.WaitForExit()
                if p.ExitCode = 0 then Ok out else Error $"{exe} exited {p.ExitCode}: {err.Trim()}"
        with e ->
            Error $"{exe}: {e.Message}"

    /// The real thing.
    let live : Env =
        { FileExists = File.Exists
          DirectoryExists = Directory.Exists
          SubDirectories =
            fun d ->
                if Directory.Exists d then Directory.EnumerateDirectories d |> List.ofSeq else []
          EnvVar = fun n -> Environment.GetEnvironmentVariable n |> Option.ofObj |> Option.filter (fun s -> s <> "")
          Run = run
          IsMacOS = OperatingSystem.IsMacOS() }
