/// Plain-language text for the library's coded arrangement issues.
///
/// The strings are the English ones shipped with iminashi's Rocksmith 2014 DLC Builder
/// (samples/DLCBuilder/i18n/en.json, MIT), keyed there by the IssueType case name with a
/// "<Name>Help" companion. Copied rather than paraphrased so the wording a user may
/// already know from that tool is the wording they see here.
module StringSmith.App.IssueText

open Rocksmith2014.XML.Processing

/// Short header and longer explanation for an issue type.
let describe (issue: IssueType) : string * string =
    match issue with
    | ApplauseEventWithoutEnd -> ("An applause event is missing the event that ends it", "The E3 and D3 applause events need to be followed by the event E13 that ends them.")
    | EventBetweenIntroApplause code -> ("Unexpected event ({0}) between intro applause events".Replace("{0}", code), "Crowd speed events (e0, e1, e2) do not work correctly when placed between the E3 and E13 events.")
    | NoteLinkedToChord -> ("Note linked to a chord", "Notes can only be linked to other notes.")
    | LinkNextMissingTargetNote -> ("LinkNext note missing a target note", "A note on the same string as a LinkNext note could not be found.")
    | LinkNextSlideMismatch -> ("LinkNext fret mismatch for slide", "The fret number of a note did not match the target fret of the slide.")
    | LinkNextFretMismatch -> ("LinkNext fret mismatch", "The fret numbers on two linked notes did not match each other.")
    | LinkNextBendMismatch -> ("LinkNext bend mismatch", "The bend value a note ended with did not match the first bend value of the note it is linked to.")
    | IncorrectLinkNext -> ("Incorrect LinkNext attribute on note", "The next note on the same string was not at the end of the sustain of the LinkNext note.")
    | UnpitchedSlideWithLinkNext -> ("Unpitched slide note with LinkNext attribute", "Using LinkNext on an unpitched slide causes mastery issues in Rocksmith 2014 Remastered.")
    | PhraseChangeOnLinkNextNote -> ("Phrase changes on LinkNext note's sustain", "This can cause a note on the same string later in the song to turn invisible when the arrangement has DD levels.")
    | DoubleHarmonic -> ("Note set as both harmonic and pinch harmonic", "A note has both the harmonic and pinch harmonic attributes.")
    | SeventhFretHarmonicWithSustain -> ("7th fret harmonic note with sustain", "A 7th fret harmonic note that has sustain is not detected correctly by the game. The sustain should be removed or the note set as ignored.  Ignore status will be automatically added to the note.")
    | NaturalHarmonicWithBend -> ("Natural harmonic with bend", "The harmonic on the note should probably be a pinch harmonic.")
    | MissingBendValue -> ("Note missing a bend value", "A note was set as a bend, but did not have any non-zero bend values.")
    | OverlappingBendValues -> ("Multiple bend values at the same time", "Multiple bend values are defined at the same time.  Some bend values will be removed automatically during build.")
    | ToneChangeOnNote -> ("Tone change occurs on a note", "Generally it is good practice to place the tone change slightly before a note than exactly on it.")
    | NoteInsideNoguitarSection -> ("Note inside a noguitar section", "There is a note or a chord inside a section named \"noguitar\".")
    | MissingLinkNextChordNotes -> ("LinkNext chord without LinkNext chord notes", "A chord itself has the LinkNext attribute set, but it does not contain any chord notes that have the attribute. Such a chord can cause the game to crash.")
    | FingeringAnchorMismatch -> ("Handshape fingering does not match anchor position", "An anchor (FHP) was on the same fret as the lowest note of a chord that does not use the first finger. Sometimes this can be intended.")
    | PossiblyWrongChordFingering -> ("Chord fingering appears to be wrong", "The chord fingering requires the fingers to cross in a way not used in common chords.")
    | BarreOverOpenStrings -> ("Barre over open strings", "There are open strings between strings that use the same finger.")
    | MutedStringInNonMutedChord -> ("Muted string in non-muted chord", "Muted notes should not be included in chords that are not fully muted. The pitch of the muted note needs to be played for the game to properly detect the chord.  The muted strings will be removed automatically.")
    | AnchorInsideHandShape -> ("Anchor is placed inside a handshape", "An anchor (FHP) placed inside a handshape may cut off the handshape at that point or in rare cases cause the game to crash.")
    | AnchorInsideHandShapeAtPhraseBoundary -> ("Phrase boundary breaks handshape", "If repeating chords cross a phrase/section boundary, in EOF, the first chord in the new phrase should be set as \"crazy\".")
    | AnchorCloseToUnpitchedSlide -> ("Anchor very close to the end of an unpitched slide", "An anchor (FHP) close to the end of the sustain of an unpitched slide might not show up in the game.")
    | FirstPhraseNotEmpty -> ("The first phrase contains notes", "The game requires that the first phrase of an arrangement is an empty one. Usually it is created automatically by EOF and called COUNT.")
    | NoEndPhrase -> ("Missing END phrase", "The arrangement does not contain a phrase called END. This phrase cuts off the anchor zone at the end of an arrangement.")
    | MoreThan100Phrases -> ("More than 100 phrases", "The arrangement contains more than 100 phrases. This will prevent the DD bars for the phrases from showing up. The game may also crash if there is a very large quantity of phrases.")
    | IncorrectMover1Phrase -> ("Incorrect phrase mover usage", "The mover phrase occurs on a note. The number at the end of the phrase name should at least 2 in this case.")
    | HopoIntoSameNote -> ("Hammer-on/pull-of on same fret as previous note", "A hammer-on or pull-of uses the same fret as the previous note on the same string.")
    | FingerChangeDuringSlide -> ("Finger used changes during slide", "Based on the anchor fret positions, the finger used to play the note changes during the slide.  For example: Anchor: 1, Fret: 3 (Used finger: 3) slides to Anchor: 5, Fret: 5 (Used finger: 1)")
    | PositionShiftIntoPullOff -> ("Position shift into pull-off", "The anchor position (FHP) changes at a note/chord with pull-off.")
    | InvalidBassArrangementString -> ("Invalid strings used", "The arrangment uses strings that will not be displayed correctly for bass arrangements.  Bass arrangements with more than 4 strings should be created as guitar arrangments.")
    | FretNumberMoreThan24 -> ("Fret number over 24", "Notes whose fret number is more than 24 will not be displayed correctly by the game.")
    | NoteAfterSongEnd -> ("Note after song end", "A note or chord exist after the END phrase or the end of the audio.")
    | TechniqueNoteWithoutSustain -> ("Sustainless note with technique", "A note has a technique that requires sustain (vibrato, tremolo or slide), but the note has less than 5ms of sustain.")
    | LyricWithInvalidChar(ch, _) -> ("The lyrics contain a character ({0}) not included the default font".Replace("{0}", string ch), "A custom font is needed for the character to be displayed in-game.")
    | LyricTooLong lyric -> ("The following lyric is too long: \"{0}\"".Replace("{0}", lyric), "The maximum length of a lyric is 47 bytes when encoded in UTF-8. Lyrics longer than that will be truncated automatically.")
    | LyricsHaveNoLineBreaks -> ("The lyrics contain no line breaks", "Use line breaks to make the lyrics look good in game. Lyric lines are marked with Ctrl+M in EOF.")
    | InvalidShowlights -> ("The show lights need to contain at least one fog note and one beam note", "This show lights file can crash the game when the song is selected.")
    | LowBassTuningWithoutWorkaround -> ("Low bass tuning without workaround applied", "Notes in low tunings are not detected properly by the game without applying a workaround.  Right click on the arrangement and select \"Apply Low Tuning Fix\".")
    | IncorrectLowBassTuningForTuningPitch -> ("Possibly incorrect bass tuning", "When the tuning pitch is set to 220Hz, the tuning values for the strings should be positive.")

/// Code, time and header for one line of display.
let line (issue: Issue) : string =
    let header, _ = describe issue.IssueType
    let code = issueCode issue.IssueType
    match issue.TimeCode with
    | Some ms ->
        let t = System.TimeSpan.FromMilliseconds(float ms)
        let stamp = t.Minutes.ToString() + ":" + t.Seconds.ToString("D2") + "." + t.Milliseconds.ToString("D3")
        $"{code} at {stamp} - {header}"
    | None -> $"{code} - {header}"
