namespace Ds2.ThreeDView

open System
open Ds2.Core
open Ds2.Store

// =============================================================================
// Context Builder — DsStore → 3D View DTO 변환
// =============================================================================

module ContextBuilder =

    // ─────────────────────────────────────────────────────────────────────
    // CallNode 빌드
    // ─────────────────────────────────────────────────────────────────────

    let private buildCallNode (work: Work) (flowName: string) (call: Call) (callArrows: ArrowBetweenCalls list) : CallNode =
        let nextId =
            callArrows
            |> List.tryFind (fun (a: ArrowBetweenCalls) -> a.SourceId = call.Id)
            |> Option.map (fun (a: ArrowBetweenCalls) -> a.TargetId)

        let prevId =
            callArrows
            |> List.tryFind (fun (a: ArrowBetweenCalls) -> a.TargetId = call.Id)
            |> Option.map (fun (a: ArrowBetweenCalls) -> a.SourceId)

        let apiDefName =
            if call.ApiCalls.Count > 0 then
                call.ApiCalls.[0].Name
            else
                call.ApiName

        {
            Id = call.Id
            WorkId = work.Id
            WorkName = work.Name
            FlowName = flowName
            State = call.Status4
            DevicesAlias = call.DevicesAlias
            ApiDefName = apiDefName
            NextCallId = nextId
            PrevCallId = prevId
        }

    // ─────────────────────────────────────────────────────────────────────
    // WorkNode 빌드
    // ─────────────────────────────────────────────────────────────────────

    /// Flow 내의 Work들을 WorkNode 리스트로 변환
    let buildWorkNodes (store: DsStore) (flowId: Guid) : WorkNode list =
        let flow = DsQuery.getFlow flowId store
        let flowName =
            flow |> Option.map (fun (f: Flow) -> f.Name) |> Option.defaultValue ""

        let works = DsQuery.worksOf flowId store

        // System ID for arrow queries
        let systemId =
            works
            |> List.tryHead
            |> Option.bind (fun (w: Work) -> DsQuery.trySystemIdOfWork w.Id store)

        // Work간 화살표
        let workArrows =
            match systemId with
            | Some sId -> DsQuery.arrowWorksOf sId store
            | None -> []

        // Work ID 집합
        let workIds = works |> List.map (fun (w: Work) -> w.Id) |> Set.ofList

        works
        |> List.map (fun (work: Work) ->
            let calls = DsQuery.callsOf work.Id store
            let callArrows = DsQuery.arrowCallsOf work.Id store

            let callNodes =
                calls |> List.map (fun (c: Call) -> buildCallNode work flowName c callArrows)

            let incoming =
                workArrows
                |> List.filter (fun (a: ArrowBetweenWorks) -> a.TargetId = work.Id && Set.contains a.SourceId workIds)
                |> List.map (fun (a: ArrowBetweenWorks) -> a.SourceId)

            let outgoing =
                workArrows
                |> List.filter (fun (a: ArrowBetweenWorks) -> a.SourceId = work.Id && Set.contains a.TargetId workIds)
                |> List.map (fun (a: ArrowBetweenWorks) -> a.TargetId)

            {
                Id = work.Id
                Name = work.Name
                FlowName = flowName
                FlowId = flowId
                State = work.Status4
                IncomingWorkIds = incoming
                OutgoingWorkIds = outgoing
                Calls = callNodes
                Position = None
            })

    // ─────────────────────────────────────────────────────────────────────
    // DeviceNode 빌드
    // ─────────────────────────────────────────────────────────────────────

    /// Project의 모든 System을 DeviceNode로 변환
    let buildDeviceNodes (store: DsStore) (projectId: Guid) : DeviceNode list =
        let activeSystems = DsQuery.activeSystemsOf projectId store
        let passiveSystems = DsQuery.passiveSystemsOf projectId store
        let allSystems = activeSystems @ passiveSystems

        // System별 Call 사용 횟수 계산 (DevicesAlias → System Name 매핑)
        let allFlows = DsQuery.allFlows store
        let allCallDeviceAliases =
            allFlows
            |> List.collect (fun (f: Flow) ->
                DsQuery.worksOf f.Id store
                |> List.collect (fun (w: Work) ->
                    DsQuery.callsOf w.Id store
                    |> List.map (fun (c: Call) -> c.DevicesAlias)))

        let deviceUsageCount =
            allCallDeviceAliases
            |> List.countBy id
            |> Map.ofList

        // Flow Name 추론: System에 속한 Flow 중 첫 번째
        let systemFlowMap =
            allSystems
            |> List.map (fun (sys: DsSystem) ->
                let flows = DsQuery.flowsOf sys.Id store
                let flowName =
                    flows |> List.tryHead |> Option.map (fun (f: Flow) -> f.Name) |> Option.defaultValue ""
                sys.Id, flowName)
            |> Map.ofList

        allSystems
        |> List.map (fun (sys: DsSystem) ->
            let sysType = sys.Properties.SystemType
            let apiDefs = DsQuery.apiDefsOf sys.Id store

            let apiDefNodes =
                apiDefs
                |> List.map (fun (ad: ApiDef) ->
                    // CallerCount: ApiDefId 참조하는 ApiCall 수
                    let callerCount =
                        DsQuery.allApiCalls store
                        |> List.filter (fun (ac: ApiCall) ->
                            ac.ApiDefId = Some ad.Id)
                        |> List.length
                    {
                        ApiDefNode.Id = ad.Id
                        Name = ad.Name
                        CallerCount = callerCount
                        State = Status4.Ready
                    })

            let flowName =
                systemFlowMap
                |> Map.tryFind sys.Id
                |> Option.defaultValue ""

            let isUsed =
                deviceUsageCount
                |> Map.containsKey sys.Name

            {
                DeviceNode.Id = sys.Id
                Name = sys.Name
                DeviceType = DeviceClassifier.classify sys.Name sysType
                FlowName = flowName
                SystemType = sysType
                State = Status4.Ready
                ApiDefs = apiDefNodes
                IsUsedInSimulation = isUsed
                Position = None
            })

    // ─────────────────────────────────────────────────────────────────────
    // FlowZone 빌드
    // ─────────────────────────────────────────────────────────────────────

    /// 배치된 Device 위치에서 FlowZone 생성
    let buildFlowZones (devices: DeviceNode list) (positions: LayoutPosition list) : FlowZone list =
        LayoutAlgorithm.computeFlowZones devices positions
