namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// Promaker 의 PropertyPanel / Tree / Canvas 가 같은 selection 상태에 대해
/// 동일한 boolean 분기를 갖도록 통합 projection.
/// </summary>
[<Sealed>]
type SelectionSummary(
    count: int,
    uniformKind: Nullable<EntityKind>,
    isSingleSelection: bool,
    isMultiSelection: bool,
    isSingleWorkSelected: bool,
    isSingleCallSelected: bool,
    isSingleSystemSelected: bool,
    isWorkSelected: bool,
    isCallSelected: bool) =
    member _.Count = count
    /// 모든 선택 키가 같은 EntityKind 일 때 그 값. 혼합 또는 비어있으면 null.
    member _.UniformKind = uniformKind
    member _.IsSingleSelection = isSingleSelection
    member _.IsMultiSelection = isMultiSelection
    member _.IsSingleWorkSelected = isSingleWorkSelected
    member _.IsSingleCallSelected = isSingleCallSelected
    member _.IsSingleSystemSelected = isSingleSystemSelected
    /// 모든 선택 키가 Work — 단일 또는 다중.
    member _.IsWorkSelected = isWorkSelected
    /// 모든 선택 키가 Call — 단일 또는 다중.
    member _.IsCallSelected = isCallSelected

    /// 선택 키 목록을 받아 통합 summary 반환.
    static member Build(selectedKeys: seq<SelectionKey>) : SelectionSummary =
        let keys = selectedKeys |> Seq.toList
        let count = keys.Length
        let uniformKind =
            match keys with
            | [] -> Nullable()
            | first :: _ when keys |> List.forall (fun k -> k.EntityKind = first.EntityKind) ->
                Nullable(first.EntityKind)
            | _ -> Nullable()

        let isSingle = count = 1
        let isMulti = count > 1
        let kindIs kind =
            uniformKind.HasValue && uniformKind.Value = kind

        SelectionSummary(
            count = count,
            uniformKind = uniformKind,
            isSingleSelection = isSingle,
            isMultiSelection = isMulti,
            isSingleWorkSelected = (isSingle && kindIs EntityKind.Work),
            isSingleCallSelected = (isSingle && kindIs EntityKind.Call),
            isSingleSystemSelected = (isSingle && kindIs EntityKind.System),
            isWorkSelected = kindIs EntityKind.Work,
            isCallSelected = kindIs EntityKind.Call)
