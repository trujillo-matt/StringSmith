namespace StringSmith.App.Controls

open System
open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.Types

/// A TextBox whose change callback does NOT fire when the view sets the text.
///
/// Why this exists. Avalonia's `TextBox.Text` setter raises a change notification, and
/// FuncUI's plain `TextBox.onTextChanged` is wired to that notification. So every render
/// that assigns `Text` dispatches a "the user edited this" message. When FuncUI also
/// recycles a control into a different logical slot — which happens whenever the number of
/// sibling views changes between renders — the value written into the recycled control is
/// dispatched to the WRONG field's handler.
///
/// That is not hypothetical: the first run of this app on macOS showed the Year field
/// displaying "440", the value belonging to the Tuning frequency box directly below it,
/// because the tuning value was written into a recycled control still wired to SetYear.
///
/// The fix is a suppression flag held across the programmatic assignment, plus skipping
/// the initial notification that `GetObservable` replays on subscribe. The mechanism is
/// the one used by `Controls/FixedTextBox.fs` in iminashi's Rocksmith 2014 DLC Builder
/// (MIT), which exists for the same reason on this same Avalonia + FuncUI stack.
///
/// The complementary half of the fix lives in `Views/Widgets.fs`: every field row keeps a
/// constant child count so controls are not reassigned between slots in the first place.
type GuardedTextBox() as this =
    inherit TextBox()

    let mutable subscription: IDisposable = null
    let mutable callback: string -> unit = ignore
    /// True while the view (not the user) is assigning Text.
    let mutable suppress = false
    /// GetObservable replays the current value on subscribe; that one is not an edit.
    let mutable sawInitial = false

    /// Keep the Fluent theme's TextBox styling; without this the control renders unstyled.
    override _.StyleKeyOverride = typeof<TextBox>

    member _.Suppress
        with get () = suppress
        and set v = suppress <- v

    member _.OnTextChangedCallback
        with get (): string -> unit = callback
        and set (v) =
            if not (isNull subscription) then subscription.Dispose()
            callback <- v
            sawInitial <- false

            subscription <-
                this.GetObservable(TextBox.TextProperty)
                |> Observable.subscribe (fun text ->
                    if not sawInitial then sawInitial <- true
                    elif not suppress then callback (if isNull text then "" else text))

    override _.OnDetachedFromLogicalTree(e) =
        if not (isNull subscription) then
            subscription.Dispose()
            subscription <- null

        base.OnDetachedFromLogicalTree(e)

    /// Assigns Text without notifying the callback.
    static member text<'t when 't :> GuardedTextBox>(value: string) =
        let getter: 't -> string = fun c -> c.Text
        let setter: 't * string -> unit =
            fun (c, v) ->
                c.Suppress <- true
                c.Text <- v
                c.Suppress <- false

        AttrBuilder<'t>.CreateProperty<string>("Text", value, ValueSome getter, ValueSome setter, ValueNone)

    /// Fires only on user edits. The comparer keeps the subscription alive across renders;
    /// re-subscribing every render would reset the skip-initial state and reintroduce the
    /// spurious dispatch this control exists to prevent.
    static member onTextChanged<'t when 't :> GuardedTextBox>(fn: string -> unit) =
        let getter: 't -> (string -> unit) = fun c -> c.OnTextChangedCallback
        let setter: 't * (string -> unit) -> unit = fun (c, f) -> c.OnTextChangedCallback <- f
        let comparer _ = true

        AttrBuilder<'t>.CreateProperty<string -> unit>("OnTextChanged", fn, ValueSome getter, ValueSome setter, ValueSome comparer)

[<RequireQualifiedAccess>]
module GuardedTextBox =
    /// Fully qualified: opening Avalonia.FuncUI.DSL here would shadow the Avalonia.Controls
    /// TextBox type with the DSL module of the same name.
    let create (attrs: IAttr<GuardedTextBox> list) : IView<GuardedTextBox> =
        Avalonia.FuncUI.DSL.ViewBuilder.Create<GuardedTextBox>(attrs)
