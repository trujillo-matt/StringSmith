namespace StringSmith.App.Controls

open System
open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.Types

/// Controls whose change callback does NOT fire when the view sets the value, and whose
/// callback is refreshed on every render so a recycled control can never dispatch for the
/// field it used to be.
///
/// Two independent hazards, both of which have shipped broken:
///
/// 1. Avalonia's property setters raise change notifications, so a plain
///    `TextBox.onTextChanged` fires on every render that assigns `Text`, dispatching a
///    "the user edited this" message nobody sent. Guarded by a suppression flag.
///
/// 2. FuncUI matches children BY INDEX. If a children list changes length between renders,
///    every sibling after the change shifts by one and FuncUI patches the control that was
///    rendering slot N with the view for slot N+1. If the change-callback attribute is not
///    re-applied during that patch, the recycled control keeps the OLD slot's callback:
///    typing into one field then edits its neighbour. iminashi's `FixedTextBox` /
///    `FixedComboBox` (MIT) pin the callback with an always-equal comparer, which is safe
///    only while the view structure never changes shape. Ours does, so instead every
///    attribute here uses `alwaysApply`: the setter runs on every render, the callback is
///    always current, and the displayed value is always re-asserted from the model.
///
/// Re-applying is cheap (a field assignment, and Avalonia no-ops an equal property set)
/// and it removes a whole class of bug rather than one instance of it. The complementary
/// half lives in `Views/Widgets.fs`, which keeps children counts constant so the recycling
/// does not happen in the first place. Both halves are wanted: one prevents, one contains.
module private Guard =
    /// A comparer that never reports equality, so FuncUI applies the attribute every render.
    /// The alternative — letting FuncUI compare the values — cannot work for callbacks,
    /// because structural equality on F# functions raises at runtime.
    let alwaysApply _ = false

/// A StackPanel that marks a deliberately variable-length run of children.
///
/// Children lists in this app must keep a constant length, because FuncUI matches children
/// by index and a length change shifts every sibling after it. Content that genuinely varies
/// with model state (validation notes, log lines, per-track rows) goes inside one of these:
/// the run then occupies exactly one slot in its parent no matter how long it is. The type
/// is distinct so `Views/Widgets.fs`'s `container` is the single way to express this, and so
/// the shape test can stop descending here rather than flagging intended variation.
type VariableChildren() =
    inherit StackPanel()
    override _.StyleKeyOverride = typeof<StackPanel>

/// A TextBox that is a controlled input: the model is the single source of truth.
type GuardedTextBox() as this =
    inherit TextBox()

    let mutable callback: string -> unit = ignore
    /// True while the view, not the user, is assigning Text.
    let mutable suppress = false
    /// GetObservable replays the current value on subscribe; that replay is not an edit.
    let mutable sawInitial = false

    do
        // Subscribed once, for the lifetime of the control. Swapping the callback is a
        // field assignment, so there is never a reason to tear this down and rebuild it.
        this.GetObservable(TextBox.TextProperty)
        |> Observable.add (fun text ->
            if not sawInitial then sawInitial <- true
            elif not suppress then callback (if isNull text then "" else text))

    /// Keep the Fluent theme's TextBox styling; without this the control renders unstyled.
    override _.StyleKeyOverride = typeof<TextBox>

    member _.Suppress
        with get () = suppress
        and set v = suppress <- v

    member _.OnTextChangedCallback
        with get (): string -> unit = callback
        and set v = callback <- v

    /// Assigns Text without notifying, and re-asserts it every render so the control can
    /// never keep displaying a value the model does not hold.
    static member text<'t when 't :> GuardedTextBox>(value: string) =
        let getter: 't -> string = fun c -> c.Text
        let setter: 't * string -> unit =
            fun (c, v) ->
                c.Suppress <- true
                c.Text <- v
                c.Suppress <- false

        AttrBuilder<'t>.CreateProperty<string>("Text", value, ValueSome getter, ValueSome setter, ValueSome Guard.alwaysApply)

    /// Fires only on user edits, and is refreshed every render.
    static member onTextChanged<'t when 't :> GuardedTextBox>(fn: string -> unit) =
        let getter: 't -> (string -> unit) = fun c -> c.OnTextChangedCallback
        let setter: 't * (string -> unit) -> unit = fun (c, f) -> c.OnTextChangedCallback <- f

        AttrBuilder<'t>.CreateProperty<string -> unit>("OnTextChanged", fn, ValueSome getter, ValueSome setter, ValueSome Guard.alwaysApply)

