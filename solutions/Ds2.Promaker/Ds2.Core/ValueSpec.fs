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

/// 타입 안전한 비제네릭 ValueSpec (런타임 캐스팅 위험 제거)
type ValueSpec =
    | UndefinedValue
    | IntValue of ValueSpec<int>
    | FloatValue of ValueSpec<decimal>
    | StringValue of ValueSpec<string>
    | BoolValue of ValueSpec<bool>

/// ValueSpec 생성 헬퍼
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ValueSpec =

    let private boundOpen<'T> (value: 'T) : Bound<'T> = (value, Open)
    let private boundClosed<'T> (value: 'T) : Bound<'T> = (value, Closed)
    let private segment<'T> (lower: Bound<'T> option) (upper: Bound<'T> option) : RangeSegment<'T> =
        { Lower = lower; Upper = upper }

    let singleInt (value: int) : ValueSpec = IntValue (Single value)
    let singleFloat (value: decimal) : ValueSpec = FloatValue (Single value)
    let singleString (value: string) : ValueSpec = StringValue (Single value)
    let singleBool (value: bool) : ValueSpec = BoolValue (Single value)
    let multipleInt (values: int list) : ValueSpec = IntValue (Multiple values)
    let multipleFloat (values: decimal list) : ValueSpec = FloatValue (Multiple values)
    let multipleString (values: string list) : ValueSpec = StringValue (Multiple values)
    let multipleBool (values: bool list) : ValueSpec = BoolValue (Multiple values)
    let rangesInt (segments: RangeSegment<int> list) : ValueSpec = IntValue (Ranges segments)
    let rangesFloat (segments: RangeSegment<decimal> list) : ValueSpec = FloatValue (Ranges segments)

    // 닫힌 구간 튜플 입력: (하한 option, 상한 option)
    // 예: [Some 10, Some 20; None, Some 0]
    let rangesIntClosed (segments: (int option * int option) list) : ValueSpec =
        segments
        |> List.map (fun (lower, upper) ->
            segment (lower |> Option.map boundClosed) (upper |> Option.map boundClosed))
        |> rangesInt

    let rangesFloatClosed (segments: (decimal option * decimal option) list) : ValueSpec =
        segments
        |> List.map (fun (lower, upper) ->
            segment (lower |> Option.map boundClosed) (upper |> Option.map boundClosed))
        |> rangesFloat

    let undefined : ValueSpec = UndefinedValue
