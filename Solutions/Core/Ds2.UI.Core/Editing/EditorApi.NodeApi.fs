namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// EditorNodeApi — 노드(Project/System/Flow/Work/Call/ApiDef) 추가/삭제/이동/복사 진입점
// =============================================================================

type EditorNodeApi(store: DsStore, exec: ExecFn, batchExec: BatchExecFn) =

    member _.AddProjectAndGetId(name: string) : Guid =
        let project = Project(name)
        exec(AddProject project)
        project.Id

    member _.AddSystemAndGetId(name: string, projectId: Guid, isActive: bool) : Guid =
        let system = DsSystem(name)
        exec(AddSystem(system, projectId, isActive))
        system.Id

    member _.AddFlowAndGetId(name: string, systemId: Guid) : Guid =
        let flow = Flow(name, systemId)
        exec(AddFlow flow)
        flow.Id

    member _.AddWorkAndGetId(name: string, flowId: Guid) : Guid =
        let work = Work(name, flowId)
        exec(AddWork work)
        work.Id

    member _.AddCallsWithDevice
        (projectId: Guid)
        (workId: Guid)
        (callNames: string seq)
        (createDeviceSystem: bool) =
        let cmds = DeviceOps.buildAddCallsWithDeviceCmds store projectId workId (callNames |> Seq.toList) createDeviceSystem
        batchExec "Add Calls" (fun bExec -> cmds |> List.iter bExec)

    member _.AddCallWithLinkedApiDefsAndGetId
        (workId: Guid)
        (devicesAlias: string)
        (apiName: string)
        (apiDefIds: Guid seq)
        : Guid =
        let call, cmd = DeviceOps.buildAddCallWithLinkedApiDefsCmd store workId devicesAlias apiName apiDefIds
        exec cmd
        call.Id

    member _.AddApiDefAndGetId(name: string, systemId: Guid) : Guid =
        let apiDef = ApiDef(name, systemId)
        exec(AddApiDef apiDef)
        apiDef.Id

    member _.MoveEntities(requests: seq<MoveEntityRequest>) : int =
        let cmds = RemoveOps.buildMoveEntitiesCmds store requests
        batchExec "Move Selected Nodes" (fun bExec -> cmds |> List.iter bExec)
        cmds.Length

    member this.MoveEntitiesUi(requests: seq<UiMoveEntityRequest>) : int =
        requests
        |> Seq.map (fun request ->
            let newPos =
                if request.HasPosition then
                    Some(Xywh(request.X, request.Y, request.W, request.H))
                else
                    None
            MoveEntityRequest(request.EntityType, request.Id, newPos))
        |> this.MoveEntities

    member _.RemoveEntities(selections: seq<string * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        let cmds = RemoveOps.buildRemoveEntitiesCmds store selList
        batchExec "Delete Entities" (fun bExec -> cmds |> List.iter bExec)

    member _.RenameEntity(id: Guid, entityType: string, newName: string) =
        match EntityNameAccess.tryGetName store entityType id with
        | Some oldName when oldName <> newName ->
            exec(RenameEntity(id, entityType, oldName, newName))
        | _ -> ()

    member _.PasteEntities
        (copiedEntityType: string, copiedEntityIds: seq<Guid>, targetEntityType: string, targetEntityId: Guid)
        : int =
        let ids = copiedEntityIds |> Seq.distinct |> Seq.toList
        let mutable result = 0
        batchExec $"Paste {copiedEntityType}s" (fun bExec ->
            result <- PasteOps.dispatchPaste bExec store copiedEntityType ids targetEntityType targetEntityId)
        result
