namespace StringSmith.Audio

open System
open System.Text.Json

/// Embedded metadata from the audio file, used to PREFILL the metadata form. Every value
/// is shown to the user and editable; nothing here is trusted silently.
type AudioTags =
    { Title: string option
      Artist: string option
      Album: string option
      Year: int option
      DurationMs: float option
      Codec: string option }

    static member Empty =
        { Title = None; Artist = None; Album = None; Year = None; DurationMs = None; Codec = None }

module Tags =

    /// ffprobe's tag keys vary in case and by container (TITLE / title / Title, date vs
    /// year, "2003-05-01" vs "2003"). Match case-insensitively and take the first four
    /// digits of anything date-like.
    let private tag (tags: JsonElement) (names: string list) =
        if tags.ValueKind <> JsonValueKind.Object then None
        else
            tags.EnumerateObject()
            |> Seq.tryFind (fun p -> names |> List.exists (fun n -> String.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            |> Option.map (fun p -> p.Value.GetString())
            |> Option.bind Option.ofObj
            |> Option.map (fun s -> s.Trim())
            |> Option.filter (fun s -> s <> "")

    let private year (s: string) =
        let digits = s |> Seq.takeWhile Char.IsDigit |> Seq.truncate 4 |> Seq.toArray |> String
        match Int32.TryParse digits with
        | true, y when digits.Length = 4 -> Some y
        | _ -> None

    /// Parses `ffprobe -v quiet -print_format json -show_format -show_streams <file>`.
    let parseFfprobeJson (json: string) : AudioTags =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let format =
                match root.TryGetProperty "format" with
                | true, f -> Some f
                | _ -> None
            let tags =
                format |> Option.bind (fun f -> match f.TryGetProperty "tags" with | true, t -> Some t | _ -> None)
            let duration =
                format
                |> Option.bind (fun f -> match f.TryGetProperty "duration" with | true, d -> Some d | _ -> None)
                |> Option.bind (fun d ->
                    match d.ValueKind with
                    | JsonValueKind.String ->
                        match Double.TryParse(d.GetString(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                        | true, v -> Some(v * 1000.0)
                        | _ -> None
                    | JsonValueKind.Number -> Some(d.GetDouble() * 1000.0)
                    | _ -> None)
            let codec =
                match root.TryGetProperty "streams" with
                | true, s when s.ValueKind = JsonValueKind.Array ->
                    s.EnumerateArray()
                    |> Seq.tryFind (fun st -> match st.TryGetProperty "codec_type" with | true, t -> t.GetString() = "audio" | _ -> false)
                    |> Option.bind (fun st -> match st.TryGetProperty "codec_name" with | true, c -> Option.ofObj (c.GetString()) | _ -> None)
                | _ -> None
            let t names = tags |> Option.bind (fun tg -> tag tg names)
            { Title = t [ "title" ]
              Artist = t [ "artist"; "album_artist"; "performer" ]
              Album = t [ "album" ]
              Year = t [ "date"; "year"; "originaldate"; "TDRC"; "TYER" ] |> Option.bind year
              DurationMs = duration
              Codec = codec }
        with _ ->
            AudioTags.Empty

    let read (env: Env) (ffprobePath: string) (audioFile: string) : Result<AudioTags, string> =
        env.Run ffprobePath [ "-v"; "quiet"; "-print_format"; "json"; "-show_format"; "-show_streams"; audioFile ]
        |> Result.map parseFfprobeJson
