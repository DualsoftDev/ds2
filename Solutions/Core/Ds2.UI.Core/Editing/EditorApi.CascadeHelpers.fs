module Ds2.UI.Core.CascadeHelpers

open System
open Ds2.Core

/// Work ID 집합에 연결된 ArrowBetweenWorks 수집
let arrowWorksFor (store: DsStore) (workIds: Set<Guid>) =
    store.ArrowWorksReadOnly.Values
    |> Seq.filter (fun a -> workIds.Contains a.SourceId || workIds.Contains a.TargetId)
    |> Seq.toList

/// Call ID 집합에 연결된 ArrowBetweenCalls 수집
let arrowCallsFor (store: DsStore) (callIds: Set<Guid>) =
    store.ArrowCallsReadOnly.Values
    |> Seq.filter (fun a -> callIds.Contains a.SourceId || callIds.Contains a.TargetId)
    |> Seq.toList

/// 코어 엔티티 삭제 명령 생성 (말단→부모 순: ArrowCall → Call → ArrowWork → Work)
let coreRemoveCommands
    (arrowCalls: ArrowBetweenCalls list)
    (calls: Call list)
    (arrowWorks: ArrowBetweenWorks list)
    (works: Work list) =
    [
        yield! arrowCalls |> List.map (fun a -> RemoveArrowCall(DeepCopyHelper.backupEntityAs a))
        yield! calls      |> List.map (fun c -> RemoveCall(DeepCopyHelper.backupEntityAs c))
        yield! arrowWorks |> List.map (fun a -> RemoveArrowWork(DeepCopyHelper.backupEntityAs a))
        yield! works      |> List.map (fun w -> RemoveWork(DeepCopyHelper.backupEntityAs w))
    ]

/// Flow 삭제 명령 생성
let flowRemoveCommands (flows: Flow list) =
    flows |> List.map (fun f -> RemoveFlow(DeepCopyHelper.backupEntityAs f))

/// HW + ApiDef 삭제 명령 생성 (System 단위)
let hwRemoveCommands (store: DsStore) (systemIds: Guid list) =
    let apiDefs    = systemIds |> List.collect (fun id -> DsQuery.apiDefsOf id store)
    let buttons    = systemIds |> List.collect (fun id -> DsQuery.buttonsOf id store)
    let lamps      = systemIds |> List.collect (fun id -> DsQuery.lampsOf id store)
    let conditions = systemIds |> List.collect (fun id -> DsQuery.conditionsOf id store)
    let actions    = systemIds |> List.collect (fun id -> DsQuery.actionsOf id store)
    [
        yield! apiDefs    |> List.map (fun d -> RemoveApiDef(DeepCopyHelper.backupEntityAs d))
        yield! buttons    |> List.map (fun b -> RemoveButton(DeepCopyHelper.backupEntityAs b))
        yield! lamps      |> List.map (fun l -> RemoveLamp(DeepCopyHelper.backupEntityAs l))
        yield! conditions |> List.map (fun c -> RemoveHwCondition(DeepCopyHelper.backupEntityAs c))
        yield! actions    |> List.map (fun a -> RemoveHwAction(DeepCopyHelper.backupEntityAs a))
    ]

/// Works/Calls에서 관련 화살표까지 한 번에 수집
let collectDescendantArrows (store: DsStore) (works: Work list) (calls: Call list) =
    let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList
    let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList
    let arrowWorks = arrowWorksFor store workIds
    let arrowCalls = arrowCallsFor store callIds
    (arrowWorks, arrowCalls)
