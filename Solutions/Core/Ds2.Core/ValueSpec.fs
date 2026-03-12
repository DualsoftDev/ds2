namespace Ds2.Core

open System

/// 범위 경계 타입 (열림/닫힘)
type BoundType = Open | Closed

/// 범위 경계 (값과 경계 타입)
type Bound<'T> = 'T * BoundType

/// 범위 구간
type RangeSegment<'T> = {
    Lower: Bound<'T> option
    Upper: Bound<'T> option
}

/// 값 명세 (단일 값, 복수 값, 범위)
type ValueSpec<'T when 'T : equality and 'T : comparison> =
    | Undefined
    | Single of 'T
    | Multiple of 'T list
    | Ranges of RangeSegment<'T> list

/// 타입 안전한 비제네릭 ValueSpec — 비트폭 정보를 JSON에서 완전히 보존
type ValueSpec =
    | UndefinedValue
    | BoolValue    of ValueSpec<bool>
    | Int8Value    of ValueSpec<sbyte>
    | Int16Value   of ValueSpec<int16>
    | Int32Value   of ValueSpec<int>
    | Int64Value   of ValueSpec<int64>
    | UInt8Value   of ValueSpec<byte>
    | UInt16Value  of ValueSpec<uint16>
    | UInt32Value  of ValueSpec<uint32>
    | UInt64Value  of ValueSpec<uint64>
    | Float32Value of ValueSpec<float32>   // System.Single (단정밀도)
    | Float64Value of ValueSpec<float>     // System.Double (배정밀도, F#의 float)
    | StringValue  of ValueSpec<string>

/// ValueSpec 생성 헬퍼
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ValueSpec =

    let singleBool    (value: bool)    = BoolValue    (Single value)
    let singleInt8    (value: sbyte)   = Int8Value    (Single value)
    let singleInt16   (value: int16)   = Int16Value   (Single value)
    let singleInt32   (value: int)     = Int32Value   (Single value)
    let singleInt64   (value: int64)   = Int64Value   (Single value)
    let singleUInt8   (value: byte)    = UInt8Value   (Single value)
    let singleUInt16  (value: uint16)  = UInt16Value  (Single value)
    let singleUInt32  (value: uint32)  = UInt32Value  (Single value)
    let singleUInt64  (value: uint64)  = UInt64Value  (Single value)
    let singleFloat32 (value: float32) = Float32Value (Single value)
    let singleFloat64 (value: float)   = Float64Value (Single value)
    let singleString  (value: string)  = StringValue  (Single value)

    let rangesInt32 (segments: RangeSegment<int> list) : ValueSpec = Int32Value (Ranges segments)

    // 닫힌 구간 튜플 입력: (하한 option, 상한 option)
    let rangesInt32Closed (segments: (int option * int option) list) : ValueSpec =
        let boundClosed (value: 'T) : Bound<'T> = (value, Closed)
        let segment (lower: Bound<'T> option) (upper: Bound<'T> option) : RangeSegment<'T> = { Lower = lower; Upper = upper }
        segments
        |> List.map (fun (lower, upper) ->
            segment (lower |> Option.map boundClosed) (upper |> Option.map boundClosed))
        |> rangesInt32

    // ── 값 추출 ────────────────────────────────────────────────────

    /// Single 케이스의 값을 문자열로 추출 (없으면 "true")
    let toDefaultString (spec: ValueSpec) : string =
        match spec with
        | UndefinedValue -> "true"
        | BoolValue    (Single v) -> string v
        | Int8Value    (Single v) -> string v
        | Int16Value   (Single v) -> string v
        | Int32Value   (Single v) -> string v
        | Int64Value   (Single v) -> string v
        | UInt8Value   (Single v) -> string v
        | UInt16Value  (Single v) -> string v
        | UInt32Value  (Single v) -> string v
        | UInt64Value  (Single v) -> string v
        | Float32Value (Single v) -> string v
        | Float64Value (Single v) -> string v
        | StringValue  (Single v) -> v
        | _ -> "true"

    /// BoolValue(Single false)인지 확인
    let isFalse (spec: ValueSpec) : bool =
        match spec with
        | BoolValue (Single false) -> true
        | _ -> false

    // ── 값 평가 ────────────────────────────────────────────────────

    /// ValueSpec DU와 현재 문자열 값 비교
    let evaluate (valueSpec: ValueSpec) (currentValue: string) : bool =
        let inRange (value: 'T) (segment: RangeSegment<'T>) =
            let lowerOk =
                match segment.Lower with
                | None -> true
                | Some (bound, Closed) -> value >= bound
                | Some (bound, Open) -> value > bound
            let upperOk =
                match segment.Upper with
                | None -> true
                | Some (bound, Closed) -> value <= bound
                | Some (bound, Open) -> value < bound
            lowerOk && upperOk
        let containsTyped (value: 'T) (spec: ValueSpec<'T>) =
            match spec with
            | Undefined -> true
            | Single v -> v = value
            | Multiple vs -> vs |> List.contains value
            | Ranges segments -> segments |> List.exists (inRange value)
        let inline tryParse parser spec = 
            match parser currentValue with true, v -> containsTyped v spec | _ -> false
        match valueSpec with
        | UndefinedValue   -> true
        | BoolValue    spec -> tryParse Boolean.TryParse spec
        | Int8Value    spec -> tryParse SByte.TryParse   spec
        | Int16Value   spec -> tryParse Int16.TryParse   spec
        | Int32Value   spec -> tryParse Int32.TryParse   spec
        | Int64Value   spec -> tryParse Int64.TryParse   spec
        | UInt8Value   spec -> tryParse Byte.TryParse    spec
        | UInt16Value  spec -> tryParse UInt16.TryParse  spec
        | UInt32Value  spec -> tryParse UInt32.TryParse  spec
        | UInt64Value  spec -> tryParse UInt64.TryParse  spec
        | Float32Value spec -> tryParse Single.TryParse  spec
        | Float64Value spec -> tryParse Double.TryParse  spec
        | StringValue  spec -> containsTyped currentValue spec