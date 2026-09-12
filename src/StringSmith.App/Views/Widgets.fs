/// Small view helpers so the sections read as intent rather than DSL.
module StringSmith.App.Views.Widgets

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media

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
                if not enabled then dim disabledReason
                yield! content
            ])
    ] :> IView

/// Label on the left, control and its notes on the right.
let labelled (label: string) (control: IView) (notes: IView list) : IView =
    Grid.create [
        Grid.columnDefinitions "170,*"
        Grid.margin (Thickness(0.0, 2.0))
        Grid.children [
            TextBlock.create [ Grid.column 0; TextBlock.text label; TextBlock.verticalAlignment VerticalAlignment.Center ]
            StackPanel.create [ Grid.column 1; StackPanel.spacing 2.0; StackPanel.children (control :: notes) ]
        ]
    ] :> IView

let textField (label: string) (value: string) (sourceLabel: string) (onChange: string -> unit) (notes: IView list) : IView =
    let box = TextBox.create [ TextBox.text value; TextBox.onTextChanged onChange ] :> IView
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

let comboBox (items: string list) (selected: string) (onChange: string -> unit) (width: float) : IView =
    ComboBox.create [
        ComboBox.dataItems items
        ComboBox.selectedItem selected
        ComboBox.width width
        ComboBox.onSelectedItemChanged (fun o -> match o with :? string as s -> onChange s | _ -> ())
    ] :> IView

let fmtTime (ms: float) = StringSmith.App.Derive.fmtTime ms
