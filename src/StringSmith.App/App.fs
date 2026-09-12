namespace StringSmith.App

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Styling
open Avalonia.Themes.Fluent

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- ThemeVariant.Dark
        this.Name <- "StringSmith"

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- MainWindow()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
