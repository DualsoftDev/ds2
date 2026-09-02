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
    let private addBucketItem
        (buckets: Dictionary<string, ResizeArray<Call * string * string option>>)
        (flowName: string)
        (call: Call)
        (apiCallLabel: string)
        (systemHint: string option) =
        let bucket =
            match buckets.TryGetValue(flowName) with
            | true, existing -> existing
            | false, _ ->
                let created = ResizeArray()
                buckets.[flowName] <- created
                created
        bucket.Add(call, apiCallLabel, systemHint)

    let mapToSystemPlan (store: DsStore) (projectId: Guid) (systemId: Guid) (document: CsvDocument) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let flows = Dictionary<string, Flow>()
        let works = Dictionary<Guid * string, Work>()
        let calls = Dictionary<Guid * string * string * string, Call>()
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option>>()
        // 같은 Call에 매핑된 entry IO 정보를 행 순서대로 추적 — ApiCall 생성 순서와 1:1 대응
        let ioEntriesByCall = Dictionary<Guid, ResizeArray<string option * string option * string option * string option>>()

        for entry in document.Entries do
            let flow =
                match flows.TryGetValue(entry.FlowName) with
                | true, existing -> existing
                | false, _ ->
                    let created = Flow(entry.FlowName, systemId)
                    operations.Add(AddFlow created)
                    flows.[entry.FlowName] <- created
                    created

            let workKey = (flow.Id, entry.WorkName)
            let work =
                match works.TryGetValue(workKey) with
                | true, existing -> existing
                | false, _ ->
                    let created = Work(flow.Name, entry.WorkName, flow.Id)
                    operations.Add(AddWork created)
                    works.[workKey] <- created
                    created

            let callKey = (flow.Id, entry.WorkName, entry.DeviceAlias, entry.ApiName)
            let call =
                match calls.TryGetValue(callKey) with
                | true, existing -> existing
                | false, _ ->
                    let created = Call(entry.DeviceAlias, entry.ApiName, work.Id)
                    operations.Add(AddCall created)
                    calls.[callKey] <- created
                    created

            // 행마다 등록 — System 열이 다르면 그 수만큼 Passive System/ApiDef/ApiCall 이 만들어진다.
            addBucketItem callsByFlow entry.FlowName call $"{entry.SystemName}.{entry.ApiName}" (Some entry.SystemName)

            let ioEntries =
                match ioEntriesByCall.TryGetValue(call.Id) with
                | true, existing -> existing
                | false, _ ->
                    let created = ResizeArray()
                    ioEntriesByCall.[call.Id] <- created
                    created
            ioEntries.Add(entry.InName, entry.InAddress, entry.OutName, entry.OutAddress)

        let allFlowCalls =
            [ for KeyValue(flowName, flowCalls) in callsByFlow do
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

        ImportPlan.ofSeq operations
