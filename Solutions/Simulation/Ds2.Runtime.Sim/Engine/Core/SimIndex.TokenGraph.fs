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
