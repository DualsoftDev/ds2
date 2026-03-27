namespace Ds2.ThreeDView

open System

// =============================================================================
// Selection State — 선택/하이라이트 상태 관리
// =============================================================================

module SelectionState =

    type State =
        {
            Current: SelectionEvent option
            HighlightedNodeIds: Set<Guid>
            ConnectionArrows: (Guid * Guid * ArrowKind) list
        }

    let empty : State =
        {
            Current = None
            HighlightedNodeIds = Set.empty
            ConnectionArrows = []
        }

    let clear : State = empty

    /// Work 선택 시: 연결된 Work 하이라이트 + 화살표 생성
    let selectWork (work: WorkNode) (_allWorks: WorkNode list) : State =
        let inIds = work.IncomingWorkIds |> Set.ofList
        let outIds = work.OutgoingWorkIds |> Set.ofList

        let highlighted =
            Set.add work.Id (Set.union inIds outIds)

        let inArrows =
            work.IncomingWorkIds
            |> List.map (fun srcId -> (srcId, work.Id, ArrowKind.Incoming))

        let outArrows =
            work.OutgoingWorkIds
            |> List.map (fun tgtId -> (work.Id, tgtId, ArrowKind.Outgoing))

        {
            Current = Some (WorkSelected work.Id)
            HighlightedNodeIds = highlighted
            ConnectionArrows = inArrows @ outArrows
        }

    /// Call 선택 시: 해당 Call의 디바이스 하이라이트
    let selectCall (call: CallNode) (devices: DeviceNode list) : State =
        let deviceIds =
            devices
            |> List.filter (fun d -> d.Name = call.DevicesAlias)
            |> List.map (fun d -> d.Id)
            |> Set.ofList

        {
            Current = Some (CallSelected call.Id)
            HighlightedNodeIds = Set.add call.WorkId deviceIds
            ConnectionArrows = []
        }

    /// Device 선택 시: 해당 디바이스의 ApiDef 하이라이트
    let selectDevice (device: DeviceNode) : State =
        {
            Current = Some (DeviceSelected device.Id)
            HighlightedNodeIds = Set.singleton device.Id
            ConnectionArrows = []
        }

    /// ApiDef 선택
    let selectApiDef (deviceId: Guid) (apiDefName: string) : State =
        {
            Current = Some (ApiDefSelected (deviceId, apiDefName))
            HighlightedNodeIds = Set.singleton deviceId
            ConnectionArrows = []
        }

    /// SelectionEvent로부터 State 생성
    let applyEvent (event: SelectionEvent) (workNodes: WorkNode list) (deviceNodes: DeviceNode list) : State =
        match event with
        | WorkSelected workId ->
            workNodes
            |> List.tryFind (fun w -> w.Id = workId)
            |> Option.map (fun w -> selectWork w workNodes)
            |> Option.defaultValue empty

        | CallSelected callId ->
            let call =
                workNodes
                |> List.collect (fun w -> w.Calls)
                |> List.tryFind (fun c -> c.Id = callId)
            match call with
            | Some c -> selectCall c deviceNodes
            | None -> empty

        | DeviceSelected deviceId ->
            deviceNodes
            |> List.tryFind (fun d -> d.Id = deviceId)
            |> Option.map selectDevice
            |> Option.defaultValue empty

        | ApiDefSelected (deviceId, apiDefName) ->
            selectApiDef deviceId apiDefName

        | EmptySpaceSelected ->
            empty
