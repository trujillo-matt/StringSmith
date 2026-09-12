namespace StringSmith.Core

/// Musical time in ticks. The resolution (ticks per quarter note) is carried on the
/// TabScore by the producer; nothing here assumes a value.
///
/// Using a unit of measure makes a tick/millisecond mix-up a compile error. That is
/// the single most likely bug class in this pipeline: the musical domain (ticks) and
/// the audio domain (milliseconds) must only ever meet inside StringSmith.Sync.
[<Measure>]
type tick

/// Wall-clock time in milliseconds against the user's actual audio recording.
[<Measure>]
type ms
