namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core.Store

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SimIndex =

    let findOrEmpty key map =
        SimIndexAlgorithms.findOrEmpty key map

    let canonicalWorkGuid (index: SimIndex) (workGuid: Guid) =
        index.WorkCanonicalGuids
        |> Map.tryFind workGuid
        |> Option.defaultValue workGuid

    let referenceGroupOf (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.WorkReferenceGroups
        |> Map.tryFind canonical
        |> Option.defaultValue [ canonical ]

    let workGroupOf (index: SimIndex) (workGuid: Guid) =
        index.WorkGroupSets |> Map.tryFind workGuid |> Option.defaultValue Set.empty

    let canonicalCallGuid (index: SimIndex) (callGuid: Guid) =
        index.CallCanonicalGuids
        |> Map.tryFind callGuid
        |> Option.defaultValue callGuid

    let callReferenceGroupOf (index: SimIndex) (callGuid: Guid) =
        let canonical = canonicalCallGuid index callGuid
        index.CallReferenceGroups
        |> Map.tryFind canonical
        |> Option.defaultValue [ canonical ]

    let isTokenSource (index: SimIndex) (workGuid: Guid) =
        let canonical = canonicalWorkGuid index workGuid
        index.TokenSourceGuids |> List.contains canonical

    let txWorkGuids (index: SimIndex) (callGuid: Guid) =
        SimIndexAlgorithms.resolveApiDefGuids
            index.Store
            (findOrEmpty callGuid index.CallApiCallGuids)
            (fun d -> d.TxGuid)

    let rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        SimIndexAlgorithms.resolveApiDefGuids
            index.Store
            (findOrEmpty callGuid index.CallApiCallGuids)
            (fun d -> d.RxGuid)

    let build (store: DsStore) (tickMs: int) : SimIndex =
        SimIndexBuild.build store tickMs

    type ConnectionSnapshot = SimIndexConnectionSnapshot

    let snapshotConnections (index: SimIndex) : ConnectionSnapshot =
        SimIndexReload.snapshotConnections index

    let reloadConnections (index: SimIndex) : ConnectionSnapshot * ConnectionSnapshot =
        SimIndexReload.reloadConnections index

    let reloadDurations (index: SimIndex) (skipGuids: Set<Guid>) =
        SimIndexReload.reloadDurations index skipGuids

    let computeAutoHomingTargets (index: SimIndex) : Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingTargets index

    let computeAutoHomingPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingPlan index

    let computeAutoHomingCallPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexHomingQueries.computeAutoHomingCallPlan index

    let findHomingEntryPoints (index: SimIndex) (readyTargets: Set<Guid>) : Guid list * string list =
        SimIndexHomingQueries.findHomingEntryPoints index readyTargets

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        SimIndexHomingQueries.findInitialFlagRxWorkGuids index

    let hasAnyTokenRole (index: SimIndex) : bool =
        SimIndexHomingQueries.hasAnyTokenRole index
