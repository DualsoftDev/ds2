module Ds2.Store.Editor.Tests.SystemTypePresetTests

open Ds2.Editor
open Xunit

[<Fact>]
let ``normalize cylinder number to template`` () =
    Assert.Equal("Cylinder_#", SystemTypePreset.normalizeSystemTypeForDisplay "Cylinder_5")
    Assert.Equal("Cylinder_#", SystemTypePreset.normalizeSystemTypeForDisplay "Cylinder_1")
    Assert.Equal("Cylinder_#", SystemTypePreset.normalizeSystemTypeForDisplay "Cylinder_99")

[<Fact>]
let ``normalize keeps non-numeric or unrelated names`` () =
    Assert.Equal("Unit", SystemTypePreset.normalizeSystemTypeForDisplay "Unit")
    Assert.Equal("Cylinder_X", SystemTypePreset.normalizeSystemTypeForDisplay "Cylinder_X")
    Assert.Equal("Cylinder_", SystemTypePreset.normalizeSystemTypeForDisplay "Cylinder_")
    Assert.Equal("", SystemTypePreset.normalizeSystemTypeForDisplay "")

[<Fact>]
let ``parseEntries splits ApiList and dedupes by SystemType`` () =
    let mappings = [
        "ADV;RET:Cylinder"
        "OPEN,CLOSE:Door"
        "ADV;RET:Cylinder"  // 중복 SystemType — skip
        ":NoApi"            // ApiList 빈값 허용
        "JustText"          // ':' 없음 — skip
        ""                  // 공백 — skip
        "X:"                // SystemType 빈값 — skip
    ]
    let entries = SystemTypePreset.parseEntries mappings
    Assert.Equal(3, entries.Length)
    let first = entries.[0]
    Assert.Equal("Cylinder", first.SystemType)
    Assert.Equal<string seq>([| "ADV"; "RET" |], first.ApiNames)
    let second = entries.[1]
    Assert.Equal("Door", second.SystemType)
    Assert.Equal<string seq>([| "OPEN"; "CLOSE" |], second.ApiNames)
    let third = entries.[2]
    Assert.Equal("NoApi", third.SystemType)
    Assert.Empty(third.ApiNames)

[<Fact>]
let ``mergeWithDefaults preserves saved order and appends only new SystemTypes`` () =
    let saved = [ "ADV;RET:Cylinder"; "OPEN,CLOSE:Door" ]
    let defaults = [
        "ADV;RET:Cylinder"   // 이미 있음 — skip
        "ON;OFF:Lamp"        // 새 — append
        ":Door"              // 이미 있음 — skip
    ]
    let result = SystemTypePreset.mergeWithDefaults saved defaults
    Assert.Equal<string seq>(
        [ "ADV;RET:Cylinder"; "OPEN,CLOSE:Door"; "ON;OFF:Lamp" ],
        result)

[<Fact>]
let ``lookupApiNames exact match wins`` () =
    let entries : SystemTypePreset.Entry list = [
        { SystemType = "Cylinder_#"; ApiNames = [| "ADV"; "RET" |] }
        { SystemType = "Cylinder_5"; ApiNames = [| "X"; "Y" |] }
    ]
    let apis = SystemTypePreset.lookupApiNames entries "Cylinder_5"
    Assert.Equal<string seq>([| "X"; "Y" |], apis)

[<Fact>]
let ``lookupApiNames falls back to # template when no exact match`` () =
    let entries : SystemTypePreset.Entry list = [
        { SystemType = "Cylinder_#"; ApiNames = [| "ADV"; "RET" |] }
    ]
    let apis = SystemTypePreset.lookupApiNames entries "Cylinder_5"
    Assert.Equal<string seq>([| "ADV"; "RET" |], apis)

[<Fact>]
let ``lookupApiNames returns empty for unknown system type`` () =
    let entries : SystemTypePreset.Entry list = [
        { SystemType = "Cylinder_#"; ApiNames = [| "ADV"; "RET" |] }
    ]
    Assert.Empty(SystemTypePreset.lookupApiNames entries "Robot_X")
    Assert.Empty(SystemTypePreset.lookupApiNames entries "")

[<Fact>]
let ``lookupApiNames is case-insensitive on exact match`` () =
    let entries : SystemTypePreset.Entry list = [
        { SystemType = "Cylinder"; ApiNames = [| "ADV" |] }
    ]
    let apis = SystemTypePreset.lookupApiNames entries "CYLINDER"
    Assert.Equal<string seq>([| "ADV" |], apis)

[<Fact>]
let ``expandSystemTypeTemplate returns name as-is for non-template`` () =
    let result = SystemTypePreset.expandSystemTypeTemplate "Unit" |> List.ofSeq
    Assert.Equal<string seq>([ "Unit" ], result)

[<Fact>]
let ``mergeDeviceTemplateNames removes '#' templates and dedupes case-insensitive`` () =
    let result =
        SystemTypePreset.mergeDeviceTemplateNames
            [ "Cylinder_#"; "Robot"; "Unit" ]
            [ "robot"; "Door"; "Cylinder_5" ]
    // '#' 템플릿 제외, dedup 후 OrdinalIgnoreCase 정렬
    Assert.Equal<string seq>([ "Cylinder_5"; "Door"; "Robot"; "Unit" ], result)

[<Fact>]
let ``mergeDeviceTemplateNames with empty inputs returns empty`` () =
    let result = SystemTypePreset.mergeDeviceTemplateNames [] []
    Assert.Empty(result)
