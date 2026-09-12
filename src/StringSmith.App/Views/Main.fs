module StringSmith.App.Views.Main

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Input
open Avalonia.Layout
open StringSmith.Audio
open StringSmith.App
open StringSmith.App.Model
open StringSmith.App.Views.Widgets

/// One window, sections stacked with progressive disclosure, persistent bottom bar.
let view (_window: Window) (m: Model) (dispatch: Msg -> unit) : IView =
    let depsSummary =
        match m.Deps with
        | DepsChecking -> dim "Checking dependencies…"
        | DepsReady d ->
            let missing = Probe.describeMissing d
            if missing.IsEmpty then good "FFmpeg, ffprobe and Wwise found"
            else warn $"{missing.Length} dependency issue(s); see section 1"
    let running = match m.Build with BuildRunning _ -> true | _ -> false
    let canBuild = (Derive.buildBlockers m).IsEmpty && not running

    DockPanel.create [
        DockPanel.background "#161616"
        DockPanel.allowDrop true
        DockPanel.onDragEnter (fun e ->
            e.DragEffects <- if e.DataTransfer.Contains(DataFormat.File) then DragDropEffects.Copy else DragDropEffects.None)
        DockPanel.onDrop (fun e ->
            e.Handled <- true
            match e.DataTransfer.TryGetFiles() with
            | null -> ()
            | files -> files |> Seq.map (fun f -> f.Path.LocalPath) |> List.ofSeq |> FilesDropped |> dispatch)
        DockPanel.children [
            // bottom bar: dependency status, progress, Build
            Border.create [
                DockPanel.dock Dock.Bottom
                Border.background "#1e1e1e"
                Border.padding (Thickness(16.0, 10.0))
                Border.child (
                    DockPanel.create [
                        DockPanel.children [
                            Button.create [
                                DockPanel.dock Dock.Right
                                Button.content (if running then "Building…" else "Build")
                                Button.isEnabled canBuild
                                Button.fontSize 15.0
                                Button.padding (Thickness(22.0, 8.0))
                                Button.onClick (fun _ -> dispatch StartBuild)
                            ]
                            StackPanel.create [
                                StackPanel.orientation Orientation.Vertical
                                StackPanel.spacing 4.0
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.children [
                                    depsSummary
                                    match m.Build with
                                    | BuildRunning p ->
                                        ProgressBar.create [ ProgressBar.minimum 0.0; ProgressBar.maximum 100.0; ProgressBar.value p.Percent; ProgressBar.height 10.0; ProgressBar.margin (Thickness(0.0, 0.0, 16.0, 0.0)) ]
                                        let pct = p.Percent.ToString("F0")
                                        dim $"{p.Stage.Label}  {pct}%%"
                                    | _ -> ()
                                ]
                            ]
                        ]
                    ])
            ]
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness 16.0)
                        StackPanel.children [
                            Sections.dependencies m dispatch
                            Sections.sources m dispatch
                            Sections.tracks m dispatch
                            Sections.metadata m dispatch
                            Sections.sync m dispatch
                            Sections.output m dispatch
                            Sections.build m dispatch
                        ]
                    ])
            ]
        ]
    ] :> IView
