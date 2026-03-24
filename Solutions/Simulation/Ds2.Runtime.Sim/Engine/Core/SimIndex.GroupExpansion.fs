namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core

module internal SimIndexGroupExpansion =

    let private addPredecessor (target: Guid) (source: Guid) (m: Map<Guid, Guid list>) =
        match m.TryFind target with
        | Some preds -> if List.contains source preds then m else m.Add(target, source :: preds)
        | None -> m.Add(target, [ source ])

    let rec private findRoot (parent: Map<Guid, Guid>) (x: Guid) =
        match parent.TryFind x with
        | Some p when p <> x -> findRoot parent p
        | _ -> x

    let private union (parent: Map<Guid, Guid>) (x: Guid) (y: Guid) =
        let rootX = findRoot parent x
        let rootY = findRoot parent y
        if rootX = rootY then parent else parent.Add(rootY, rootX)

    let buildGroupSets (sourceIds: Guid list) (targetIds: Guid list) =
        if sourceIds.IsEmpty then []
        else
            let allNodes = (sourceIds @ targetIds) |> List.distinct
            let initialParent = allNodes |> List.map (fun n -> n, n) |> Map.ofList
            let finalParent =
                List.zip sourceIds targetIds
                |> List.fold (fun parent (sourceId, targetId) -> union parent sourceId targetId) initialParent

            allNodes
            |> List.groupBy (findRoot finalParent)
            |> List.map (fun (_, nodes) -> Set.ofList nodes)

    let private expandSingleGroup (groupSet: Set<Guid>) (startMap: Map<Guid, Guid list>) (resetMap: Map<Guid, Guid list>) =
        let members = groupSet |> Set.toList

        let externalStartPreds =
            members
            |> List.collect (fun memberId -> startMap.TryFind memberId |> Option.defaultValue [])
            |> List.filter (fun predId -> not (Set.contains predId groupSet))
            |> List.distinct

        let startWithExternal =
            List.allPairs members externalStartPreds
            |> List.fold (fun acc (memberId, predId) -> addPredecessor memberId predId acc) startMap

        let startSuccessors =
            startWithExternal
            |> Map.toSeq
            |> Seq.filter (fun (succId, preds) -> not (Set.contains succId groupSet) && preds |> List.exists (fun predId -> Set.contains predId groupSet))
            |> Seq.map fst
            |> Seq.toList

        let finalStart =
            List.allPairs startSuccessors members
            |> List.fold (fun acc (succId, memberId) -> addPredecessor succId memberId acc) startWithExternal

        let externalResetPreds =
            members
            |> List.collect (fun memberId -> resetMap.TryFind memberId |> Option.defaultValue [])
            |> List.filter (fun predId -> not (Set.contains predId groupSet))
            |> List.distinct

        let resetWithExternal =
            List.allPairs members externalResetPreds
            |> List.fold (fun acc (memberId, predId) -> addPredecessor memberId predId acc) resetMap

        let resetSuccessors =
            resetWithExternal
            |> Map.toSeq
            |> Seq.filter (fun (succId, preds) -> not (Set.contains succId groupSet) && preds |> List.exists (fun predId -> Set.contains predId groupSet))
            |> Seq.map fst
            |> Seq.toList

        let finalReset =
            List.allPairs resetSuccessors members
            |> List.fold (fun acc (succId, memberId) -> addPredecessor succId memberId acc) resetWithExternal

        finalStart, finalReset

    let expandWorkGroupArrows (groupArrows: ArrowBetweenWorks list) (startMap: Map<Guid, Guid list>) (resetMap: Map<Guid, Guid list>) =
        let sources = groupArrows |> List.map (fun arrow -> arrow.SourceId)
        let targets = groupArrows |> List.map (fun arrow -> arrow.TargetId)
        let groupSets = buildGroupSets sources targets
        groupSets |> List.fold (fun (starts, resets) groupSet -> expandSingleGroup groupSet starts resets) (startMap, resetMap)

    let private expandCallSingleGroup (groupSet: Set<Guid>) (predsMap: Map<Guid, Guid list>) =
        let members = groupSet |> Set.toList

        let externalPreds =
            members
            |> List.collect (fun memberId -> predsMap.TryFind memberId |> Option.defaultValue [])
            |> List.filter (fun predId -> not (Set.contains predId groupSet))
            |> List.distinct

        let predsWithExternal =
            List.allPairs members externalPreds
            |> List.fold (fun acc (memberId, predId) -> addPredecessor memberId predId acc) predsMap

        let successors =
            predsWithExternal
            |> Map.toSeq
            |> Seq.filter (fun (succId, preds) -> not (Set.contains succId groupSet) && preds |> List.exists (fun predId -> Set.contains predId groupSet))
            |> Seq.map fst
            |> Seq.toList

        List.allPairs successors members
        |> List.fold (fun acc (succId, memberId) -> addPredecessor succId memberId acc) predsWithExternal

    let expandCallGroupArrows (groupArrows: ArrowBetweenCalls list) (predsMap: Map<Guid, Guid list>) =
        let sources = groupArrows |> List.map (fun arrow -> arrow.SourceId)
        let targets = groupArrows |> List.map (fun arrow -> arrow.TargetId)
        let groupSets = buildGroupSets sources targets
        groupSets |> List.fold (fun acc groupSet -> expandCallSingleGroup groupSet acc) predsMap
