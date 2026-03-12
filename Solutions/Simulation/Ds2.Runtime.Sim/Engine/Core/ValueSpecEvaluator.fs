namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core

/// ValueSpec 평가 모듈 — ValueSpec DU와 현재 문자열 값을 직접 비교
module ValueSpecEvaluator =

    let private inRange<'T when 'T : comparison> (value: 'T) (segment: RangeSegment<'T>) : bool =
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

    let private containsTyped<'T when 'T : equality and 'T : comparison> (value: 'T) (spec: ValueSpec<'T>) : bool =
        match spec with
        | Undefined -> true
        | Single v -> v = value
        | Multiple vs -> vs |> List.contains value
        | Ranges segments -> segments |> List.exists (inRange value)

    /// ValueSpec DU와 현재 문자열 값 비교
    let evaluate (valueSpec: ValueSpec) (currentValue: string) : bool =
        match valueSpec with
        | UndefinedValue -> true
        | BoolValue spec ->
            match Boolean.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Int8Value spec ->
            match SByte.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Int16Value spec ->
            match Int16.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Int32Value spec ->
            match Int32.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Int64Value spec ->
            match Int64.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | UInt8Value spec ->
            match Byte.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | UInt16Value spec ->
            match UInt16.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | UInt32Value spec ->
            match UInt32.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | UInt64Value spec ->
            match UInt64.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Float32Value spec ->
            match Single.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | Float64Value spec ->
            match Double.TryParse(currentValue) with true, v -> containsTyped v spec | _ -> false
        | StringValue spec ->
            containsTyped currentValue spec

    /// ValueSpec이 "false" 값인지 확인
    let isFalseSpec (spec: ValueSpec) : bool =
        match spec with
        | BoolValue (Single false) -> true
        | _ -> false

    /// ValueSpec에서 기본 문자열 값 추출 (IO값 설정용)
    let toDefaultString (spec: ValueSpec) : string =
        match spec with
        | UndefinedValue -> "true"
        | BoolValue (Single v) -> string v
        | Int8Value (Single v) -> string v
        | Int16Value (Single v) -> string v
        | Int32Value (Single v) -> string v
        | Int64Value (Single v) -> string v
        | UInt8Value (Single v) -> string v
        | UInt16Value (Single v) -> string v
        | UInt32Value (Single v) -> string v
        | UInt64Value (Single v) -> string v
        | Float32Value (Single v) -> string v
        | Float64Value (Single v) -> string v
        | StringValue (Single v) -> v
        | _ -> "true"
