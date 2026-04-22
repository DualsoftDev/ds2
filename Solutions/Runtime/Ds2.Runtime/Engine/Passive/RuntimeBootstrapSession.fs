namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type RuntimeBootstrapSession(_index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
    let requiresPassiveInference =
        match runtimeMode with
        | RuntimeMode.VirtualPlant
        | RuntimeMode.Monitoring -> true
        | _ -> false

    let workIo =
        let txWorkGuids =
            ioMap.TxWorkToOutAddresses
            |> Seq.map (fun (KeyValue(workGuid, _)) -> workGuid)
            |> Set.ofSeq

        let rxWorkGuids =
            ioMap.RxWorkToInAddresses
            |> Seq.map (fun (KeyValue(workGuid, _)) -> workGuid)
            |> Set.ofSeq

        Set.union txWorkGuids rxWorkGuids
        |> Seq.map (fun workGuid ->
            let outAddresses =
                ioMap.TxWorkToOutAddresses
                |> Map.tryFind workGuid
                |> Option.defaultValue []
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> Seq.distinct
                |> Seq.toArray

            let inAddresses =
                ioMap.RxWorkToInAddresses
                |> Map.tryFind workGuid
                |> Option.defaultValue []
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> Seq.distinct
                |> Seq.toArray

            workGuid, outAddresses, inAddresses)
        |> Seq.toArray

    let getTagValue (tagValues: IReadOnlyDictionary<string, string>) address =
        let mutable value = Unchecked.defaultof<string>
        if not (String.IsNullOrWhiteSpace(address)) && tagValues.TryGetValue(address, &value) && not (isNull value) then
            value
        else
            ""

    member _.RequiresPassiveInference = requiresPassiveInference
    member _.StartsWithHomingPhase = not requiresPassiveInference
    member _.RequiresHubSnapshotSync = runtimeMode = RuntimeMode.Control
    member _.HubConnectionWaitTimeoutMs = 3000
    member _.HubSnapshotSyncTimeoutMs = 5000

    member _.BuildHubSnapshotQueryAddresses() =
        if runtimeMode <> RuntimeMode.Control then
            Array.empty
        else
            workIo
            |> Array.collect (fun (_, outAddresses, inAddresses) -> Array.append outAddresses inAddresses)
            |> Array.distinct

    member _.ResolveHubSnapshotEffects(tagValues: IReadOnlyDictionary<string, string>) =
        let effects = ResizeArray<RuntimeHubEffect>()

        if runtimeMode = RuntimeMode.Control then
            let mutable syncedWorks = 0

            for workGuid, outAddresses, inAddresses in workIo do
                if outAddresses.Length > 0 || inAddresses.Length > 0 then
                    let outOn = outAddresses |> Array.exists (fun address -> getTagValue tagValues address = "true")
                    let inOn = inAddresses |> Array.exists (fun address -> getTagValue tagValues address = "true")

                    let inferredState =
                        match outOn, inOn with
                        | false, false -> Status4.Ready
                        | true, false -> Status4.Going
                        | true, true -> Status4.Finish
                        | false, true -> Status4.Finish

                    RuntimeSessionEffects.addForceWorkState effects 0 workGuid inferredState
                    syncedWorks <- syncedWorks + 1

            RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.System (sprintf "[Ctrl] Device %d state sync complete" syncedWorks)

        effects.ToArray()
