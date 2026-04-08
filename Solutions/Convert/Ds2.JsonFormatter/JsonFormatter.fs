namespace Ds2.JsonFormatter

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Ds2.Core
open Ds2.Core.Store

/// Ev2/외부 시스템에서 Ds2 JSON 파일을 생성하기 위한 헬퍼 모듈
///
/// 사용 예시:
///   let store = Builder.createStore "MyProject" "MySystem" "MyFlow"
///   Builder.addWork store flowId "Work1" (Some (TimeSpan.FromMilliseconds 300.)) TokenRole.Source
///   Builder.addArrowWork store systemId w1 w2 ArrowType.Start
///   Exporter.saveCompact store "output.json"
[<RequireQualifiedAccess>]
module Builder =

    /// 빈 Store에 Project + Active System + Flow를 생성하고 (store, projectId, systemId, flowId) 반환
    let createStore (projectName: string) (systemName: string) (flowName: string) =
        let store = DsStore.empty()

        let project = Project(projectName)
        let system = DsSystem(systemName)
        let sysProps = SimulationSystemProperties()
        sysProps.SystemType <- Some "Unit"
        system.SetSimulationProperties(sysProps)
        let flow = Flow(flowName, system.Id)

        store.Projects.Add(project.Id, project)
        store.Systems.Add(system.Id, system)
        store.Flows.Add(flow.Id, flow)
        project.ActiveSystemIds.Add(system.Id)

        (store, project.Id, system.Id, flow.Id)

    /// Work 추가. duration: Some (TimeSpan) | None, tokenRole: TokenRole enum
    let addWork (store: DsStore) (flowId: Guid) (localName: string) (duration: TimeSpan option) (tokenRole: TokenRole) =
        let flow =
            match store.Flows.TryGetValue(flowId) with
            | true, f -> f
            | _ -> invalidOp $"Flow {flowId} not found"
        let work = Work(flow.Name, localName, flowId)
        work.Duration <- duration
        work.TokenRole <- tokenRole
        store.Works.Add(work.Id, work)
        work.Id

    /// Passive System(Device) 추가. 반환: (systemId, flowId, workId, apiDefId)
    let addDevice (store: DsStore) (projectId: Guid) (deviceName: string) (apiName: string) =
        let project =
            match store.Projects.TryGetValue(projectId) with
            | true, p -> p
            | _ -> invalidOp $"Project {projectId} not found"

        let system = DsSystem(deviceName)
        let sysProps2 = SimulationSystemProperties()
        sysProps2.SystemType <- Some "Unit"
        system.SetSimulationProperties(sysProps2)
        store.Systems.Add(system.Id, system)
        project.PassiveSystemIds.Add(system.Id)

        let flow = Flow(deviceName, system.Id)
        store.Flows.Add(flow.Id, flow)

        let work = Work(deviceName, apiName, flow.Id)
        store.Works.Add(work.Id, work)

        let apiDef = ApiDef(apiName, system.Id)
        apiDef.TxGuid <- Some work.Id
        apiDef.RxGuid <- Some work.Id
        store.ApiDefs.Add(apiDef.Id, apiDef)

        (system.Id, flow.Id, work.Id, apiDef.Id)

    /// Call 추가 (Device와 연결). 반환: (callId, apiCallId)
    let addCall (store: DsStore) (workId: Guid) (devicesAlias: string) (apiName: string) (apiDefId: Guid) =
        let call = Call(devicesAlias, apiName, workId)
        let apiCall = ApiCall($"{devicesAlias}.{apiName}")
        apiCall.ApiDefId <- Some apiDefId
        call.ApiCalls.Add(apiCall)

        store.Calls.Add(call.Id, call)
        store.ApiCalls.Add(apiCall.Id, apiCall)

        (call.Id, apiCall.Id)

    /// ArrowBetweenWorks 추가 (systemId가 parent)
    let addArrowWork (store: DsStore) (systemId: Guid) (sourceWorkId: Guid) (targetWorkId: Guid) (arrowType: ArrowType) =
        let arrow = ArrowBetweenWorks(systemId, sourceWorkId, targetWorkId, arrowType)
        store.ArrowWorks.Add(arrow.Id, arrow)
        arrow.Id

    /// ArrowBetweenCalls 추가 (workId가 parent)
    let addArrowCall (store: DsStore) (workId: Guid) (sourceCallId: Guid) (targetCallId: Guid) (arrowType: ArrowType) =
        let arrow = ArrowBetweenCalls(workId, sourceCallId, targetCallId, arrowType)
        store.ArrowCalls.Add(arrow.Id, arrow)
        arrow.Id

    /// IOTag 설정
    let setApiCallIOTags (store: DsStore) (apiCallId: Guid) (outTag: IOTag option) (inTag: IOTag option) =
        match store.ApiCalls.TryGetValue(apiCallId) with
        | true, ac ->
            ac.OutTag <- outTag
            ac.InTag <- inTag
        | _ -> invalidOp $"ApiCall {apiCallId} not found"

    /// CallCondition 추가. 반환: conditionId
    let addCondition (store: DsStore) (callId: Guid) (conditionType: CallConditionType) (apiCallIds: Guid list) (isOR: bool) =
        let call =
            match store.Calls.TryGetValue(callId) with
            | true, c -> c
            | _ -> invalidOp $"Call {callId} not found"
        let condition = CallCondition()
        condition.Type <- Some conditionType
        condition.IsOR <- isOR
        for apiCallId in apiCallIds do
            match store.ApiCalls.TryGetValue(apiCallId) with
            | true, ac -> condition.Conditions.Add(ac)
            | _ -> invalidOp $"ApiCall {apiCallId} not found"
        call.CallConditions.Add(condition)
        condition.Id

    /// TokenSpec 추가
    let addTokenSpec (store: DsStore) (id: int) (label: string) (workId: Guid option) =
        let project = store.Projects.Values |> Seq.head
        project.TokenSpecs.Add({ Id = id; Label = label; Fields = Map.empty; WorkId = workId })


/// JSON 파일 내보내기/가져오기
[<RequireQualifiedAccess>]
module Exporter =

    /// DsStore를 JSON 파일로 저장 (Ds2 규격)
    let save (store: DsStore) (path: string) =
        store.SaveToFile(path)

    /// JSON 파일에서 DsStore 로드
    let load (path: string) =
        let store = DsStore.empty()
        store.LoadFromFile(path)
        store

    /// Store를 생성하고 바로 저장하는 편의 함수
    let saveNew (projectName: string) (systemName: string) (flowName: string) (path: string) (configure: DsStore -> Guid -> Guid -> Guid -> unit) =
        let (store, projectId, systemId, flowId) = Builder.createStore projectName systemName flowName
        configure store projectId systemId flowId
        store.SaveToFile(path)
        store
