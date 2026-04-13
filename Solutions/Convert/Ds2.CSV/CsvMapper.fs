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

    let private addBucketItem
        (buckets: Dictionary<string, ResizeArray<Call * string * string option * string option * string option * string option>>)
        (flowName: string)
        (call: Call)
        (callLabel: string)
        (inName: string option)
        (inAddress: string option)
        (outName: string option)
        (outAddress: string option) =
        let bucket =
            match buckets.TryGetValue(flowName) with
            | true, existing -> existing
            | false, _ ->
                let created = ResizeArray()
                buckets.[flowName] <- created
                created
        bucket.Add(call, callLabel, inName, inAddress, outName, outAddress)

    let mapToSystemPlan (store: DsStore) (projectId: Guid) (systemId: Guid) (document: CsvDocument) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let flows = Dictionary<string, Flow>()
        let works = Dictionary<Guid * string, Work>()
        let calls = Dictionary<Guid * string * string * string, Call>()
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option * string option * string option * string option>>()
        let mutable entryByCall = Map.empty<Guid, CsvEntry>
        // 같은 Call에 매핑된 entry IO 정보를 순서대로 추적
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
                    entryByCall <- Map.add created.Id entry entryByCall
                    let callLabel = $"{created.DevicesAlias}.{created.ApiName}"
                    addBucketItem callsByFlow entry.FlowName created callLabel entry.InName entry.InAddress entry.OutName entry.OutAddress
                    created

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
                let linkedCalls =
                    flowCalls |> Seq.map (fun (call, label, _, _, _, _) ->
                        let sysHint = entryByCall |> Map.tryFind call.Id |> Option.map (fun e -> e.SystemName)
                        call, label, sysHint) |> Seq.toList
                yield flowName, linkedCalls ]
        ImportPlanDeviceOps.linkCallsToDevicesMultiFlow store projectId allFlowCalls operations

        for KeyValue(callId, ioEntries) in ioEntriesByCall do
            let call = calls.Values |> Seq.find (fun c -> c.Id = callId)
            for i in 0 .. ioEntries.Count - 1 do
                if i < call.ApiCalls.Count then
                    let inName, inAddress, outName, outAddress = ioEntries.[i]
                    if inName.IsSome || inAddress.IsSome || outName.IsSome || outAddress.IsSome then
                        let apiCall = call.ApiCalls.[i]
                        match inName, inAddress with
                        | Some name, Some address -> apiCall.InTag <- Some(IOTag(name, address, ""))
                        | None, Some address -> apiCall.InTag <- Some(IOTag("In", address, ""))
                        | Some _, None -> ()
                        | None, None -> ()

                        match outName, outAddress with
                        | Some name, Some address -> apiCall.OutTag <- Some(IOTag(name, address, ""))
                        | None, Some address -> apiCall.OutTag <- Some(IOTag("Out", address, ""))
                        | Some _, None -> ()
                        | None, None -> ()

        ImportPlan.ofSeq operations
