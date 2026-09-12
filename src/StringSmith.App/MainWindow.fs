namespace StringSmith.App

open Avalonia.Controls
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
open Elmish

type MainWindow() as this =
    inherit HostWindow()

    do
        base.Title <- "StringSmith"
        base.Width <- 1120.0
        base.Height <- 880.0
        base.MinWidth <- 900.0
        base.MinHeight <- 640.0
        base.WindowStartupLocation <- WindowStartupLocation.CenterScreen

        // The window is passed to update (for native dialogs) and view by closure; the
        // model itself stays a plain record. Dispatch from background threads (build
        // progress) is marshalled to the UI thread by runWithAvaloniaSyncDispatch.
        Program.mkProgram Update.init (Update.update this) (Views.Main.view this)
        |> Program.withHost this
        |> Program.runWithAvaloniaSyncDispatch ()
