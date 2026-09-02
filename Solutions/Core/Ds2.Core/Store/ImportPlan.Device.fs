namespace Ds2.Core.Store

open System
open System.Collections.Generic
open Ds2.Core

module internal ImportPlanDeviceOps =

    type internal WiringMode =
        | Chain
        | AllPairs
        | NoneMode

    type private DeviceBatchState = {
        PendingSystems: Map<string, DsSystem>
        PendingFlows: Map<string, Flow>
        PendingWorks: Map<string * Guid, Work>
        PendingApiDefs: Map<string * Guid, ApiDef>
        NewSystemIds: Set<Guid>
        PendingWorkOrderRev: Map<string, Work list>
        PlannedArrowPairs: Set<Guid * Guid>
    }

    let private initialState = {
        PendingSystems = Map.empty
        PendingFlows = Map.empty
        PendingWorks = Map.empty
        PendingApiDefs = Map.empty
        NewSystemIds = Set.empty
        PendingWorkOrderRev = Map.empty
        PlannedArrowPairs = Set.empty
    }

    /// 단일 API device 에 자동 생성되는 완료 더미 Work 이름.
    let [<Literal>] internal doneWorkName = "DONE"

    /// 센서를 생략한 API 의 감지 지연(ms) — SensingType.Virtual 기본값(ApiDef 편집 다이얼로그와 동일).
    let [<Literal>] internal sensorlessSensingMs = 200

    let hasCreatableApiName (callName: string) =
        // M2: splitApiCallName(canonical, isNull/no-dot→None) 위임 — null/no-dot → false 정책 보존.
        Queries.splitApiCallName callName |> Option.exists (snd >> String.IsNullOrEmpty >> not)

    let private queueOperation operation (operations: ResizeArray<ImportPlanOperation>) =
        operations.Add(operation)

    let private ensureSystem
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (devAlias: string)
        (systemNameHint: string option)
        (systemType: string option)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let systemName = systemNameHint |> Option.defaultWith (fun () -> $"{flowName}_{devAlias}")
        match Map.tryFind systemName state.PendingSystems with
        | Some system -> system, systemName, state
        | None ->
            // §6: PendingSystems 는 항상 systemName 키로만 add(아래 ensureSystem 분기) → bare devAlias 캐시조회는 미적중 dead branch 라 제거. store passive 조회는 passiveMatch 에서 별도 수행.
            let passiveSystems = Queries.passiveSystemsOf projectId store
            let passiveMatch =
                if systemNameHint.IsNone then
                    passiveSystems |> List.tryFind (fun s -> s.Name = devAlias)
                    |> Option.orElseWith (fun () -> passiveSystems |> List.tryFind (fun s -> s.Name = systemName))
                else
                    passiveSystems |> List.tryFind (fun s -> s.Name = systemName)
            match passiveMatch with
            | Some existing ->
                match Queries.flowsOf existing.Id store with
                | flow :: _ ->
                    let existingWorks = Queries.worksOf flow.Id store
                    let existingWorkOrder =
                        Map.tryFind systemName state.PendingWorkOrderRev
                        |> Option.defaultValue []
                        |> List.append existingWorks
                    let existingPendingWorks =
                        existingWorks
                        |> List.fold (fun acc work -> Map.add (work.Name, existing.Id) work acc) state.PendingWorks
                    existing, systemName,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add systemName flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds
                        PendingWorkOrderRev = Map.add systemName existingWorkOrder state.PendingWorkOrderRev
                        PendingWorks = existingPendingWorks }
                | [] ->
                    let flow = Flow($"{devAlias}_Flow", existing.Id)
                    queueOperation (AddFlow flow) operations
                    existing, systemName,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add systemName flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds }
            | None ->
                let system = DsSystem(systemName)
                system.SystemType <- systemType
                let flow = Flow($"{devAlias}_Flow", system.Id)
                queueOperation (AddSystem system) operations
                queueOperation (LinkSystemToProject(projectId, system.Id, false)) operations
                queueOperation (AddFlow flow) operations
                system, systemName,
                { state with
                    PendingSystems = Map.add systemName system state.PendingSystems
                    PendingFlows = Map.add systemName flow state.PendingFlows
                    NewSystemIds = Set.add system.Id state.NewSystemIds }

    let private ensurePendingWork
        (deviceKey: string)
        (apiName: string)
        (systemId: Guid)
        (workDuration: TimeSpan option)
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let key = (apiName, systemId)
        if not (Set.contains systemId state.NewSystemIds) || Map.containsKey key state.PendingWorks then
            state
        else
            let flow = Map.find deviceKey state.PendingFlows
            let work =
                Queries.worksOf flow.Id store
                |> List.tryFind (fun existing -> existing.LocalName = apiName)
                |> Option.defaultWith (fun () ->
                    let created = Work(flow.Name, apiName, flow.Id)
                    created.Duration <- workDuration
                    queueOperation (AddWork created) operations
                    created)
            let current = Map.tryFind deviceKey state.PendingWorkOrderRev |> Option.defaultValue []
            { state with
                PendingWorks = Map.add key work state.PendingWorks
                PendingWorkOrderRev = Map.add deviceKey (work :: current) state.PendingWorkOrderRev }

    let private ensureApiDef
        (store: DsStore)
        (system: DsSystem)
        (apiName: string)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let key = (apiName, system.Id)
        match Map.tryFind key state.PendingApiDefs with
        | Some apiDef -> apiDef, state
        | None ->
            match Queries.apiDefsOf system.Id store |> List.tryFind (fun existing -> existing.Name = apiName) with
            | Some existing ->
                existing, { state with PendingApiDefs = Map.add key existing state.PendingApiDefs }
            | None ->
                let apiDef = ApiDef(apiName, system.Id)
                // PendingWorks에서 매칭되는 Work가 있으면 연결 설정
                match Map.tryFind key state.PendingWorks with
                | Some work ->
                    // Work가 있으면 기본 조합 (Normal None) 으로 설정
                    apiDef.ActionType <- ActionType.Normal None
                    apiDef.SensingType <- SensingType.Normal None
                    apiDef.TxGuid <- Some work.Id
                    apiDef.RxGuid <- Some work.Id
                | None ->
                    // Work가 없으면 기본값 유지
                    ()
                queueOperation (AddApiDef apiDef) operations
                apiDef, { state with PendingApiDefs = Map.add key apiDef state.PendingApiDefs }

    let private createAndRegisterApiCall
        (call: Call)
        (name: string)
        (apiDefId: Guid)
        (operations: ResizeArray<ImportPlanOperation>) =
        let apiCall = ApiCall(name)
        apiCall.ApiDefId <- Some apiDefId
        call.ApiCalls.Add(apiCall)
        queueOperation (AddApiCall apiCall) operations

    let private buildWorkArrowsBy
        (pairsOf: Work list -> (Work * Work) list)
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        state.PendingWorkOrderRev
        |> Map.fold (fun currentState deviceKey workOrderRev ->
            match Map.tryFind deviceKey currentState.PendingFlows with
            | None -> currentState
            | Some flow ->
                let systemId = flow.ParentId
                let existingArrows = Queries.arrowWorksOf systemId store
                let pairs = workOrderRev |> List.rev |> pairsOf
                let nextPairs =
                    pairs
                    |> List.fold (fun acc (src, dst) ->
                        let pair = (src.Id, dst.Id)
                        let alreadyExists =
                            Set.contains pair acc
                            || existingArrows |> List.exists (fun arrow ->
                                arrow.ArrowType = ArrowType.ResetReset
                                && ((arrow.SourceId = src.Id && arrow.TargetId = dst.Id)
                                    || (arrow.SourceId = dst.Id && arrow.TargetId = src.Id)))
                        if alreadyExists then
                            acc
                        else
                            let arrow = ArrowBetweenWorks(systemId, src.Id, dst.Id, ArrowType.ResetReset)
                            queueOperation (AddArrowWork arrow) operations
                            Set.add pair acc
                    ) currentState.PlannedArrowPairs
                { currentState with PlannedArrowPairs = nextPairs }) state
        |> ignore

    let private buildWorkArrows store operations state =
        buildWorkArrowsBy List.pairwise store operations state

    let private buildWorkArrowsAllPairs store operations state =
        let allPairs (ws: Work list) =
            [ for i in 0 .. ws.Length - 1 do
                for j in i + 1 .. ws.Length - 1 do
                    yield ws.[i], ws.[j] ]
        buildWorkArrowsBy allPairs store operations state

    /// 같은 Device(Call alias) 에 속한 Passive System 들은 그 device 의 **전체 API 집합**을 공유한다.
    /// CSV 에 특정 System 의 특정 API 행이 없는 것은 "그 동작이 없다"가 아니라 "센서를 생략했다"는 뜻이다.
    /// (솔레노이드 1개가 실린더 N개를 구동하지만 전진 센서는 1번 실린더에만 달린 실설비 패턴)
    /// 누락된 API 의 Work/ApiDef 를 채워 넣어 ADV↔RET 상호 리셋이 성립하게 한다.
    /// ApiCall 은 만들지 않는다 — 입력에 없는 행을 지어내지 않기 위함(센서 없는 동작은 미배선 상태로 남음).
    let private completeDeviceApiSets
        (store: DsStore)
        (callsByFlow: (string * (Call * string * string option) list) list)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let workDurationDefault = Some (TimeSpan.FromMilliseconds 500.)
        callsByFlow
        |> List.fold (fun flowState (flowName, calls) ->
            // alias 별 API 집합 / deviceKey 집합 / (alias,api) → Call 수집
            let mutable apisByAlias = Map.empty<string, Set<string>>
            let mutable keysByAlias = Map.empty<string, Set<string>>
            let mutable callByAliasApi = Map.empty<string * string, Call>
            for (call, _, sysHint) in calls do
                let apiName = call.ApiName
                if not (String.IsNullOrEmpty apiName) then
                    let alias = call.DevicesAlias
                    let deviceKey = sysHint |> Option.defaultWith (fun () -> $"{flowName}_{alias}")
                    let apis = defaultArg (Map.tryFind alias apisByAlias) Set.empty
                    apisByAlias <- Map.add alias (Set.add apiName apis) apisByAlias
                    let keys = defaultArg (Map.tryFind alias keysByAlias) Set.empty
                    keysByAlias <- Map.add alias (Set.add deviceKey keys) keysByAlias
                    if not (Map.containsKey (alias, apiName) callByAliasApi) then
                        callByAliasApi <- Map.add (alias, apiName) call callByAliasApi

            apisByAlias
            |> Map.fold (fun aliasState alias apiNames ->
                defaultArg (Map.tryFind alias keysByAlias) Set.empty
                |> Set.fold (fun keyState deviceKey ->
                    match Map.tryFind deviceKey keyState.PendingSystems with
                    | None -> keyState
                    | Some system ->
                        apiNames
                        |> Set.fold (fun apiState apiName ->
                            if Map.containsKey (apiName, system.Id) apiState.PendingWorks then apiState
                            else
                                let withWork =
                                    ensurePendingWork deviceKey apiName system.Id workDurationDefault store operations apiState
                                // 신규 device 가 아니면 ensurePendingWork 가 state 를 그대로 반환 → 건너뛴다.
                                if not (Map.containsKey (apiName, system.Id) withWork.PendingWorks) then withWork
                                else
                                    let apiDef, withApiDef = ensureApiDef store system apiName operations withWork
                                    // 센서가 없으므로 출력 시점 + T(ms) 후 자동 완료로 정의한다.
                                    // (V2 검증: SensingType=Virtual 이면 InTag 불필요)
                                    apiDef.SensingType <- SensingType.Virtual sensorlessSensingMs
                                    // ApiCall 을 만들지 않으면 Active 가 이 Work 를 구동할 수 없어
                                    // Passive 가 반대 상태로 고착된다(데드락). 반드시 연결한다.
                                    match Map.tryFind (alias, apiName) callByAliasApi with
                                    | Some call -> createAndRegisterApiCall call $"{system.Name}.{apiName}" apiDef.Id operations
                                    | None -> ()
                                    withApiDef
                        ) keyState
                ) aliasState
            ) flowState
        ) state

    /// 단일 API device 보정 — API Work 가 1개뿐인 신규 passive device flow 에 'DONE' 더미 Work 를 추가한다.
    /// 이유: 상호 리셋 파트너가 없는 1-API device 는 1회 동작 후 Finish 로 고착되어 재기동이 불가하다.
    /// 배선: (API -Start-> DONE) + (API <-ResetReset-> DONE). DONE 은 Call 이 없어 시작 즉시 Finish 하고
    /// (Execution.fs: callGuids.IsEmpty -> Finish), 그 결과 API Work 를 리셋해 다음 사이클을 준비한다.
    ///
    /// 화살표 2개를 StartReset 하나로 합치지 말 것 — 합치면 재기동에 별도 API 가 필요해져
    /// "API 1개로 Work 동작을 반복" 하는 이 패턴의 목적이 깨진다. Start(완료→DONE 기동)와
    /// ResetReset(상호 리셋으로 재무장)은 역할이 다르며, 둘이 함께 있어야 단일 API 가 반복 동작한다.
    /// 시뮬레이션에서 API Work 가 여러 번 순환하는 것은 이 재무장 동작이며 정상이다(수렴함).
    /// ApiDef 는 Tx = API Work, Rx = DONE 으로 잡아 '동작 송신 ~ 완료 수신' 을 분리한다.
    let private buildSingleApiDoneWorks
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        state.PendingWorkOrderRev
        |> Map.iter (fun deviceKey workOrderRev ->
            match workOrderRev with
            | [ apiWork ] when apiWork.LocalName <> doneWorkName ->
                match Map.tryFind deviceKey state.PendingFlows with
                | None -> ()
                | Some flow ->
                    let alreadyHasDone =
                        Queries.worksOf flow.Id store
                        |> List.exists (fun existing -> existing.LocalName = doneWorkName)
                    if not alreadyHasDone then
                        let systemId = flow.ParentId
                        // Duration 미지정 = 즉시 완료되는 더미(순수 상태 전이용).
                        // ApiDef.Rx 가 DONE 을 가리키므로 이 device 를 부르는 Call 의 device duration
                        // (Queries.callDeviceDurationMs = Rx Work.Duration)은 0ms 가 된다.
                        // 피드백 센서가 없는 출력·감지 전용 device 의 실제 거동에 맞춘 의도된 값이므로
                        // 여기에 Duration 을 부여하지 말 것(부여 시 완료 지연 + 재기동 지연).
                        let doneWork = Work(flow.Name, doneWorkName, flow.Id)
                        queueOperation (AddWork doneWork) operations
                        queueOperation
                            (AddArrowWork (ArrowBetweenWorks(systemId, apiWork.Id, doneWork.Id, ArrowType.Start)))
                            operations
                        queueOperation
                            (AddArrowWork (ArrowBetweenWorks(systemId, apiWork.Id, doneWork.Id, ArrowType.ResetReset)))
                            operations
                        // 이 device 의 신규 ApiDef(Tx = API Work) 만 Rx 를 DONE 으로 재지정.
                        // 기존 store ApiDef 는 Tx 가 다른 Work 를 가리키므로 영향 없음(store 비변경 계약 유지).
                        state.PendingApiDefs
                        |> Map.iter (fun (_, apiDefSystemId) apiDef ->
                            if apiDefSystemId = systemId && apiDef.TxGuid = Some apiWork.Id then
                                apiDef.RxGuid <- Some doneWork.Id)
            | _ -> ())

    let private linkCallsToDevicesWithState
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (calls: (Call * string * string option) list)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        if calls.IsEmpty then state
        else
            calls
            |> List.fold (fun st (call, callName, sysHint) ->
                let apiName = call.ApiName
                if String.IsNullOrEmpty apiName then
                    st
                else
                    let devAlias = call.DevicesAlias
                    let system, deviceKey, withSystem = ensureSystem store projectId flowName devAlias sysHint None operations st
                    let workDurationDefault = Some (TimeSpan.FromMilliseconds 500.)
                    let withWork = ensurePendingWork deviceKey apiName system.Id workDurationDefault store operations withSystem
                    let apiDef, withApiDef = ensureApiDef store system apiName operations withWork
                    createAndRegisterApiCall call callName apiDef.Id operations
                    withApiDef
            ) state

    /// LLM helper 진입점 — PassiveSystem + Flow + Work×N + ApiDef×N (+ optional ResetReset Arrow) cascade 1회 발행.
    /// 반환 = (PassiveSystem.Id, (apiName * ApiDef.Id) list).
    /// **반환 list 의 순서는 입력 `apiNames` 순서를 그대로 보존** — caller (LlmAgent) 가 `apiDef*Ref` /
    /// `apiDefRefs` 의 입력 순서와 zip 하여 batch ref table 에 다중 등록한다. 순서 파괴 시 ref 가
    /// 다른 ApiDef 를 가리키는 silent miscompile 가능 → 본 보장은 contract.
    /// helper 는 *신규* device 생성 책임만 짐 — 동명 PassiveSystem 이 store 에 이미 존재하면 invalidOp.
    /// 기존 device 재사용 시나리오는 LLM 이 사전에 find_by_name/export_model_doc 로 조회 후 primitive add_call 사용.
    let internal buildPassiveDeviceCascade
        (store: DsStore)
        (projectId: Guid)
        (operations: ResizeArray<ImportPlanOperation>)
        (name: string)
        (deviceType: string)
        (apiNames: string list)
        (workDuration: TimeSpan option)
        (wiringMode: WiringMode)
        : Guid * (string * Guid) list =
        // D9 정책 (rev 12 신설) — passive-only 검사. LlmAgent 측 진입 경로 (`ToolOperations.runDeviceCascade`) 는
        // active+passive 통합 sibling guard (`hasSystemNameClashInProject`) 로 한 단계 앞서 fail 처리하므로 본 분기는
        // LlmAgent 호출 시 unreachable. 본 검사는 Editor/Mermaid/CSV import 등 LlmAgent 외 호출자 보호용으로 유지.
        let existing =
            Queries.passiveSystemsOf projectId store
            |> List.tryFind (fun s -> s.Name = name)
        match existing with
        | Some _ ->
            invalidOp $"PassiveSystem '{name}' 이 이미 존재합니다 — find_by_name/export_model_doc 로 사전 조회 후 primitive add_call 로 기존 ApiDef.Id 참조 권장 (helper 는 신규 device 생성 책임)"
        | None -> ()
        let system, deviceKey, stateWithSystem =
            ensureSystem store projectId name name (Some name) (Some deviceType) operations initialState
        let stateWithWorks =
            apiNames
            |> List.fold (fun st apiName ->
                ensurePendingWork deviceKey apiName system.Id workDuration store operations st
            ) stateWithSystem
        let apiDefIds, stateWithApiDefs =
            apiNames
            |> List.fold (fun (acc, st) apiName ->
                let apiDef, st' = ensureApiDef store system apiName operations st
                ((apiName, apiDef.Id) :: acc, st')
            ) ([], stateWithWorks)
        let apiDefIdsOrdered = List.rev apiDefIds
        match wiringMode with
        | Chain ->
            buildWorkArrows store operations stateWithApiDefs
            buildSingleApiDoneWorks store operations stateWithApiDefs
        | AllPairs ->
            buildWorkArrowsAllPairs store operations stateWithApiDefs
            buildSingleApiDoneWorks store operations stateWithApiDefs
        | NoneMode -> ()
        system.Id, apiDefIdsOrdered

    let linkCallsToDevices
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (calls: (Call * string) list)
        (operations: ResizeArray<ImportPlanOperation>) =
        if not calls.IsEmpty then
            let withHint = calls |> List.map (fun (c, n) -> c, n, None)
            let finalState = linkCallsToDevicesWithState store projectId flowName withHint operations initialState
            let completedState = completeDeviceApiSets store [ flowName, withHint ] operations finalState
            buildWorkArrows store operations completedState
            buildSingleApiDoneWorks store operations completedState

    /// 여러 Flow의 Call을 state 공유하며 처리. systemNameHint가 있으면 System 이름으로 사용.
    let linkCallsToDevicesMultiFlow
        (store: DsStore)
        (projectId: Guid)
        (callsByFlow: (string * (Call * string * string option) list) list)
        (operations: ResizeArray<ImportPlanOperation>) =
        let finalState =
            callsByFlow
            |> List.fold (fun st (flowName, calls) ->
                linkCallsToDevicesWithState store projectId flowName calls operations st
            ) initialState
        let completedState = completeDeviceApiSets store callsByFlow operations finalState
        buildWorkArrows store operations completedState
        buildSingleApiDoneWorks store operations completedState
