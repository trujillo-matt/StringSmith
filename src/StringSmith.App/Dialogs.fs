/// Native file and folder pickers through Avalonia 11's StorageProvider. Always invoked
/// on the UI thread regardless of which thread the Cmd started on.
module StringSmith.App.Dialogs

open System.Collections.Generic
open Avalonia.Controls
open Avalonia.Platform.Storage
open Avalonia.Threading

let tabPatterns = [ "*.gp3"; "*.gp4"; "*.gp5"; "*.gpx"; "*.gp" ]
let audioPatterns = [ "*.wav"; "*.ogg"; "*.flac"; "*.mp3"; "*.m4a"; "*.aac" ]
let imagePatterns = [ "*.png"; "*.jpg"; "*.jpeg" ]

let private fileType (name: string) (patterns: string list) =
    [| FilePickerFileType(name, Patterns = patterns) |]

let openFile (window: Window) (title: string) (patterns: string list) : Async<string option> =
    async {
        let! files =
            Dispatcher.UIThread.InvokeAsync<IReadOnlyList<IStorageFile>>(fun () ->
                window.StorageProvider.OpenFilePickerAsync(
                    FilePickerOpenOptions(Title = title, AllowMultiple = false, FileTypeFilter = fileType title patterns)))
            |> Async.AwaitTask
        return files |> Seq.tryHead |> Option.bind (fun f -> Option.ofObj (f.TryGetLocalPath()))
    }

let openFolder (window: Window) (title: string) : Async<string option> =
    async {
        let! folders =
            Dispatcher.UIThread.InvokeAsync<IReadOnlyList<IStorageFolder>>(fun () ->
                window.StorageProvider.OpenFolderPickerAsync(FolderPickerOpenOptions(Title = title, AllowMultiple = false)))
            |> Async.AwaitTask
        return folders |> Seq.tryHead |> Option.bind (fun f -> Option.ofObj (f.TryGetLocalPath()))
    }
