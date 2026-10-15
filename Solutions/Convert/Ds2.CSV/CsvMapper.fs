namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store


module internal CsvMapper =

    let tryResolveProjectId (store: DsStore) (systemId: Guid) =
        Queries.allProjects store
        |> List.tryFind (fun project ->
            project.ActiveSystemIds.Contains(systemId) ||
            project.PassiveSystemIds.Contains(systemId))
        |> Option.map (fun project -> project.Id)

    /// Flow 별 디바이스 캐스케이드 입력에 (Call, ApiCall 이름, Passive System 이름) 1건 등록.
    /// **CSV 한 행 = 버킷 1건** 이므로 같은 Call 이라도 System 열이 다르면 행마다
    /// 별도 Passive System/ApiDef/ApiCall 이 생성된다.
    /// (실설비 패턴: 솔레노이드 1개가 LATCH1~5 를 구동하고 실린더마다 개별 센서가 달린 경우)
    /// 버킷은 Flow 이름이 아니라 Flow 개체 단위다 — 이름으로 묶으면 다른 Active System 의 같은 이름 Flow 가
    /// 한 버킷이 되어, 한쪽 디바이스에만 있는 API 를 다른 System 의 Call 에 채워 넣었다.
    let private addBucketItem
        (buckets: Dictionary<Guid, string * ResizeArray<Call * string * string option>>)
        (flow: Flow)
        (call: Call)
        (apiCallLabel: string)
        (systemHint: string option) =
        let _, bucket =
            match buckets.TryGetValue(flow.Id) with
            | true, existing -> existing
            | false, _ ->
                let created = flow.Name, ResizeArray()
                buckets.[flow.Id] <- created
                created
        bucket.Add(call, apiCallLabel, systemHint)

    let private linePrefix (entry: CsvEntry) =
        match entry.SourceLines with
        | line :: _ -> $"line {line}: "
        | [] -> ""

    /// defaultSystem = ActiveSystem 열이 빈 행이 들어갈 System(가져오기 대화상자 이름 또는 지정한 System).
    /// store 에 아직 없으면 처음 쓰일 때 만든다 — 모든 행이 ActiveSystem 을 적었는데 미리 만들어 두면
    /// 빈 Active System 이 하나 더 생겨 내보냈다 다시 읽은 System 수가 어긋난다.
    /// ActiveSystem 이름이 프로젝트에 이미 있는 Active System 과 같으면 그 System 에 넣는다.
    let mapToSystemPlan (store: DsStore) (projectId: Guid) (defaultSystem: DsSystem) (document: CsvDocument) : Result<ImportPlan, string list> =
        let operations = ResizeArray<ImportPlanOperation>()
        let systems = Dictionary<string, DsSystem>()
        let flows = Dictionary<Guid * string, Flow>()
        let works = Dictionary<Guid * string, Work>()
        // (Active System, "Flow.Work") → Work. ReferenceOf 는 같은 Active System 안에서만 찾는다 —
        // 여러 System 에 같은 이름의 Flow·Work 가 있어도 다른 System 의 원본을 집지 않게.
        let worksByPath = Dictionary<Guid * string, Work>()
        let calls = Dictionary<Guid * string * string, Call>()
        let callsByFlow = Dictionary<Guid, string * ResizeArray<Call * string * string option>>()
        // 같은 Call에 매핑된 entry IO 정보를 행 순서대로 추적 — ApiCall 생성 순서와 1:1 대응
        let ioEntriesByCall = Dictionary<Guid, ResizeArray<string option * string option * string option * string option>>()
        let referenceEntries = ResizeArray<CsvEntry * DsSystem * Work>()

        let addActiveSystem (system: DsSystem) =
            operations.Add(AddSystem system)
            operations.Add(LinkSystemToProject(projectId, system.Id, true))

        let resolveSystem (name: string option) =
            let key = name |> Option.defaultValue defaultSystem.Name
            match systems.TryGetValue(key) with
            | true, existing -> existing
            | false, _ ->
                let system =
                    if key = defaultSystem.Name then
                        if not (store.Systems.ContainsKey defaultSystem.Id) then addActiveSystem defaultSystem
                        defaultSystem
                    else
                        match Queries.activeSystemsOf projectId store |> List.tryFind (fun s -> s.Name = key) with
                        | Some existing -> existing
                        | None ->
                            let created = DsSystem(key)
                            addActiveSystem created
                            created
                systems.[key] <- system
                system

        // 헤더만 있는 CSV 도 예전처럼 빈 System 하나는 만든다.
        if document.Entries.IsEmpty then resolveSystem None |> ignore

        for entry in document.Entries do
            let system = resolveSystem entry.ActiveSystemName

            let flowKey = (system.Id, entry.FlowName)
            let flow =
                match flows.TryGetValue(flowKey) with
                | true, existing -> existing
                | false, _ ->
                    let created = Flow(entry.FlowName, system.Id)
                    operations.Add(AddFlow created)
                    flows.[flowKey] <- created
                    created

            let workKey = (flow.Id, entry.WorkName)
            let work =
                match works.TryGetValue(workKey) with
                | true, existing -> existing
                | false, _ ->
                    let created = Work(flow.Name, entry.WorkName, flow.Id)
                    operations.Add(AddWork created)
                    works.[workKey] <- created
                    worksByPath.[(system.Id, $"{flow.Name}.{entry.WorkName}")] <- created
                    created

            match entry.ReferenceOf with
            | Some _ ->
                // 원본 행이 뒤에 나와도 찾을 수 있게 일반 Call 을 다 만든 뒤에 잇는다.
                referenceEntries.Add(entry, system, work)
            | None ->
                let callKey = (work.Id, entry.DeviceAlias, entry.ApiName)
                let call =
                    match calls.TryGetValue(callKey) with
                    | true, existing -> existing
                    | false, _ ->
                        let created = Call(entry.DeviceAlias, entry.ApiName, work.Id)
                        operations.Add(AddCall created)
                        calls.[callKey] <- created
                        created

                // 행마다 등록 — System 열이 다르면 그 수만큼 Passive System/ApiDef/ApiCall 이 만들어진다.
                addBucketItem callsByFlow flow call $"{entry.SystemName}.{entry.ApiName}" (Some entry.SystemName)

                let ioEntries =
                    match ioEntriesByCall.TryGetValue(call.Id) with
                    | true, existing -> existing
                    | false, _ ->
                        let created = ResizeArray()
                        ioEntriesByCall.[call.Id] <- created
                        created
                ioEntries.Add(entry.InName, entry.InAddress, entry.OutName, entry.OutAddress)

        // Reference 는 디바이스·ApiCall 을 만들지 않고 원본 Call 만 가리킨다.
        // 예전엔 Reference 행을 일반 행으로 읽어 유령 디바이스가 생기거나 원본의 두 번째 ApiCall 로 흡수됐다.
        let referenceErrors = ResizeArray<string>()
        for entry, system, work in referenceEntries do
            let target = entry.ReferenceOf.Value
            let original =
                match worksByPath.TryGetValue((system.Id, target)) with
                | true, originWork ->
                    match calls.TryGetValue((originWork.Id, entry.DeviceAlias, entry.ApiName)) with
                    | true, call -> Some call
                    | false, _ -> None
                | false, _ -> None
            match original with
            | Some original ->
                let reference = Call(entry.DeviceAlias, entry.ApiName, work.Id)
                reference.ReferenceOf <- Some original.Id
                operations.Add(AddCall reference)
            | None ->
                referenceErrors.Add(
                    $"{linePrefix entry}ReferenceOf '{target}' 에서 원본 Call '{entry.DeviceAlias}.{entry.ApiName}' 을 찾을 수 없습니다 "
                    + $"(Active System '{system.Name}' 안의 Flow.Work 이고, 그 Work 에 같은 Device·Api 의 일반 행이 있어야 합니다).")

        // 같은 이름의 Flow 가 여러 Active System 에 있으면 같은 이름이 두 번 들어간다 — 캐스케이드는 항목별로 처리한다.
        let allFlowCalls =
            [ for KeyValue(_, (flowName, flowCalls)) in callsByFlow do
                yield flowName, List.ofSeq flowCalls ]
        ImportPlanDeviceOps.linkCallsToDevicesMultiFlow store projectId allFlowCalls operations

        for KeyValue(callId, ioEntries) in ioEntriesByCall do
            let call = calls.Values |> Seq.find (fun c -> c.Id = callId)
            // 방어용 — 정상 입력은 행마다 ApiCall 이 생성되어 개수가 이미 일치한다.
            // ApiName 이 비어 캐스케이드가 건너뛴 경우 등에서만 부족분을 첫 ApiCall 기준으로 채운다.
            if ioEntries.Count > call.ApiCalls.Count && call.ApiCalls.Count > 0 then
                let template = call.ApiCalls.[0]
                for _ in call.ApiCalls.Count .. ioEntries.Count - 1 do
                    let extra = ApiCall(template.Name)
                    extra.ApiDefId <- template.ApiDefId
                    call.ApiCalls.Add(extra)
                    operations.Add(AddApiCall extra)
            for i in 0 .. ioEntries.Count - 1 do
                if i < call.ApiCalls.Count then
                    let inName, inAddress, outName, outAddress = ioEntries.[i]
                    if inName.IsSome || inAddress.IsSome || outName.IsSome || outAddress.IsSome then
                        let apiCall = call.ApiCalls.[i]
                        match inName, inAddress with
                        | Some name, Some address -> apiCall.InTag <- Some(IOTag(name, address, ""))
                        | None, Some address -> apiCall.InTag <- Some(IOTag("In", address, ""))
                        | Some name, None -> apiCall.InTag <- Some(IOTag(name, "", ""))
                        | None, None -> ()

                        match outName, outAddress with
                        | Some name, Some address -> apiCall.OutTag <- Some(IOTag(name, address, ""))
                        | None, Some address -> apiCall.OutTag <- Some(IOTag("Out", address, ""))
                        | Some name, None -> apiCall.OutTag <- Some(IOTag(name, "", ""))
                        | None, None -> ()

        // 센서를 생략한 API 는 CSV 행이 없어 캐스케이드가 만든 ApiCall 의 태그가 비어 있다.
        // 출력(솔레노이드)은 같은 Call 안에서 공유되므로 형제 ApiCall 의 OutTag 를 물려준다.
        // V1 검증(ActionType≠Virtual ⇒ OutTag 필수) 충족. InTag 는 SensingType=Virtual 이라 불필요.
        for call in calls.Values do
            match call.ApiCalls |> Seq.tryFind (fun apiCall -> apiCall.OutTag.IsSome) with
            | Some source ->
                let sourceTag = source.OutTag.Value
                for apiCall in call.ApiCalls do
                    if apiCall.OutTag.IsNone then
                        apiCall.OutTag <- Some(IOTag(sourceTag.Name, sourceTag.Address, sourceTag.Description))
            | None -> ()

        if referenceErrors.Count > 0 then Error (List.ofSeq referenceErrors)
        else Ok (ImportPlan.ofSeq operations)
