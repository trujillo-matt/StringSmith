module StringSmith.Audio.Tests.FakeEnv

open StringSmith.Audio

/// A filesystem and process table as data.
let make (files: string list) (dirs: Map<string, string list>) (envVars: Map<string, string>) (run: string -> string list -> Result<string, string>) (mac: bool) : Env =
    { FileExists = fun p -> List.contains p files
      DirectoryExists = fun d -> dirs.ContainsKey d
      SubDirectories = fun d -> dirs |> Map.tryFind d |> Option.defaultValue []
      EnvVar = fun n -> envVars |> Map.tryFind n
      Run = run
      IsMacOS = mac }

let noRun _ _ = Error "no processes in this fake"
