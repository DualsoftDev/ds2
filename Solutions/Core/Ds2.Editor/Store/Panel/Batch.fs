namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store

/// Duration 일괄편집용 행
type WorkDurationBatchRow(workId: Guid, flowName: string, workName: string, periodMs: int) =
    member val WorkId = workId
    member val FlowName = flowName
    member val WorkName = workName
    member val PeriodMs = periodMs

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
        DsQuery.allProjects store
        |> List.collect (fun p -> DsQuery.projectSystemsOf p.Id store)
        |> List.collect (fun sys ->
            DsQuery.flowsOf sys.Id store
            |> List.collect (fun flow ->
                DsQuery.worksOf flow.Id store
                |> List.map (fun work ->
                    let ms = work.Properties.Period |> Option.map (fun t -> int t.TotalMilliseconds) |> Option.defaultValue 0
                    WorkDurationBatchRow(work.Id, flow.Name, work.LocalName, ms))))

    /// 모든 ApiCall의 IO 태그 정보를 일괄 조회
    [<Extension>]
    static member GetAllApiCallIORows(store: DsStore) : ApiCallIOBatchRow list =
        DsQuery.allProjects store
        |> List.collect (fun p -> DsQuery.projectSystemsOf p.Id store)
        |> List.collect (fun sys ->
            DsQuery.flowsOf sys.Id store
            |> List.collect (fun flow ->
                DsQuery.worksOf flow.Id store
                |> List.collect (fun work ->
                    DsQuery.callsOf work.Id store
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

                            ApiCallIOBatchRow(call.Id, apiCall.Id, flow.Name, work.Name, call.Name, deviceName, apiName, inAddr, inSym, outAddr, outSym, outDataType, inDataType))
                        |> Seq.toList))))

    /// Work Duration 일괄 변경 (단일 Undo 트랜잭션)
    [<Extension>]
    static member UpdateWorkDurationsBatch(store: DsStore, changes: seq<struct(Guid * int)>) =
        let changeList = changes |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateWorkDurationsBatch: {changeList.Length} items")
            store.WithTransaction("Work Duration 일괄 변경", fun () ->
                for struct(workId, newMs) in changeList do
                    let period = if newMs <= 0 then None else Some (TimeSpan.FromMilliseconds(float newMs))
                    store.TrackMutate(store.Works, workId, fun w -> w.Properties.Period <- period))
            store.EmitRefreshAndHistory()

    /// ApiCall IO 태그 일괄 변경 (단일 Undo 트랜잭션)
    [<Extension>]
    static member UpdateApiCallIOTagsBatch(store: DsStore, changes: seq<struct(Guid * string * string * string * string)>) =
        let changeList = changes |> Seq.toList
        if not changeList.IsEmpty then
            StoreLog.debug($"UpdateApiCallIOTagsBatch: {changeList.Length} items")
            store.WithTransaction("I/O 태그 일괄 변경", fun () ->
                for struct(apiCallId, inAddr, inSym, outAddr, outSym) in changeList do
                    store.TrackMutate(store.ApiCalls, apiCallId, fun apiCall ->
                        if String.IsNullOrWhiteSpace(inAddr) && String.IsNullOrWhiteSpace(inSym) then
                            apiCall.InTag <- None
                        else
                            apiCall.InTag <- Some(IOTag((if isNull inSym then "" else inSym), (if isNull inAddr then "" else inAddr), ""))
                        if String.IsNullOrWhiteSpace(outAddr) && String.IsNullOrWhiteSpace(outSym) then
                            apiCall.OutTag <- None
                        else
                            apiCall.OutTag <- Some(IOTag((if isNull outSym then "" else outSym), (if isNull outAddr then "" else outAddr), ""))))
            store.EmitRefreshAndHistory()

    /// 이름 기반으로 ApiCall의 CallId와 ApiCallId 조회 (TAG Wizard용)
    [<Extension>]
    static member FindApiCallIds(store: DsStore, flowName: string, workName: string, callName: string, deviceName: string) : struct(Guid * Guid) option =
        DsQuery.allProjects store
        |> List.tryPick (fun p ->
            DsQuery.projectSystemsOf p.Id store
            |> List.tryPick (fun sys ->
                DsQuery.flowsOf sys.Id store
                |> List.tryPick (fun flow ->
                    if flow.Name = flowName then
                        DsQuery.worksOf flow.Id store
                        |> List.tryPick (fun work ->
                            if work.Name = workName then
                                DsQuery.callsOf work.Id store
                                |> List.tryPick (fun call ->
                                    if call.Name = callName then
                                        call.ApiCalls
                                        |> Seq.tryPick (fun apiCall ->
                                            // DeviceName을 ApiCall의 ApiDef 이름과 비교
                                            let apiCallDeviceName =
                                                match apiCall.ApiDefId with
                                                | Some defId ->
                                                    match DsQuery.getApiDef defId store with
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
