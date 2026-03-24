namespace Ds2.Runtime.Sim.Engine.Core

open System

module internal SimIndexTokenGraph =

    let appendSuccessorsFromStartPreds existingMap (startPreds: Map<Guid, Guid list>) =
        let successorsFromPreds =
            startPreds
            |> Map.toSeq
            |> Seq.collect (fun (targetId, sourceIds) -> sourceIds |> List.map (fun sourceId -> sourceId, targetId))
            |> Seq.groupBy fst
            |> Seq.map (fun (sourceId, pairs) ->
                sourceId,
                pairs
                |> Seq.map snd
                |> Seq.distinct
                |> Seq.toList)
            |> Map.ofSeq

        successorsFromPreds
        |> Map.fold (fun acc key values ->
            let existing = acc |> Map.tryFind key |> Option.defaultValue []
            acc.Add(key, existing @ values)) existingMap

    let buildWorkGuidsByKey (allWorkGuids: Guid list) (workSystemName: Map<Guid, string>) (workName: Map<Guid, string>) =
        allWorkGuids
        |> List.choose (fun workGuid ->
            match Map.tryFind workGuid workSystemName, Map.tryFind workGuid workName with
            | Some systemName, Some workNameValue -> Some ((systemName, workNameValue), workGuid)
            | _ -> None)
        |> List.groupBy fst
        |> List.map (fun (key, grouped) -> key, grouped |> List.map snd)
        |> Map.ofList

    let findCycleSinks (tokenSources: Guid list) (tokenSuccessors: Map<Guid, Guid list>) =
        let mutable visited = Set.empty<Guid>
        let mutable onStack = Set.empty<Guid>
        let mutable sinks = Set.empty<Guid>

        let rec dfs nodeId =
            if not (visited.Contains nodeId) then
                visited <- visited.Add nodeId
                onStack <- onStack.Add nodeId

                tokenSuccessors
                |> Map.tryFind nodeId
                |> Option.defaultValue []
                |> List.iter (fun succId ->
                    if onStack.Contains succId then
                        sinks <- sinks.Add nodeId
                    else
                        dfs succId)

                onStack <- onStack.Remove nodeId

        tokenSources |> List.iter dfs
        sinks

    let findSourceBasedSinks (tokenSources: Guid list) (tokenSuccessors: Map<Guid, Guid list>) =
        tokenSuccessors
        |> Map.toSeq
        |> Seq.choose (fun (sourceId, succIds) ->
            if succIds |> List.exists (fun succId -> tokenSources |> List.contains succId) then Some sourceId
            else None)
        |> Set.ofSeq

    let buildTokenPathGuids (tokenSources: Guid list) (tokenSuccessors: Map<Guid, Guid list>) =
        let mutable visited = Set.empty<Guid>
        let mutable queue = tokenSources

        while not queue.IsEmpty do
            let currentId = queue.Head
            queue <- queue.Tail

            if not (visited.Contains currentId) then
                visited <- visited.Add currentId
                let successors = tokenSuccessors |> Map.tryFind currentId |> Option.defaultValue []
                queue <- queue @ successors

        visited
