namespace Ds2.Editor

open System
open System.Linq
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// PropertyPanel.Refresh — Work / Call / System 영역의 선택 상태를 통합한 projection.
/// 각 record 는 store 의 raw 값에서 distinct/uniform 결정과 nullable 변환까지 마친 결과를 들고 있어,
/// C# 측은 단순히 ObservableProperty 에 set 만 하면 됨.
/// </summary>

[<Sealed>]
type WorkSelectionState(
    periodMs: Nullable<int>,
    deviceDurationMs: Nullable<int>,
    deviceDurationHint: string,
    hasLinkedTokenSpec: bool,
    linkedTokenSpecLabel: string,
    isWorkFinished: Nullable<bool>,
    tokenSourceState: Nullable<bool>,
    tokenIgnoreState: Nullable<bool>,
    tokenSinkState: Nullable<bool>) =

    member _.PeriodMs = periodMs
    member _.DeviceDurationMs = deviceDurationMs
    member _.DeviceDurationHint = deviceDurationHint
    member _.HasLinkedTokenSpec = hasLinkedTokenSpec
    member _.LinkedTokenSpecLabel = linkedTokenSpecLabel
    member _.IsWorkFinished = isWorkFinished
    member _.TokenSourceState = tokenSourceState
    member _.TokenIgnoreState = tokenIgnoreState
    member _.TokenSinkState = tokenSinkState

    /// Work 가 선택되지 않은 경우의 비어있는 상태.
    static member Empty =
        WorkSelectionState(
            Nullable(), Nullable(), "", false, "",
            Nullable(false), Nullable(false), Nullable(false), Nullable(false))

    /// <summary>
    /// Work 선택 상태 빌드.
    /// </summary>
    /// <param name="store">현재 store</param>
    /// <param name="canonicalWorkIds">선택된 Work 들의 canonical id 목록</param>
    /// <param name="singleResolvedWorkId">단일 Work 선택 시 ReferenceOfId 해석 후 id (devDuration 조회용). null 이면 다중 선택.</param>
    /// <param name="singleRawWorkId">단일 Work 선택 시 raw id (TokenSpec linked lookup 용). null 이면 다중 선택.</param>
    static member Build(
            store: DsStore,
            canonicalWorkIds: Guid seq,
            singleResolvedWorkId: Nullable<Guid>,
            singleRawWorkId: Nullable<Guid>) : WorkSelectionState =
        let ids = canonicalWorkIds |> Seq.toList
        if List.isEmpty ids then
            WorkSelectionState.Empty
        else
            // Period: distinct → uniform 또는 null
            let periodValues =
                ids
                |> List.map (fun wid -> DsStorePanelTimeExtensions.GetWorkPeriodMsOrNull(store, wid))
                |> List.distinct
            let periodMs =
                if periodValues.Length = 1 then periodValues.[0] else Nullable()

            // Single 일 때만 devDuration + linkedSpec 의미
            let mutable deviceDurationMs = Nullable<int>()
            let mutable deviceDurationHint = ""
            let mutable hasLinkedTokenSpec = false
            let mutable linkedTokenSpecLabel = ""

            if singleResolvedWorkId.HasValue then
                match Queries.tryGetDeviceDurationMs singleResolvedWorkId.Value store with
                | Some ms ->
                    deviceDurationMs <- Nullable(ms)
                    deviceDurationHint <- sprintf "예상 소요 시간: %dms" ms
                | None -> ()

            if singleRawWorkId.HasValue then
                let canonical = Queries.resolveOriginalWorkId singleRawWorkId.Value store
                let linked =
                    Queries.getTokenSpecs store
                    |> List.tryFind (fun spec ->
                        match spec.WorkId with
                        | Some wid -> Queries.resolveOriginalWorkId wid store = canonical
                        | None -> false)
                match linked with
                | Some spec ->
                    hasLinkedTokenSpec <- true
                    linkedTokenSpecLabel <- sprintf "#%O %s" spec.Id spec.Label
                | None -> ()

            // IsFinished distinct → uniform 또는 null
            let isFinishedValues =
                ids
                |> List.map (fun wid -> DsStorePanelTimeExtensions.GetWorkIsFinished(store, wid))
                |> List.distinct
            let isWorkFinished =
                if isFinishedValues.Length = 1 then Nullable(isFinishedValues.[0]) else Nullable()

            // Token roles → 3 flag state
            let workRoles =
                ids
                |> List.map (fun wid ->
                    match Queries.getWork wid store with
                    | Some w -> w.TokenRole
                    | None -> TokenRole.None)

            WorkSelectionState(
                periodMs = periodMs,
                deviceDurationMs = deviceDurationMs,
                deviceDurationHint = deviceDurationHint,
                hasLinkedTokenSpec = hasLinkedTokenSpec,
                linkedTokenSpecLabel = linkedTokenSpecLabel,
                isWorkFinished = isWorkFinished,
                tokenSourceState = TokenRoleOps.resolveTokenRoleFlagState (workRoles |> List.toSeq) TokenRole.Source,
                tokenIgnoreState = TokenRoleOps.resolveTokenRoleFlagState (workRoles |> List.toSeq) TokenRole.Ignore,
                tokenSinkState = TokenRoleOps.resolveTokenRoleFlagState (workRoles |> List.toSeq) TokenRole.Sink)


[<Sealed>]
type CallSelectionState(
    timeoutMs: Nullable<int>,
    callType: CallType) =

    member _.TimeoutMs = timeoutMs
    member _.CallType = callType

    static member Empty = CallSelectionState(Nullable(), CallType.WaitForCompletion)

    static member Build(store: DsStore, callIds: Guid seq) : CallSelectionState =
        let ids = callIds |> Seq.toList
        if List.isEmpty ids then
            CallSelectionState.Empty
        else
            let timeoutValues =
                ids
                |> List.map (fun cid -> DsStorePanelTimeExtensions.GetCallTimeoutMsOrNull(store, cid))
                |> List.distinct
            let timeoutMs =
                if timeoutValues.Length = 1 then timeoutValues.[0] else Nullable()

            let callTypeValues =
                ids
                |> List.map (fun cid ->
                    match Queries.getCall cid store with
                    | Some c ->
                        match c.GetSimulationProperties() with
                        | Some sim -> sim.CallType
                        | None -> CallType.WaitForCompletion
                    | None -> CallType.WaitForCompletion)
                |> List.distinct
            let callType =
                if callTypeValues.Length = 1 then callTypeValues.[0] else CallType.WaitForCompletion

            CallSelectionState(timeoutMs = timeoutMs, callType = callType)


/// 단일 System 선택 시 SystemType 결정 (다른 System 패널 호출은 C# 에서 진행).
module SystemSelectionState =

    /// 선택된 System 의 SystemType (없으면 ""). System 자체가 없으면 "".
    [<CompiledName("ResolveSystemType")>]
    let resolveSystemType (store: DsStore) (systemId: Guid) : string =
        match Queries.getSystem systemId store with
        | Some sys ->
            match sys.SystemType with
            | Some t when not (System.String.IsNullOrEmpty t) -> t
            | _ -> ""
        | None -> ""
