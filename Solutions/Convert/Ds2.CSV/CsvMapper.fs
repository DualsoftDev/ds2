namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.UI.Core

module internal CsvMapper =

    let tryResolveProjectId (store: DsStore) (systemId: Guid) =
        DsQuery.allProjects store
        |> List.tryFind (fun project ->
            project.ActiveSystemIds.Contains(systemId) ||
            project.PassiveSystemIds.Contains(systemId))
        |> Option.map (fun project -> project.Id)

    let private addBucketItem
        (buckets: Dictionary<string, ResizeArray<Call * string * string option * string option>>)
        (flowName: string)
        (call: Call)
        (callLabel: string)
        (inAddress: string option)
        (outAddress: string option) =
        let bucket =
            match buckets.TryGetValue(flowName) with
            | true, existing -> existing
            | false, _ ->
                let created = ResizeArray()
                buckets.[flowName] <- created
                created
        bucket.Add(call, callLabel, inAddress, outAddress)

    let mapToSystem (store: DsStore) (projectId: Guid) (systemId: Guid) (document: CsvDocument) =
        let flows = Dictionary<string, Flow>()
        let works = Dictionary<Guid * string, Work>()
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option * string option>>()

        for entry in document.Entries do
            let flow =
                match flows.TryGetValue(entry.FlowName) with
                | true, existing -> existing
                | false, _ ->
                    let created = Flow(entry.FlowName, systemId)
                    store.TrackAdd(store.Flows, created)
                    flows.[entry.FlowName] <- created
                    created

            let workKey = (flow.Id, entry.WorkName)
            let work =
                match works.TryGetValue(workKey) with
                | true, existing -> existing
                | false, _ ->
                    let created = Work(entry.WorkName, flow.Id)
                    store.TrackAdd(store.Works, created)
                    works.[workKey] <- created
                    created

            let call = Call(entry.DeviceAlias, entry.ApiName, work.Id)
            store.TrackAdd(store.Calls, call)

            let callLabel = $"{call.DevicesAlias}.{call.ApiName}"
            addBucketItem callsByFlow entry.FlowName call callLabel entry.InAddress entry.OutAddress

        for KeyValue(flowName, calls) in callsByFlow do
            let linkedCalls =
                calls
                |> Seq.map (fun (call, label, _, _) -> call, label)
                |> Seq.toList

            DirectDeviceOps.linkCallsToDevices store projectId flowName linkedCalls

            for call, _, inAddress, outAddress in calls do
                if call.ApiCalls.Count > 0 && (inAddress.IsSome || outAddress.IsSome) then
                    let apiCall = call.ApiCalls.[0]
                    store.TrackMutate(store.ApiCalls, apiCall.Id, fun current ->
                        match outAddress with
                        | Some address -> current.OutTag <- Some(IOTag("Out", address, ""))
                        | None -> ()
                        match inAddress with
                        | Some address -> current.InTag <- Some(IOTag("In", address, ""))
                        | None -> ())