[<RequireQualifiedAccess>]
module GuardedTextBox =
    /// Fully qualified: opening Avalonia.FuncUI.DSL here would shadow the Avalonia.Controls
    /// TextBox type with the DSL module of the same name.
    let create (attrs: IAttr<GuardedTextBox> list) : IView<GuardedTextBox> =
        Avalonia.FuncUI.DSL.ViewBuilder.Create<GuardedTextBox>(attrs)

/// A ComboBox with the same two guarantees. The track-mapping combos capture a track index
/// in their handlers, so a stale callback there would assign a role to the wrong track.
type GuardedComboBox() as this =
    inherit ComboBox()

    let mutable callback: obj -> unit = ignore
    let mutable suppress = false
    let mutable sawInitial = false

    do
        this.GetObservable(ComboBox.SelectedItemProperty)
        |> Observable.add (fun item ->
            if not sawInitial then sawInitial <- true
            elif not suppress then callback item)

    override _.StyleKeyOverride = typeof<ComboBox>

    member _.Suppress
        with get () = suppress
        and set v = suppress <- v

    member _.OnSelectionChangedCallback
        with get (): obj -> unit = callback
        and set v = callback <- v

    static member selectedItem<'t when 't :> GuardedComboBox>(value: obj) =
        let getter: 't -> obj = fun c -> c.SelectedItem
        let setter: 't * obj -> unit =
            fun (c, v) ->
                c.Suppress <- true
                c.SelectedItem <- v
                c.Suppress <- false

        AttrBuilder<'t>.CreateProperty<obj>("SelectedItem", value, ValueSome getter, ValueSome setter, ValueSome Guard.alwaysApply)

    static member onSelectedItemChanged<'t when 't :> GuardedComboBox>(fn: obj -> unit) =
        let getter: 't -> (obj -> unit) = fun c -> c.OnSelectionChangedCallback
        let setter: 't * (obj -> unit) -> unit = fun (c, f) -> c.OnSelectionChangedCallback <- f

        AttrBuilder<'t>.CreateProperty<obj -> unit>("OnSelectedItemChanged", fn, ValueSome getter, ValueSome setter, ValueSome Guard.alwaysApply)

    /// Setting ItemsSource clears the selection, so restore it without notifying.
    static member dataItems<'t when 't :> GuardedComboBox>(items: Collections.IEnumerable) =
        let getter: 't -> Collections.IEnumerable = fun c -> c.ItemsSource
        let setter: 't * Collections.IEnumerable -> unit =
            fun (c, v) ->
                let wasSelected = c.SelectedItem
                c.Suppress <- true
                c.ItemsSource <- v
                if not (isNull wasSelected) && v |> Seq.cast<obj> |> Seq.contains wasSelected then
                    c.SelectedItem <- wasSelected
                c.Suppress <- false

        // Default comparer here, unlike the rest: re-assigning ItemsSource every render
        // would churn the selection for no reason. The item lists are stable values.
        AttrBuilder<'t>.CreateProperty<Collections.IEnumerable>("DataItems", items, ValueSome getter, ValueSome setter, ValueNone)

[<RequireQualifiedAccess>]
module GuardedComboBox =
    let create (attrs: IAttr<GuardedComboBox> list) : IView<GuardedComboBox> =
        Avalonia.FuncUI.DSL.ViewBuilder.Create<GuardedComboBox>(attrs)
