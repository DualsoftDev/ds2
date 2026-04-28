namespace Ds2.Runtime.Engine.Passive

open System.Collections.Generic
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type RuntimeModeSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
    let bootstrapSession = RuntimeBootstrapSession(index, ioMap, runtimeMode)
    let hubSession = RuntimeHubSession(index, ioMap, runtimeMode)

    let hubSource =
        match runtimeMode with
        | RuntimeMode.Control -> "control"
        | RuntimeMode.VirtualPlant -> "virtualplant"
        | RuntimeMode.Monitoring -> "monitoring"
        | RuntimeMode.Simulation -> ""
        | _ -> ""

    member _.RuntimeMode = runtimeMode
    member _.HubSource = hubSource
    member _.RequiresHubConnection = runtimeMode <> RuntimeMode.Simulation
    member _.UsesControlWriteBridge = runtimeMode = RuntimeMode.Control
    member _.RequiresPassiveInference = bootstrapSession.RequiresPassiveInference
    member _.StartsWithHomingPhase = bootstrapSession.StartsWithHomingPhase
    member _.RequiresHubSnapshotSync = bootstrapSession.RequiresHubSnapshotSync
    member _.HubConnectionWaitTimeoutMs = bootstrapSession.HubConnectionWaitTimeoutMs
    member _.HubSnapshotSyncTimeoutMs = bootstrapSession.HubSnapshotSyncTimeoutMs

    member _.ShouldIgnoreHubSource(source: string) =
        match runtimeMode with
        | RuntimeMode.VirtualPlant -> false
        | _ -> hubSource <> "" && source = hubSource

    member _.BuildHubSnapshotQueryAddresses() =
        bootstrapSession.BuildHubSnapshotQueryAddresses()

    member _.ResolveHubSnapshotEffects(tagValues: IReadOnlyDictionary<string, string>) =
        match runtimeMode with
        | RuntimeMode.VirtualPlant
        | RuntimeMode.Monitoring ->
            tagValues
            |> Seq.filter (fun (KeyValue(address, value)) -> not (System.String.IsNullOrWhiteSpace(address)) && value = "true")
            |> Seq.collect (fun (KeyValue(address, value)) -> hubSession.HandleHubTag(address, value, "snapshot"))
            |> Seq.toArray
        | _ ->
            bootstrapSession.ResolveHubSnapshotEffects(tagValues)

    member _.HandleHubTag(address: string, value: string, source: string) =
        hubSession.HandleHubTag(address, value, source)
