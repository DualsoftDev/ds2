namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store

[<Extension>]
type DsStorePanelPropertiesExtensions =
    /// 프로젝트 속성 일괄 변경 (Undo 지원)
    [<Extension>]
    static member UpdateProjectProperties(store: DsStore, iriPrefix: string, globalAssetId: string, author: string, version: string, description: string, splitDeviceAasx: bool,
                                          presetSystemTypes: string[]) =
        StoreLog.debug($"UpdateProjectProperties iri={iriPrefix}")
        let project = DsQuery.allProjects store |> List.head
        store.WithTransaction("프로젝트 속성 변경", fun () ->
            store.TrackMutate(store.Projects, project.Id, fun p ->
                p.Properties.IriPrefix     <- DirectPanelOps.toOpt iriPrefix
                p.Properties.GlobalAssetId <- DirectPanelOps.toOpt globalAssetId
                p.Properties.Author        <- DirectPanelOps.toOpt author
                p.Properties.Version       <- DirectPanelOps.toOpt version
                p.Properties.Description   <- DirectPanelOps.toOpt description
                p.Properties.SplitDeviceAasx <- splitDeviceAasx
                ProjectPropertiesHelper.setPresetSystemTypes p.Properties presetSystemTypes))

    /// 시스템 타입 변경 (Undo 지원)
    [<Extension>]
    static member UpdateSystemType(store: DsStore, systemId: Guid, systemType: string) =
        StoreLog.debug($"UpdateSystemType systemId={systemId}, systemType={systemType}")
        store.WithTransaction("시스템 타입 변경", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                sys.Properties.SystemType <- DirectPanelOps.toOpt systemType))

    /// <summary>
    /// ApiCall의 IO 태그 정보 업데이트 (TAG Wizard에서 사용)
    /// </summary>
    [<Extension>]
    static member UpdateApiCallIoTags(store: DsStore, callId: Guid, apiCallId: Guid,
                                      outSymbol: string, outAddress: string,
                                      inSymbol: string, inAddress: string) : bool =
        StoreLog.debug($"UpdateApiCallIoTags callId={callId}, apiCallId={apiCallId}")

        let createTag name addr =
            match DirectPanelOps.toOpt name, DirectPanelOps.toOpt addr with
            | Some n, Some a ->
                let tag = IOTag()
                tag.Name <- n
                tag.Address <- a
                Some tag
            | _ -> None

        let newOutTag = createTag outSymbol outAddress
        let newInTag = createTag inSymbol inAddress

        DirectPanelOps.mutateCallProps store callId "IO 태그 업데이트" (fun call ->
            let targetApiCall =
                call.ApiCalls
                |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
                |> Option.defaultWith (fun () ->
                    failwith $"ApiCall {apiCallId} not found in Call {callId}")

            targetApiCall.OutTag <- newOutTag
            targetApiCall.InTag <- newInTag)
        true
