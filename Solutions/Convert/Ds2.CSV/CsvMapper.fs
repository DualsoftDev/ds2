namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store

module internal CsvMapper =

    let tryResolveProjectId (store: DsStore) (systemId: Guid) =
        DsQuery.allProjects store
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
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option * string option * string option * string option>>()

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

            let call = Call(entry.DeviceAlias, entry.ApiName, work.Id)
            operations.Add(AddCall call)

            let callLabel = $"{call.DevicesAlias}.{call.ApiName}"
            addBucketItem callsByFlow entry.FlowName call callLabel entry.InName entry.InAddress entry.OutName entry.OutAddress

        for KeyValue(flowName, calls) in callsByFlow do
            let linkedCalls =
                calls
                |> Seq.map (fun (call, label, _, _, _, _) -> call, label)
                |> Seq.toList

            ImportPlanDeviceOps.linkCallsToDevices store projectId flowName linkedCalls operations

            for call, _, inName, inAddress, outName, outAddress in calls do
                if call.ApiCalls.Count > 0 && (inName.IsSome || inAddress.IsSome || outName.IsSome || outAddress.IsSome) then
                    let apiCall = call.ApiCalls.[0]
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
