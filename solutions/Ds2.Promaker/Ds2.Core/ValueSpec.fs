namespace Ds2.Core

open System

/// ValueSpec 마커 인터페이스
type IValueSpec = interface end

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

    interface IValueSpec

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

    let private boundOpen<'T> (value: 'T) : Bound<'T> = (value, Open)
    let private boundClosed<'T> (value: 'T) : Bound<'T> = (value, Closed)
    let private segment<'T> (lower: Bound<'T> option) (upper: Bound<'T> option) : RangeSegment<'T> =
        { Lower = lower; Upper = upper }

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
        segments
        |> List.map (fun (lower, upper) ->
            segment (lower |> Option.map boundClosed) (upper |> Option.map boundClosed))
        |> rangesInt32

    let undefined : ValueSpec = UndefinedValue
