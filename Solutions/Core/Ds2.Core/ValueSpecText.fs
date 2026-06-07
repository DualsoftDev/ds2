namespace Ds2.Core

open System
open System.Globalization

/// ValueSpec 의 사람 친화 *텍스트* parser / formatter 공용 SSOT.
///
/// **추출 배경** (todo-refactor-condition.md Phase 2 박제 결정 #4):
/// 기존 로직은 `Ds2.Editor` 의 `internal PropertyPanelValueSpec` 에만 있어 `Ds2.LlmAgent`
/// (friend assembly 아님) 가 재사용할 수 없었다. condition leaf 의 `eq` sugar / typed
/// `inputSpec` 값 텍스트와 PropertyPanel UI 가 같은 parse/format 규칙을 쓰도록 `Ds2.Core`
/// 로 추출한다. `PropertyPanelValueSpec` 는 본 module 로 위임 (중복 helper 신설 금지).
///
/// 의존: `ValueSpec` (DU) 만 — typeIndex (UI 콤보 인덱스) 비의존. typeIndex ↔ ValueSpec
/// 매핑은 UI 전용이므로 `Ds2.Editor.ValueSpecTypeIndex` 에 잔류.
[<RequireQualifiedAccess>]
module ValueSpecText =

    // ─── format: ValueSpec → text ────────────────────────────────────────────

    let private formatTyped toText (spec: ValueSpec<'T>) =
        let formatBound toText (value, boundType) =
            let openToken, closeToken =
                match boundType with
                | BoundType.Open -> "(", ")"
                | BoundType.Closed -> "[", "]"
            $"{openToken}{toText value}{closeToken}"
        let formatBoundOr toText noneText boundOpt =
            boundOpt
            |> Option.map (formatBound toText)
            |> Option.defaultValue noneText
        let formatRangeSegment toText (segment: RangeSegment<'T>) =
            let lower = formatBoundOr toText "(-inf)" segment.Lower
            let upper = formatBoundOr toText "(+inf)" segment.Upper
            $"{lower}..{upper}"
        match spec with
        | Undefined -> "Undefined"
        | Single v -> toText v
        | Multiple vs -> vs |> List.map toText |> String.concat ", "
        | Ranges segments ->
            segments
            |> List.map (formatRangeSegment toText)
            |> String.concat "; "

    /// ValueSpec → 사람 친화 텍스트. 실수 정수값("10")에 소수점(".0") 보장하여
    /// 역파싱 시 정수 오인식 방지.
    let format (valueSpec: ValueSpec) =
        let ensureDecimalPoint (s: string) =
            if
                s.Equals("NaN", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Infinity", StringComparison.OrdinalIgnoreCase)
                || s.Equals("+Infinity", StringComparison.OrdinalIgnoreCase)
                || s.Equals("-Infinity", StringComparison.OrdinalIgnoreCase)
            then
                s
            elif s.Contains('.') || s.Contains('E') || s.Contains('e') then s
            else s + ".0"
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

    // ─── tryParseAs: hint + text → ValueSpec ──────────────────────────────────

    /// hint 타입으로 파싱 시도, 실패 시 타입 추론(bool→int64→float64→string)으로 폴백.
    /// `Multiple` 은 콤마 구분 + `..` 미포함 시 적용. `Ranges` 텍스트 입력은 미지원 (None 폴백).
    let tryParseAs (hint: ValueSpec) (text: string) =
        let inferFromText (raw: string) =
            match Boolean.TryParse(raw) with
            | true, b -> Some(ValueSpec.singleBool b)
            | _ ->
                match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, i -> Some(ValueSpec.singleInt64 i)
                | _ ->
                    match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, d -> Some(ValueSpec.singleFloat64 d)
                    | _ -> Some(ValueSpec.singleString raw)
        // TryParse 공통 헬퍼 — Single/Multiple 모두 처리
        let tryParseAll (tryParse: string -> bool * 'a) (wrap: 'a list -> ValueSpec) (parts: string list) =
            let parsed = parts |> List.choose (fun s -> match tryParse s with true, v -> Some v | _ -> None)
            if parsed.Length = parts.Length then Some(wrap parsed) else None
        // .NET TryParse 오버로드 해소용 래퍼
        let parseBool    (s: string) = Boolean.TryParse(s)
        let parseInt8    (s: string) = SByte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseInt16   (s: string) = Int16.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseInt32   (s: string) = Int32.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseInt64   (s: string) = Int64.TryParse(s,  NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseUInt8   (s: string) = Byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseUInt16  (s: string) = UInt16.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseUInt32  (s: string) = UInt32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        let parseUInt64  (s: string) = UInt64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
        // System.Single 명시 — namespace Ds2.Core 에서 ValueSpec 의 `Single` case 가 type `Single` 을 가림.
        let parseFloat32 (s: string) = System.Single.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
        let parseFloat64 (s: string) = Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
        // Single/Multiple 통합 래퍼
        let wrapAs isMultiple (multiWrap: 'a list -> ValueSpec) (singleWrap: 'a -> ValueSpec) : 'a list -> ValueSpec =
            if isMultiple then multiWrap else (List.head >> singleWrap)

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
                | StringValue  _ -> Some(if isMultiple then StringValue (Multiple parts) else ValueSpec.singleString raw)
                | UndefinedValue -> None
            hinted |> Option.orElse (inferFromText raw)

    // ─── eq / typed inputSpec 지원 (Phase 2) ───────────────────────────────────

    /// 단일 equality(`eq`) 를 표현 가능한 ValueSpec 인지 — `Single` 만 허용.
    /// `Multiple` / `Ranges` / `Undefined` 는 eq sugar 로 환원 불가 → typed inputSpec fallback 대상.
    let isSingleEquality (spec: ValueSpec) : bool =
        let single (s: ValueSpec<'T>) = match s with Single _ -> true | _ -> false
        match spec with
        | BoolValue    s -> single s
        | Int8Value    s -> single s
        | Int16Value   s -> single s
        | Int32Value   s -> single s
        | Int64Value   s -> single s
        | UInt8Value   s -> single s
        | UInt16Value  s -> single s
        | UInt32Value  s -> single s
        | UInt64Value  s -> single s
        | Float32Value s -> single s
        | Float64Value s -> single s
        | StringValue  s -> single s
        | UndefinedValue -> false

    /// ValueSpec 을 *타입 case* 만 유지한 default(`Single 0` 류) 로 환원 — eq 텍스트 파싱 hint 용.
    /// 값은 무의미(파싱이 덮어씀); case 만 보존하여 `tryParseAs` 가 대상 타입으로 파싱하도록 한다.
    let typeHintOf (spec: ValueSpec) : ValueSpec =
        match spec with
        | BoolValue    _ -> ValueSpec.singleBool false
        | Int8Value    _ -> ValueSpec.singleInt8 0y
        | Int16Value   _ -> ValueSpec.singleInt16 0s
        | Int32Value   _ -> ValueSpec.singleInt32 0
        | Int64Value   _ -> ValueSpec.singleInt64 0L
        | UInt8Value   _ -> ValueSpec.singleUInt8 0uy
        | UInt16Value  _ -> ValueSpec.singleUInt16 0us
        | UInt32Value  _ -> ValueSpec.singleUInt32 0u
        | UInt64Value  _ -> ValueSpec.singleUInt64 0UL
        | Float32Value _ -> ValueSpec.singleFloat32 0.0f
        | Float64Value _ -> ValueSpec.singleFloat64 0.0
        | StringValue  _ -> ValueSpec.singleString ""
        | UndefinedValue -> UndefinedValue

    /// 숫자 case 분류 — eq 숫자 token 의 타입 확정에 ApiDef metadata(참조 ApiCall ValueSpec)가
    /// 필요한지 판단용. bool/string 은 token 자체로 타입 확정 가능하므로 metadata 불요.
    let isNumericCase (spec: ValueSpec) : bool =
        match spec with
        | Int8Value _ | Int16Value _ | Int32Value _ | Int64Value _
        | UInt8Value _ | UInt16Value _ | UInt32Value _ | UInt64Value _
        | Float32Value _ | Float64Value _ -> true
        | BoolValue _ | StringValue _ | UndefinedValue -> false

    /// 두 ValueSpec 의 *타입 case* (내부 값 무관) 일치 여부. `typeHintOf` 가 case 별 고정 default 로
    /// 환원하므로 그 결과 equality 가 곧 case 일치. eq strict 파싱(범위 초과·타입 불일치가 다른
    /// case 로 살아나는 것 차단)과 emit 동적 eq↔inputSpec 판정(re-parse 복원 가능성 = hint case 일치)의
    /// 공용 술어. `tryParseAs` 의 inferFromText fallback 동작은 본 함수가 건드리지 않음 (Editor 보존).
    let sameCase (a: ValueSpec) (b: ValueSpec) : bool =
        typeHintOf a = typeHintOf b
