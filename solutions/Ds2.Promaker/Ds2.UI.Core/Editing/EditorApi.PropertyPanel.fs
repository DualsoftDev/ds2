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
        inputValueSpecText: string,
        outputSpecTypeIndex: int,
        inputSpecTypeIndex: int
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
    member _.OutputSpecTypeIndex = outputSpecTypeIndex
    member _.InputSpecTypeIndex  = inputSpecTypeIndex

[<Sealed>]
type CallConditionApiCallItem
    (apiCallId: Guid, apiCallName: string, apiDefDisplayName: string, outputSpecText: string, outputSpecTypeIndex: int) =
    member _.ApiCallId         = apiCallId
    member _.ApiCallName       = apiCallName
    member _.ApiDefDisplayName = apiDefDisplayName
    member _.OutputSpecText    = outputSpecText
    member _.OutputSpecTypeIndex = outputSpecTypeIndex

[<Sealed>]
type CallConditionPanelItem
    (conditionId: Guid, conditionType: CallConditionType,
     isOR: bool, isRising: bool, items: CallConditionApiCallItem list) =
    member _.ConditionId   = conditionId
    member _.ConditionType = conditionType
    member _.IsOR          = isOR
    member _.IsRising      = isRising
    member _.Items         = items

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

    // 실수 포맷 시 정수 값("10")에 소수점(".0")을 보장하여 LoadFromText 역파싱 오인식 방지
    let private ensureDecimalPoint (s: string) =
        if s.Contains('.') || s.Contains('E') || s.Contains('e') then s else s + ".0"

    let format (valueSpec: ValueSpec) =
        match valueSpec with
        | UndefinedValue   -> "Undefined"
        | BoolValue  spec  -> formatTyped (fun v -> if v then "true" else "false") spec
        | Int8Value  spec  -> formatTyped string spec
        | Int16Value spec  -> formatTyped string spec
        | Int32Value spec  -> formatTyped string spec
        | Int64Value spec  -> formatTyped string spec
        | UInt8Value  spec -> formatTyped string spec
        | UInt16Value spec -> formatTyped string spec
        | UInt32Value spec -> formatTyped string spec
        | UInt64Value spec -> formatTyped string spec
        | Float32Value spec -> formatTyped (fun (v: float32) -> ensureDecimalPoint (v.ToString("G9",  CultureInfo.InvariantCulture))) spec
        | Float64Value spec -> formatTyped (fun (v: float)   -> ensureDecimalPoint (v.ToString("G17", CultureInfo.InvariantCulture))) spec
        | StringValue spec  -> formatTyped id spec

    let private inferFromText (raw: string) =
        match Boolean.TryParse(raw) with
        | true, b -> Some(ValueSpec.singleBool b)
        | _ ->
            match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, i -> Some(ValueSpec.singleInt64 i)
            | _ ->
                match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, d -> Some(ValueSpec.singleFloat64 d)
                | _ -> Some(ValueSpec.singleString raw)

    // 콤보박스 인덱스 (0=Undefined, 1=bool, 2=int8, ..., 10=float32, 11=float64, 12=string)
    let dataTypeIndex (spec: ValueSpec) =
        match spec with
        | UndefinedValue   -> 0
        | BoolValue    _   -> 1
        | Int8Value    _   -> 2
        | Int16Value   _   -> 3
        | Int32Value   _   -> 4
        | Int64Value   _   -> 5
        | UInt8Value   _   -> 6
        | UInt16Value  _   -> 7
        | UInt32Value  _   -> 8
        | UInt64Value  _   -> 9
        | Float32Value _   -> 10
        | Float64Value _   -> 11
        | StringValue  _   -> 12

    // 타입 인덱스 → 기본 ValueSpec (다이얼로그에서 사용자가 선택한 타입을 hint로 사용)
    let specFromTypeIndex (idx: int) : ValueSpec =
        match idx with
        | 1  -> BoolValue    (Single false)
        | 2  -> Int8Value    (Single 0y)
        | 3  -> Int16Value   (Single 0s)
        | 4  -> Int32Value   (Single 0)
        | 5  -> Int64Value   (Single 0L)
        | 6  -> UInt8Value   (Single 0uy)
        | 7  -> UInt16Value  (Single 0us)
        | 8  -> UInt32Value  (Single 0u)
        | 9  -> UInt64Value  (Single 0UL)
        | 10 -> Float32Value (Single 0.0f)
        | 11 -> Float64Value (Single 0.0)
        | 12 -> StringValue  (Single "")
        | _  -> UndefinedValue

    // 쉼표로 분리된 텍스트에서 Multiple 파싱
    let private parseMultipleAs (hint: ValueSpec) (parts: string list) : ValueSpec option =
        let tryAll (parse: string -> 'a option) (wrap: 'a list -> ValueSpec) =
            let parsed = parts |> List.choose parse
            if parsed.Length = parts.Length then Some(wrap parsed) else None
        let tryParse p = match p with true, v -> Some v | _ -> None
        match hint with
        | Int8Value   _ -> tryAll (fun s -> SByte.TryParse(s) |> tryParse)  (fun vs -> Int8Value   (Multiple vs))
        | Int16Value  _ -> tryAll (fun s -> Int16.TryParse(s)  |> tryParse)  (fun vs -> Int16Value  (Multiple vs))
        | Int32Value  _ -> tryAll (fun s -> Int32.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture) |> tryParse) (fun vs -> Int32Value  (Multiple vs))
        | Int64Value  _ -> tryAll (fun s -> Int64.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture) |> tryParse) (fun vs -> Int64Value  (Multiple vs))
        | UInt8Value  _ -> tryAll (fun s -> Byte.TryParse(s)   |> tryParse)  (fun vs -> UInt8Value  (Multiple vs))
        | UInt16Value _ -> tryAll (fun s -> UInt16.TryParse(s) |> tryParse)  (fun vs -> UInt16Value (Multiple vs))
        | UInt32Value _ -> tryAll (fun s -> UInt32.TryParse(s) |> tryParse)  (fun vs -> UInt32Value (Multiple vs))
        | UInt64Value _ -> tryAll (fun s -> UInt64.TryParse(s) |> tryParse)  (fun vs -> UInt64Value (Multiple vs))
        | Float32Value _ -> tryAll (fun s -> Single.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) |> tryParse) (fun vs -> Float32Value (Multiple vs))
        | Float64Value _ -> tryAll (fun s -> Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) |> tryParse) (fun vs -> Float64Value (Multiple vs))
        | StringValue _ -> Some(StringValue (Multiple parts))
        | BoolValue _ ->
            let parsed = parts |> List.choose (fun s -> match Boolean.TryParse(s) with true, b -> Some b | _ -> None)
            if parsed.Length = parts.Length then Some(BoolValue (Multiple parsed)) else None
        | UndefinedValue -> None

    // 힌트 타입으로 파싱 시도, 실패 시 타입 추론으로 폴백
    let tryParseAs (hint: ValueSpec) (text: string) =
        let raw = text.Trim()
        if String.IsNullOrWhiteSpace(raw) || raw.Equals("undefined", StringComparison.OrdinalIgnoreCase) then
            Some UndefinedValue
        else
            // Multiple: 쉼표 포함 (Ranges의 "; " 구분자와 충돌 없음)
            let isMultiple = raw.Contains(',') && not (raw.Contains(".."))
            if isMultiple then
                let parts = raw.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
                parseMultipleAs hint parts
                |> Option.orElse (inferFromText raw)
            else
                let hinted =
                    match hint with
                    | Int8Value   _ -> match SByte.TryParse(raw)  with true, v -> Some(ValueSpec.singleInt8 v)   | _ -> None
                    | Int16Value  _ -> match Int16.TryParse(raw)  with true, v -> Some(ValueSpec.singleInt16 v)  | _ -> None
                    | Int32Value  _ -> match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)  with true, v -> Some(ValueSpec.singleInt32 v)  | _ -> None
                    | Int64Value  _ -> match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)  with true, v -> Some(ValueSpec.singleInt64 v)  | _ -> None
                    | UInt8Value  _ -> match Byte.TryParse(raw)   with true, v -> Some(ValueSpec.singleUInt8 v)  | _ -> None
                    | UInt16Value _ -> match UInt16.TryParse(raw) with true, v -> Some(ValueSpec.singleUInt16 v) | _ -> None
                    | UInt32Value _ -> match UInt32.TryParse(raw) with true, v -> Some(ValueSpec.singleUInt32 v) | _ -> None
                    | UInt64Value _ -> match UInt64.TryParse(raw) with true, v -> Some(ValueSpec.singleUInt64 v) | _ -> None
                    | Float32Value _ -> match Single.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with true, v -> Some(ValueSpec.singleFloat32 v) | _ -> None
                    | Float64Value _ -> match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with true, v -> Some(ValueSpec.singleFloat64 v) | _ -> None
                    | BoolValue   _ -> match Boolean.TryParse(raw) with true, b -> Some(ValueSpec.singleBool b)  | _ -> None
                    | StringValue _ -> Some(ValueSpec.singleString raw)
                    | UndefinedValue -> None
                match hinted with
                | Some _ -> hinted
                | None -> inferFromText raw
