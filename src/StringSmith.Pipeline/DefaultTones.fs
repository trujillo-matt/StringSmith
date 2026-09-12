namespace StringSmith.Pipeline

open Rocksmith2014.Common.Manifest

/// Built-in tones so a first package plays with sound. Gear keys and knob values are the
/// ones used by iminashi's own integration-test project (MIT); the descriptor codes are
/// taken from the same file. Tone import from existing packages is DLC Builder territory
/// and out of scope for Milestone 1.
module DefaultTones =

    let private pedal (key: string) (typ: string) (knobs: (string * float32) list) : Pedal =
        { Type = typ
          KnobValues = Map.ofList knobs
          Key = key
          Category = None
          Skin = None
          SkinIndex = None }

    let private gear amp cab post : Gear =
        { Amp = amp
          Cabinet = cab
          Racks = Array.create 4 None
          PrePedals = Array.create 4 None
          PostPedals = [| post; None; None; None |] }

    let private tone key (descriptors: string list) (g: Gear) (volume: float) : Tone =
        { GearList = g
          ToneDescriptors = Array.ofList descriptors
          NameSeparator = Tone.DefaultNameSeparator
          Volume = volume
          MacVolume = None
          Key = key
          Name = key
          SortOrder = None }

    /// Orange AD50 into a 2x12, spring reverb after.
    let guitarDistortion =
        tone "stringsmith_guitar" [ "$[35722]DISTORTION"; "$[35724]LEAD" ]
            (gear
                (pedal "Amp_OrangeAD50" "Amps" [ "Amp_OrangeAD50_Bass", 40.0f; "Amp_OrangeAD50_Gain", 60.0f; "Amp_OrangeAD50_Mid", 85.0f; "Amp_OrangeAD50_Treble", 80.0f ])
                (pedal "Cab_OrangePPC212OB_Condenser_Cone" "Cabinets" [])
                (Some(pedal "Pedal_SpringReverb" "Pedals" [ "Pedal_SpringReverb_Depth", 50.0f; "Pedal_SpringReverb_Mix", 20.0f; "Pedal_SpringReverb_Time", 60.0f ])))
            -19.8

    /// Clean: the same amp with the gain rolled back, no reverb.
    let guitarClean =
        tone "stringsmith_clean" [ "$[35720]CLEAN" ]
            (gear
                (pedal "Amp_OrangeAD50" "Amps" [ "Amp_OrangeAD50_Bass", 50.0f; "Amp_OrangeAD50_Gain", 15.0f; "Amp_OrangeAD50_Mid", 60.0f; "Amp_OrangeAD50_Treble", 65.0f ])
                (pedal "Cab_OrangePPC212OB_Condenser_Cone" "Cabinets" [])
                None)
            -17.0

    /// CH350B bass amp into a 4x10, multiband compressor after.
    let bass =
        tone "stringsmith_bass" [ "$[35715]BASS" ]
            (gear
                (pedal "Bass_Amp_CH350B" "Amps" [ "Bass_Amp_CH350B_Bass", 75.0f; "Bass_Amp_CH350B_Gain", 10.0f; "Bass_Amp_CH350B_Treble", 46.0f ])
                (pedal "Bass_Cab_CH410BC_57_OffAxis" "Cabinets" [])
                (Some(pedal "Bass_Pedal_MBComp" "Pedals" [ "Bass_Pedal_MBComp_Compress", 37.0f; "Bass_Pedal_MBComp_Filter", 240.0f; "Bass_Pedal_MBComp_Rate", 30.0f ])))
            -21.9

    let all = [ guitarDistortion; guitarClean; bass ]

    /// Sensible default per role.
    let forRole (role: StringSmith.Conversion.ArrangementRole) =
        match role with
        | StringSmith.Conversion.Bass -> bass
        | StringSmith.Conversion.Lead -> guitarDistortion
        | StringSmith.Conversion.Rhythm -> guitarDistortion
