module StringSmith.App.Program

open System
open Avalonia

[<EntryPoint; STAThread>]
let main (args: string array) =
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args)
