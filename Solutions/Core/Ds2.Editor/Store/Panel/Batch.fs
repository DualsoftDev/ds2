namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

/// Duration 일괄편집용 행
type WorkDurationBatchRow(workId: Guid, systemName: string, flowName: string, workName: string, periodMs: int, isDeviceWork: bool) =
    member val WorkId = workId
    member val SystemName = systemName
    member val FlowName = flowName
    member val WorkName = workName
    member val PeriodMs = periodMs
    member val IsDeviceWork = isDeviceWork

/// I/O 일괄편집용 행
type ApiCallIOBatchRow(callId: Guid, apiCallId: Guid, flowName: string, workName: string, callName: string,
                       deviceName: string, apiName: string,
                       inAddress: string, inSymbol: string, outAddress: string, outSymbol: string,
                       outDataType: string, inDataType: string) =
    member val CallId = callId
    member val ApiCallId = apiCallId
    member val FlowName = flowName
    member val WorkName = workName
    member val CallName = callName
    member val DeviceName = deviceName
    member val ApiName = apiName
    member val InAddress = inAddress
    member val InSymbol = inSymbol
    member val OutAddress = outAddress
    member val OutSymbol = outSymbol
    member val OutDataType = outDataType
    member val InDataType = inDataType

[<Extension>]
type DsStorePanelBatchExtensions =

    /// 모든 Work의 Duration 정보를 일괄 조회
    [<Extension>]
    static member GetAllWorkDurationRows(store: DsStore) : WorkDurationBatchRow list =
        let rowsForSystems isDeviceWork (systems: DsSystem list) =
            systems
            |> List.collect (fun sys ->
                Queries.flowsOf sys.Id store
                |> List.collect (fun flow ->
                    Queries.worksOf flow.Id store
                    |> List.map (fun work ->
                        let ms = work.Properties.Duration |> Option.map (fun t -> int t.TotalMilliseconds) |> Option.defaultValue 0
                        WorkDurationBatchRow(work.Id, sys.Name, flow.Name, work.LocalName, ms, isDeviceWork))))

        Queries.allProjects store
        |> List.collect (fun project ->
            [
                yield! rowsForSystems false (Queries.activeSystemsOf project.Id store)
                yield! rowsForSystems true (Queries.passiveSystemsOf project.Id store)
            ])

    /// 모든 ApiCall의 IO 태그 정보를 일괄 조회
    [<Extension>]
    static member GetAllApiCallIORows(store: DsStore) : ApiCallIOBatchRow list =
        Queries.allProjects store
        |> List.collect (fun p -> Queries.projectSystemsOf p.Id store)
        |> List.collect (fun sys ->
            Queries.flowsOf sys.Id store
            |> List.collect (fun flow ->
                Queries.worksOf flow.Id store
                |> List.collect (fun work ->
                    Queries.callsOf work.Id store
                    |> List.collect (fun call ->
                        call.ApiCalls
                        |> Seq.map (fun apiCall ->
                            let inAddr = apiCall.InTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
                            let inSym  = apiCall.InTag |> Option.map (fun t -> t.Name)    |> Option.defaultValue ""
                            let outAddr = apiCall.OutTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
                            let outSym  = apiCall.OutTag |> Option.map (fun t -> t.Name)    |> Option.defaultValue ""

                            // Extract Device and Api names
                            let deviceName, apiName =
                                match apiCall.ApiDefId with
                                | Some apiDefId ->
                                    match store.ApiDefs.TryGetValue(apiDefId) with
                                    | true, apiDef ->
                                        let devName =
                                            match store.Systems.TryGetValue(apiDef.ParentId) with
                                            | true, system -> system.Name
                                            | false, _ -> "UNKNOWN"
                                        (devName, apiDef.Name)
                                    | false, _ -> ("UNKNOWN", "UNKNOWN")
                                | None -> ("UNKNOWN", "UNKNOWN")

                            // DataType: IOTag에는 DataType이 없으므로 기본값 "BOOL"
                            let outDataType = "BOOL"
                            let inDataType = "BOOL"

                            ApiCallIOBatchRow(call.Id, apiCall.Id, flow.Name, work.LocalName, call.Name, deviceName, apiName, inAddr, inSym, outAddr, outSym, outDataType, inDataType))
                        |> Seq.toList))))

    /// Work IsFinished 일괄 변경 (단일 Undo 트랜잭션)
    [<Extension>]
    static member UpdateWorkIsFinishedBatch(store: DsStore, changes: seq<struct(Guid * bool)>) =
        let changeList =
            changes
            |> Seq.map (fun struct(workId, isFinished) -> struct(Queries.resolveOriginalWorkId workId store, isFinished))
            |> Seq.distinctBy (fun struct(workId, _) -> workId)
            |> Seq.filter (fun struct(workId, isFinished) ->
                match Queries.getWork workId store with
                | Some work -> work.Properties.IsFinished <> isFinished
                | None -> false)
            |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateWorkIsFinishedBatch: {changeList.Length} items")
            store.WithTransaction("Work IsFinished 일괄 변경", fun () ->
                for struct(workId, isFinished) in changeList do
                    store.TrackMutate(store.Works, workId, fun work -> work.Properties.IsFinished <- isFinished))
            store.EmitRefreshAndHistory()

    /// Work Duration 일괄 변경 (단일 Undo 트랜잭션)
    [<Extension>]
    static member UpdateWorkDurationsBatch(store: DsStore, changes: seq<struct(Guid * int)>) =
        let changeList = changes |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateWorkDurationsBatch: {changeList.Length} items")
            store.WithTransaction("Work Duration 일괄 변경", fun () ->
                for struct(workId, newMs) in changeList do
                    let period = if newMs <= 0 then None else Some (TimeSpan.FromMilliseconds(float newMs))
                    store.TrackMutate(store.Works, workId, fun w -> w.Properties.Duration <- period))
            store.EmitRefreshAndHistory()

    /// Work Duration 일괄 변경 (Nullable 허용)
    [<Extension>]
    static member UpdateWorkPeriodsBatch(store: DsStore, changes: seq<struct(Guid * Nullable<int>)>) =
        let changeList =
            changes
            |> Seq.map (fun struct(workId, periodMs) ->
                let resolvedId = Queries.resolveOriginalWorkId workId store
                let period = if periodMs.HasValue && periodMs.Value > 0 then Some (TimeSpan.FromMilliseconds(float periodMs.Value)) else None
                struct(resolvedId, period))
            |> Seq.distinctBy (fun struct(workId, _) -> workId)
            |> Seq.filter (fun struct(workId, period) ->
                match Queries.getWork workId store with
                | Some work -> work.Properties.Duration <> period
                | None -> false)
            |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateWorkPeriodsBatch: {changeList.Length} items")
            store.WithTransaction("Work Duration 일괄 변경", fun () ->
                for struct(workId, period) in changeList do
                    store.TrackMutate(store.Works, workId, fun work -> work.Properties.Duration <- period))
            store.EmitRefreshAndHistory()

    /// Work TokenRole 일괄 변경 (단일 Undo 트랜잭션)
    [<Extension>]
    static member UpdateWorkTokenRolesBatch(store: DsStore, changes: seq<struct(Guid * TokenRole)>) =
        let changeList =
            changes
            |> Seq.map (fun struct(workId, role) -> struct(Queries.resolveOriginalWorkId workId store, role))
            |> Seq.distinctBy (fun struct(workId, _) -> workId)
            |> Seq.filter (fun struct(workId, role) ->
                match Queries.getWork workId store with
                | Some work -> work.TokenRole <> role
                | None -> false)
            |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateWorkTokenRolesBatch: {changeList.Length} items")
            store.WithTransaction("Work TokenRole 일괄 변경", fun () ->
                for struct(workId, role) in changeList do
                    store.TrackMutate(store.Works, workId, fun work -> work.TokenRole <- role))
            store.EmitRefreshAndHistory()

    /// Work TokenRole 플래그 토글 (비트 XOR 방식, 단일 Undo 트랜잭션)
    [<Extension>]
    static member ToggleWorkTokenRoleFlag(store: DsStore, workIds: seq<Guid>, flag: TokenRole) =
        let changes =
            workIds
            |> Seq.map (fun workId -> Queries.resolveOriginalWorkId workId store)
            |> Seq.distinct
            |> Seq.choose (fun workId ->
                match Queries.getWork workId store with
                | Some work ->
                    let current = work.TokenRole
                    let next = if current.HasFlag(flag) then current &&& ~~~flag else current ||| flag
                    if next <> current then Some (struct(workId, next)) else None
                | None -> None)
            |> Seq.toList
        if not changes.IsEmpty then
            StoreLog.debug($"ToggleWorkTokenRoleFlag: {changes.Length} items, flag={flag}")
            store.WithTransaction("Work TokenRole 토글", fun () ->
                for struct(workId, role) in changes do
                    store.TrackMutate(store.Works, workId, fun work -> work.TokenRole <- role))
            store.EmitRefreshAndHistory()

    /// ApiCall IO 태그 일괄 변경 (단일 Undo 트랜잭션)
    /// C#에서는 IOTag 또는 null을 넘기면 됩니다.
    [<Extension>]
    static member UpdateApiCallIOTagsBatch(store: DsStore, changes: seq<struct(Guid * IOTag * IOTag)>) =
        let changeList = changes |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateApiCallIOTagsBatch: {changeList.Length} items")
            store.WithTransaction("I/O 태그 일괄 변경", fun () ->
                for struct(apiCallId, inTag, outTag) in changeList do
                    store.TrackMutate(store.ApiCalls, apiCallId, fun apiCall ->
                        apiCall.InTag <- Option.ofObj inTag
                        apiCall.OutTag <- Option.ofObj outTag))
            store.EmitRefreshAndHistory()

    /// 이름 기반으로 ApiCall의 CallId와 ApiCallId 조회 (TAG Wizard용)
    [<Extension>]
    static member FindApiCallIds(store: DsStore, flowName: string, workName: string, callName: string, deviceName: string) : struct(Guid * Guid) option =
        Queries.allProjects store
        |> List.tryPick (fun p ->
            Queries.projectSystemsOf p.Id store
            |> List.tryPick (fun sys ->
                Queries.flowsOf sys.Id store
                |> List.tryPick (fun flow ->
                    if flow.Name = flowName then
                        Queries.worksOf flow.Id store
                        |> List.tryPick (fun work ->
                            if work.Name = workName then
                                Queries.callsOf work.Id store
                                |> List.tryPick (fun call ->
                                    if call.Name = callName then
                                        call.ApiCalls
                                        |> Seq.tryPick (fun apiCall ->
                                            let apiCallDeviceName =
                                                match apiCall.ApiDefId with
                                                | Some defId ->
                                                    match Queries.getApiDef defId store with
                                                    | Some apiDef -> apiDef.Name
                                                    | None -> ""
                                                | None -> ""
                                            if apiCallDeviceName = deviceName then
                                                Some (struct(call.Id, apiCall.Id))
                                            else
                                                None)
                                    else
                                        None)
                            else
                                None)
                    else
                        None)))

    /// Call Timeout 일괄 변경 (Nullable 허용)
    [<Extension>]
    static member UpdateCallTimeoutsBatch(store: DsStore, changes: seq<struct(Guid * Nullable<int>)>) =
        let changeList =
            changes
            |> Seq.map (fun struct(callId, timeoutMs) ->
                let timeout = if timeoutMs.HasValue && timeoutMs.Value > 0 then Some (TimeSpan.FromMilliseconds(float timeoutMs.Value)) else None
                struct(callId, timeout))
            |> Seq.distinctBy (fun struct(callId, _) -> callId)
            |> Seq.filter (fun struct(callId, timeout) ->
                match Queries.getCall callId store with
                | Some call -> call.Properties.Timeout <> timeout
                | None -> false)
            |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateCallTimeoutsBatch: {changeList.Length} items")
            store.WithTransaction("Call Timeout 일괄 변경", fun () ->
                for struct(callId, timeout) in changeList do
                    store.TrackMutate(store.Calls, callId, fun call -> call.Properties.Timeout <- timeout))
            store.EmitRefreshAndHistory()
