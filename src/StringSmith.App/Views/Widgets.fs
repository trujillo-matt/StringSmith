/// Small view helpers so the sections read as intent rather than DSL.
module StringSmith.App.Views.Widgets

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media
open StringSmith.App.Controls

let private block (s: string) (attrs: IAttr<TextBlock> list) : IView =
    TextBlock.create ([ TextBlock.text s; TextBlock.textWrapping TextWrapping.Wrap ] @ attrs) :> IView

let text (s: string) : IView = block s []
let dim (s: string) : IView = block s [ TextBlock.foreground "#9a9a9a"; TextBlock.fontSize 12.0 ]
let warn (s: string) : IView = block ("⚠ " + s) [ TextBlock.foreground "#f0a030"; TextBlock.fontSize 12.5 ]
let error (s: string) : IView = block ("✖ " + s) [ TextBlock.foreground "#ff6b5b"; TextBlock.fontSize 12.5 ]
let good (s: string) : IView = block ("✔ " + s) [ TextBlock.foreground "#7fd17f"; TextBlock.fontSize 12.5 ]
let mono (s: string) : IView = block s [ TextBlock.fontFamily (FontFamily "Menlo, Consolas, monospace"); TextBlock.fontSize 12.0 ]

let heading (s: string) : IView =
    TextBlock.create [ TextBlock.text s; TextBlock.fontSize 17.0; TextBlock.fontWeight FontWeight.SemiBold ] :> IView

let button (label: string) (enabled: bool) (onClick: unit -> unit) : IView =
    Button.create [ Button.content label; Button.isEnabled enabled; Button.onClick (fun _ -> onClick ()); Button.padding (Thickness(12.0, 6.0)) ] :> IView

let vstack (spacing: float) (children: IView list) : IView =
    StackPanel.create [ StackPanel.orientation Orientation.Vertical; StackPanel.spacing spacing; StackPanel.children children ] :> IView

let hstack (spacing: float) (children: IView list) : IView =
    StackPanel.create [ StackPanel.orientation Orientation.Horizontal; StackPanel.spacing spacing; StackPanel.children children ] :> IView

/// A titled card. Disabled sections stay visible so the user sees the whole shape of the
/// task, dimmed, with the reason they are not yet available.
///
/// The disabled-reason line is ALWAYS rendered and merely hidden when the section is
/// enabled. Writing it as `if not enabled then dim reason` seems harmless and is not:
/// FuncUI matches children by index, so the line disappearing when a section becomes
/// enabled shifted every field below it up one slot. Each control was then patched with
/// its neighbour's view while keeping its own change callback, so typing into one field
/// edited the field above it, and a field's displayed text stopped matching the model —
/// which is why a visible "2025" in Year still left "Year must be a number" blocking the
/// build. Keep this list's length constant. The same rule applies to every children list
/// in this app; use `container` below for anything variable.
let section (title: string) (enabled: bool) (disabledReason: string) (content: IView list) : IView =
    Border.create [
        Border.margin (Thickness(0.0, 0.0, 0.0, 12.0))
        Border.padding (Thickness 14.0)
        Border.cornerRadius (CornerRadius 8.0)
        Border.background "#232323"
        Border.opacity (if enabled then 1.0 else 0.5)
        Border.isEnabled enabled
        Border.child (
            vstack 8.0 [
                heading title
                TextBlock.create [
                    TextBlock.text disabledReason
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.foreground "#9a9a9a"
                    TextBlock.fontSize 12.0
                    TextBlock.isVisible (not enabled)
                ]
                yield! content
            ])
    ] :> IView

/// Wraps a variable-length run of views in one child, so that the run's length changing
/// between renders cannot shift the index of anything beside it. Any `yield!` of a list
/// whose length depends on model state belongs inside one of these.
let container (children: IView list) : IView =
    Avalonia.FuncUI.DSL.ViewBuilder.Create<VariableChildren>
        [ StackPanel.orientation Orientation.Vertical
          StackPanel.spacing 4.0
          StackPanel.children children ] :> IView

/// Label on the left, control and its notes on the right.
///
/// The right-hand column ALWAYS has exactly two children: the control, then one container
/// holding the notes. Notes come and go as validation state changes, and if they were
/// siblings of the control then that changing count would shift sibling indices and let
/// FuncUI recycle a control into a different field's slot. Keeping the variation inside a
/// dedicated container pins every control to a stable position. See
/// `Controls/GuardedTextBox.fs` for what that bug looked like in practice.
let labelled (label: string) (control: IView) (notes: IView list) : IView =
    Grid.create [
        Grid.columnDefinitions "170,*"
        Grid.margin (Thickness(0.0, 2.0))
        Grid.children [
            TextBlock.create [ Grid.column 0; TextBlock.text label; TextBlock.verticalAlignment VerticalAlignment.Center ]
            StackPanel.create [
                Grid.column 1
                StackPanel.spacing 2.0
                StackPanel.children [
                    control
                    container notes
                ]
            ]
        ]
    ] :> IView

let textField (label: string) (value: string) (sourceLabel: string) (onChange: string -> unit) (notes: IView list) : IView =
    let box =
        GuardedTextBox.create [
            GuardedTextBox.text value
            GuardedTextBox.onTextChanged onChange
        ] :> IView
    labelled label box ((if sourceLabel = "" then [] else [ dim sourceLabel ]) @ notes)

/// A path display with a Choose button; drop targets are handled at the window level.
let filePicker (label: string) (path: string option) (placeholder: string) (onPick: unit -> unit) (notes: IView list) : IView =
    let row =
        DockPanel.create [
            DockPanel.children [
                Button.create [ DockPanel.dock Dock.Right; Button.content "Choose…"; Button.onClick (fun _ -> onPick ()); Button.margin (Thickness(8.0, 0.0, 0.0, 0.0)) ]
                TextBlock.create [
                    TextBlock.text (defaultArg path placeholder)
                    TextBlock.foreground (if path.IsSome then "#e8e8e8" else "#9a9a9a")
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                ]
            ]
        ] :> IView
    labelled label row notes

let chip (label: string) (background: string) : IView =
    Border.create [
        Border.background background
        Border.cornerRadius (CornerRadius 10.0)
        Border.padding (Thickness(10.0, 3.0))
        Border.horizontalAlignment HorizontalAlignment.Left
        Border.child (TextBlock.create [ TextBlock.text label; TextBlock.fontSize 12.0; TextBlock.fontWeight FontWeight.SemiBold ])
    ] :> IView

let fmtTime (ms: float) = StringSmith.App.Derive.fmtTime ms
