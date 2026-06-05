namespace Ds2.Editor

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

    // format / tryParseAs 의 ValueSpec 텍스트 parse/format 핵심 로직은 `Ds2.Core.ValueSpecText`
    // 공용 helper 로 추출됨 (todo-refactor-condition.md Phase 2 박제 결정 #4 — Editor/LlmAgent 공유).
    // 본 module 은 typeIndex (UI 콤보 인덱스) 결합 wrapper 만 유지.

    /// ValueSpec → 사람 친화 텍스트 (공용 helper 위임).
    let format (valueSpec: ValueSpec) = ValueSpecText.format valueSpec

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

    /// 힌트 타입으로 파싱 시도, 실패 시 타입 추론으로 폴백 (공용 helper 위임).
    let tryParseAs (hint: ValueSpec) (text: string) = ValueSpecText.tryParseAs hint text

    /// typeIndex + text → ValueSpec (패널 UI에서 사용)
    let parseFromPanel (typeIndex: int) (text: string) : ValueSpec =
        let baseSpec = specFromTypeIndex typeIndex
        tryParseAs baseSpec text |> Option.defaultValue baseSpec
