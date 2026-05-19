namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// PropertyPanel 의 batch 편집(IsFinished / TokenRole / CallType) 을
/// "store 조회 + 다음 값 계산 + batch update API 호출"까지 묶어 한 번에 처리한다.
/// C# 측은 단일 메서드 호출로 끝나고, option dereference 와 같은 F# 인터롭이 불필요해진다.
/// </summary>
[<Extension>]
type PanelEditApplyExtensions =

    /// 선택 Work 들에 동일한 IsFinished 값을 일괄 적용.
    [<Extension>]
    static member ApplyIsFinishedBatch(store: DsStore, workIds: seq<Guid>, value: bool) : unit =
        let changes = workIds |> Seq.map (fun id -> struct(id, value))
        store.UpdateWorkIsFinishedBatch(changes)

    /// 선택 Work 들에 TokenRole flag (Source/Ignore/Sink) on/off 를 적용.
    /// 각 Work 의 현재 role 을 조회해 다음 role 을 계산한 뒤 batch update.
    [<Extension>]
    static member ApplyTokenRoleFlagBatch
        (store: DsStore, workIds: seq<Guid>, flag: TokenRole, turnOn: bool) : unit =
        let changes =
            workIds
            |> Seq.map (fun workId ->
                let currentRole =
                    match Queries.getWork workId store with
                    | Some w -> w.TokenRole
                    | None -> TokenRole.None
                let nextRole = TokenRoleOps.computeNextTokenRole currentRole flag turnOn
                struct(workId, nextRole))
        store.UpdateWorkTokenRolesBatch(changes)

    /// 선택 Call 들 중 CallType 이 target 과 다른 것만 골라 단건 UpdateCallType 을 차례로 호출.
    /// (Batch 단일 transaction 이 아닌 기존 동작 그대로 유지 — Undo 단위도 호출 갯수만큼.)
    [<Extension>]
    static member ApplyCallTypeChange(store: DsStore, callIds: seq<Guid>, target: CallType) : unit =
        for callId in callIds do
            match Queries.getCall callId store with
            | Some c ->
                let current =
                    match c.GetSimulationProperties() with
                    | Some sim -> sim.CallType
                    | None -> CallType.WaitForCompletion
                if current <> target then
                    store.UpdateCallType(callId, target) |> ignore
            | None -> ()
