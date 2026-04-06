namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

[<Extension>]
type DsStorePanelPropertiesExtensions =
    /// 프로젝트 속성 일괄 변경 (Undo 지원)
    [<Extension>]
    static member UpdateProjectProperties(store: DsStore, author: string, dateTime: DateTimeOffset,
                                          version: string) =
        StoreLog.debug($"UpdateProjectProperties")
        let project = Queries.allProjects store |> List.head
        store.WithTransaction("프로젝트 속성 변경", fun () ->
            store.TrackMutate(store.Projects, project.Id, fun p ->
                p.Author <- author
                p.DateTime <- dateTime
                p.Version <- version))

    /// 시스템 타입 변경 (Undo 지원)
    [<Extension>]
    static member UpdateSystemType(store: DsStore, systemId: Guid, systemType: string) =
        StoreLog.debug($"UpdateSystemType systemId={systemId}, systemType={systemType}")
        store.WithTransaction("시스템 타입 변경", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                match sys.GetSimulationProperties() with
                | Some props -> props.SystemType <- DirectPanelOps.toOpt systemType
                | None ->
                    let props = SimulationSystemProperties()
                    props.SystemType <- DirectPanelOps.toOpt systemType
                    sys.SetSimulationProperties(props)))

    /// ApiCall의 IO 태그 정보 업데이트 (TAG Wizard에서 사용)
    /// C#에서는 IOTag 또는 null을 넘기면 됩니다.
    [<Extension>]
    static member UpdateApiCallIoTags(store: DsStore, callId: Guid, apiCallId: Guid,
                                      outTag: IOTag, inTag: IOTag) : bool =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"UpdateApiCallIoTags callId={callId}, apiCallId={apiCallId}")
        DirectPanelOps.mutateCallProps store callId "IO 태그 업데이트" (fun call ->
            let targetApiCall =
                call.ApiCalls
                |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
                |> Option.defaultWith (fun () ->
                    failwith $"ApiCall {apiCallId} not found in Call {callId}")
            targetApiCall.OutTag <- Option.ofObj outTag
            targetApiCall.InTag <- Option.ofObj inTag)
        true
