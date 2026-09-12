module StringSmith.Audio.Tests.TagsTests

open Expecto
open StringSmith.Audio

let sample = """
{
  "streams": [ { "index": 0, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "44100" } ],
  "format": {
    "filename": "song.mp3",
    "duration": "245.123000",
    "tags": { "TITLE": "The Crow, the Owl and the Dove", "artist": "Nightwish", "Album": "Imaginaerum", "date": "2011-11-30" }
  }
}"""

[<Tests>]
let tests =
    testList "Tags.parseFfprobeJson" [
        test "reads title/artist/album case-insensitively, year from a date, duration in ms, codec" {
            let t = Tags.parseFfprobeJson sample
            Expect.equal t.Title (Some "The Crow, the Owl and the Dove") "title"
            Expect.equal t.Artist (Some "Nightwish") "artist"
            Expect.equal t.Album (Some "Imaginaerum") "album"
            Expect.equal t.Year (Some 2011) "year from date"
            Expect.equal t.DurationMs (Some 245123.0) "duration ms"
            Expect.equal t.Codec (Some "mp3") "codec"
        }

        test "a bare year tag and a numeric duration also work" {
            let t = Tags.parseFfprobeJson """{"format":{"duration":12.5,"tags":{"year":"1999"}}}"""
            Expect.equal t.Year (Some 1999) "year"
            Expect.equal t.DurationMs (Some 12500.0) "numeric duration"
        }

        test "garbage year is None rather than a wrong number" {
            Expect.equal (Tags.parseFfprobeJson """{"format":{"tags":{"date":"unknown"}}}""").Year None "unknown"
            Expect.equal (Tags.parseFfprobeJson """{"format":{"tags":{"date":"99"}}}""").Year None "two digits"
        }

        test "empty strings are None, and invalid JSON is Empty not an exception" {
            Expect.equal (Tags.parseFfprobeJson """{"format":{"tags":{"title":"  "}}}""").Title None "blank"
            Expect.equal (Tags.parseFfprobeJson "not json") AudioTags.Empty "invalid"
            Expect.equal (Tags.parseFfprobeJson "{}") AudioTags.Empty "no format"
        }

        test "album_artist is a fallback for artist" {
            Expect.equal (Tags.parseFfprobeJson """{"format":{"tags":{"album_artist":"X"}}}""").Artist (Some "X") "fallback"
        }
    ]
