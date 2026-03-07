namespace Ds2.UI.Core

open System
open System.Globalization
open Ds2.Core

/// ValueSpec 타입 콤보박스 인덱스 — C#/F# 공유 상수.
[<RequireQualifiedAccess>]
module ValueSpecTypeIndex =
    [<Literal>]
    let Undefined = 0
    [<Literal>]
    let Bool = 1
    [<Literal>]
    let Int8 = 2
    [<Literal>]
    let Int16 = 3
    [<Literal>]
    let Int32 = 4
    [<Literal>]
    let Int64 = 5
    [<Literal>]
    let UInt8 = 6
    [<Literal>]
    let UInt16 = 7
    [<Literal>]
    let UInt32 = 8
    [<Literal>]
    let UInt64 = 9
    [<Literal>]
    let Float32 = 10
    [<Literal>]
    let Float64 = 11
    [<Literal>]
    let String = 12

module internal PropertyPanelValueSpec =

    let private formatBound toText (value, boundType) =
        let openToken, closeToken =
            match boundType with
            | BoundType.Open -> "(", ")"
            | BoundType.Closed -> "[", "]"
        $"{openToken}{toText value}{closeToken}"

    let private formatBoundOr toText noneText boundOpt =
        boundOpt
        |> Option.map (formatBound toText)
        |> Option.defaultValue noneText

    let private formatRangeSegment toText (segment: RangeSegment<'T>) =
        let lower = formatBoundOr toText "(-inf)" segment.Lower
        let upper = formatBoundOr toText "(+inf)" segment.Upper
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
        if
            s.Equals("NaN", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Infinity", StringComparison.OrdinalIgnoreCase)
            || s.Equals("+Infinity", StringComparison.OrdinalIgnoreCase)
            || s.Equals("-Infinity", StringComparison.OrdinalIgnoreCase)
        then
            s
        elif s.Contains('.') || s.Contains('E') || s.Contains('e') then s
        else s + ".0"

    let format (valueSpec: ValueSpec) =
        match valueSpec with
        | UndefinedValue    -> "Undefined"
        | BoolValue  spec   -> formatTyped (fun v -> if v then "true" else "false") spec
        | Int8Value  spec   -> formatTyped string spec
        | Int16Value spec   -> formatTyped string spec
        | Int32Value spec   -> formatTyped string spec
        | Int64Value spec   -> formatTyped string spec
        | UInt8Value  spec  -> formatTyped string spec
        | UInt16Value spec  -> formatTyped string spec
        | UInt32Value spec  -> formatTyped string spec
        | UInt64Value spec  -> formatTyped string spec
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

    let dataTypeIndex (spec: ValueSpec) =
        match spec with
        | UndefinedValue -> ValueSpecTypeIndex.Undefined
        | BoolValue    _ -> ValueSpecTypeIndex.Bool
        | Int8Value    _ -> ValueSpecTypeIndex.Int8
        | Int16Value   _ -> ValueSpecTypeIndex.Int16
        | Int32Value   _ -> ValueSpecTypeIndex.Int32
        | Int64Value   _ -> ValueSpecTypeIndex.Int64
        | UInt8Value   _ -> ValueSpecTypeIndex.UInt8
        | UInt16Value  _ -> ValueSpecTypeIndex.UInt16
        | UInt32Value  _ -> ValueSpecTypeIndex.UInt32
        | UInt64Value  _ -> ValueSpecTypeIndex.UInt64
        | Float32Value _ -> ValueSpecTypeIndex.Float32
        | Float64Value _ -> ValueSpecTypeIndex.Float64
        | StringValue  _ -> ValueSpecTypeIndex.String

    let specFromTypeIndex (idx: int) : ValueSpec =
        match idx with
        | ValueSpecTypeIndex.Bool    -> BoolValue    (Single false)
        | ValueSpecTypeIndex.Int8    -> Int8Value    (Single 0y)
        | ValueSpecTypeIndex.Int16   -> Int16Value   (Single 0s)
        | ValueSpecTypeIndex.Int32   -> Int32Value   (Single 0)
        | ValueSpecTypeIndex.Int64   -> Int64Value   (Single 0L)
        | ValueSpecTypeIndex.UInt8   -> UInt8Value   (Single 0uy)
        | ValueSpecTypeIndex.UInt16  -> UInt16Value  (Single 0us)
        | ValueSpecTypeIndex.UInt32  -> UInt32Value  (Single 0u)
        | ValueSpecTypeIndex.UInt64  -> UInt64Value  (Single 0UL)
        | ValueSpecTypeIndex.Float32 -> Float32Value (Single 0.0f)
        | ValueSpecTypeIndex.Float64 -> Float64Value (Single 0.0)
        | ValueSpecTypeIndex.String  -> StringValue  (Single "")
        | _                          -> UndefinedValue

    // TryParse 공통 헬퍼 — Single/Multiple 모두 처리
    let private tryParseAll (tryParse: string -> bool * 'a) (wrap: 'a list -> ValueSpec) (parts: string list) =
        let parsed = parts |> List.choose (fun s -> match tryParse s with true, v -> Some v | _ -> None)
        if parsed.Length = parts.Length then Some(wrap parsed) else None

    // .NET TryParse 오버로드 해소용 래퍼
    let private parseBool    (s: string) = Boolean.TryParse(s)
    let private parseInt8    (s: string) = SByte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseInt16   (s: string) = Int16.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseInt32   (s: string) = Int32.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseInt64   (s: string) = Int64.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseUInt8   (s: string) = Byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseUInt16  (s: string) = UInt16.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseUInt32  (s: string) = UInt32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseUInt64  (s: string) = UInt64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    let private parseFloat32 (s: string) = Single.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
    let private parseFloat64 (s: string) = Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)

    // Single/Multiple 통합 래퍼: isMultiple 여부에 따라 wrap 함수 선택
    let private wrapAs isMultiple (multiWrap: 'a list -> ValueSpec) (singleWrap: 'a -> ValueSpec) : 'a list -> ValueSpec =
        if isMultiple then multiWrap else (List.head >> singleWrap)

    // 힌트 타입으로 파싱 시도, 실패 시 타입 추론으로 폴백
    let tryParseAs (hint: ValueSpec) (text: string) =
        let raw = text.Trim()
        if String.IsNullOrWhiteSpace(raw) || raw.Equals("undefined", StringComparison.OrdinalIgnoreCase) then
            Some UndefinedValue
        else
            let isMultiple = raw.Contains(',') && not (raw.Contains(".."))
            let parts = if isMultiple then raw.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList else [raw]
            let w multiWrap singleWrap = wrapAs isMultiple multiWrap singleWrap
            let hinted =
                match hint with
                | BoolValue    _ -> tryParseAll parseBool    (w (fun vs -> BoolValue    (Multiple vs)) ValueSpec.singleBool)    parts
                | Int8Value    _ -> tryParseAll parseInt8    (w (fun vs -> Int8Value    (Multiple vs)) ValueSpec.singleInt8)     parts
                | Int16Value   _ -> tryParseAll parseInt16   (w (fun vs -> Int16Value   (Multiple vs)) ValueSpec.singleInt16)    parts
                | Int32Value   _ -> tryParseAll parseInt32   (w (fun vs -> Int32Value   (Multiple vs)) ValueSpec.singleInt32)    parts
                | Int64Value   _ -> tryParseAll parseInt64   (w (fun vs -> Int64Value   (Multiple vs)) ValueSpec.singleInt64)    parts
                | UInt8Value   _ -> tryParseAll parseUInt8   (w (fun vs -> UInt8Value   (Multiple vs)) ValueSpec.singleUInt8)    parts
                | UInt16Value  _ -> tryParseAll parseUInt16  (w (fun vs -> UInt16Value  (Multiple vs)) ValueSpec.singleUInt16)   parts
                | UInt32Value  _ -> tryParseAll parseUInt32  (w (fun vs -> UInt32Value  (Multiple vs)) ValueSpec.singleUInt32)   parts
                | UInt64Value  _ -> tryParseAll parseUInt64  (w (fun vs -> UInt64Value  (Multiple vs)) ValueSpec.singleUInt64)   parts
                | Float32Value _ -> tryParseAll parseFloat32 (w (fun vs -> Float32Value (Multiple vs)) ValueSpec.singleFloat32)  parts
                | Float64Value _ -> tryParseAll parseFloat64 (w (fun vs -> Float64Value (Multiple vs)) ValueSpec.singleFloat64)  parts
                | StringValue  _ -> Some(StringValue (if isMultiple then Multiple parts else Single raw))
                | UndefinedValue -> None
            hinted |> Option.orElse (inferFromText raw)

    /// typeIndex + text → ValueSpec (패널 UI에서 사용)
    let parseFromPanel (typeIndex: int) (text: string) : ValueSpec =
        let baseSpec = specFromTypeIndex typeIndex
        tryParseAs baseSpec text |> Option.defaultValue baseSpec
