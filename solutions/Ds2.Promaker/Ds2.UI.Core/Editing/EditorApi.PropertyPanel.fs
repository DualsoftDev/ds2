namespace Ds2.UI.Core

open System
open System.Globalization
open Ds2.Core

[<Sealed>]
type DeviceApiDefOption(id: Guid, deviceName: string, apiDefName: string) =
    member _.Id = id
    member _.DeviceName = deviceName
    member _.ApiDefName = apiDefName
    member _.DisplayName = $"{deviceName}.{apiDefName}"

[<Sealed>]
type CallApiCallPanelItem
    (
        apiCallId: Guid,
        name: string,
        apiDefId: Guid,
        hasApiDef: bool,
        apiDefDisplayName: string,
        outputAddress: string,
        inputAddress: string,
        valueSpecText: string,
        inputValueSpecText: string
    ) =
    member _.ApiCallId = apiCallId
    member _.Name = name
    member _.ApiDefId = apiDefId
    member _.HasApiDef = hasApiDef
    member _.ApiDefDisplayName = apiDefDisplayName
    member _.OutputAddress = outputAddress
    member _.InputAddress = inputAddress
    member _.ValueSpecText = valueSpecText
    member _.InputValueSpecText = inputValueSpecText

module internal PropertyPanelValueSpec =

    let private formatBound toText (value, boundType) =
        let openToken, closeToken =
            match boundType with
            | BoundType.Open -> "(", ")"
            | BoundType.Closed -> "[", "]"
        $"{openToken}{toText value}{closeToken}"

    let private formatRangeSegment toText (segment: RangeSegment<'T>) =
        let lower =
            match segment.Lower with
            | Some b -> formatBound toText b
            | None -> "(-inf)"
        let upper =
            match segment.Upper with
            | Some b -> formatBound toText b
            | None -> "(+inf)"
        $"{lower}..{upper}"

    let private formatTyped toText (spec: ValueSpec<'T>) =
        match spec with
        | Undefined -> "Undefined"
        | Single v -> toText v
        | Multiple vs -> vs |> List.map toText |> String.concat ", "
        | Ranges segments ->
            segments
            |> List.map (formatRangeSegment toText)
            |> String.concat "; "

    let format (valueSpec: ValueSpec) =
        match valueSpec with
        | UndefinedValue -> "Undefined"
        | IntValue spec -> formatTyped string spec
        | FloatValue spec -> formatTyped (fun (v: float) -> v.ToString("G", CultureInfo.InvariantCulture)) spec
        | StringValue spec -> formatTyped id spec
        | BoolValue spec -> formatTyped (fun v -> if v then "true" else "false") spec

    let tryParseSingle (text: string) =
        let raw = text.Trim()
        if String.IsNullOrWhiteSpace(raw) || raw.Equals("undefined", StringComparison.OrdinalIgnoreCase) then
            Some UndefinedValue
        else
            match Boolean.TryParse(raw) with
            | true, b -> Some(ValueSpec.singleBool b)
            | _ ->
                match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, i -> Some(ValueSpec.singleInt i)
                | _ ->
                    match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, f -> Some(ValueSpec.singleFloat f)
                    | _ -> Some(ValueSpec.singleString raw)
