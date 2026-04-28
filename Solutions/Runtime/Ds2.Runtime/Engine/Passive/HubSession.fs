namespace Ds2.Runtime.Engine.Passive

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type RuntimeHubSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
    member _.HandleHubTag(address: string, value: string, source: string) =
        let effects = ResizeArray<RuntimeHubEffect>()

        match runtimeMode with
        | RuntimeMode.Control ->
            let inMappings = ioMap.GetByInAddress(address)
            if not (List.isEmpty inMappings) then
                RuntimeSessionEffects.addInjectIo effects address value
                if value = "true" then
                    for mapping in inMappings do
                        match mapping.RxWorkGuid with
                        // currentState=Going 일 때만 atomic Force. 외부 PLC 의 stale IN=true 가
                        // Reset 흐름 도중 Homing→Finish 잘못 전이시키는 race 차단.
                        | Some rxWorkGuid -> RuntimeSessionEffects.addForceWorkStateIfGoing effects 0 rxWorkGuid Status4.Finish
                        | None -> ()

                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Finish (sprintf "[Ctrl] In %s=%s (from %s)" address value source)
            else
                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Warn (sprintf "[Ctrl] %s=%s [unmapped]" address value)

        | RuntimeMode.VirtualPlant ->
            match ioMap.GetByOutAddress(address) |> List.tryHead with
            | Some mapping ->
                match mapping.TxWorkGuid with
                | Some txWorkGuid ->
                    if value = "true" then
                        RuntimeSessionEffects.addForceWorkState effects 0 txWorkGuid Status4.Going

                        match index.WorkResetPreds |> Map.tryFind txWorkGuid with
                        | Some resetPreds ->
                            for predGuid in resetPreds |> Seq.distinct do
                                match ioMap.RxWorkToInAddresses |> Map.tryFind predGuid with
                                | Some resetInAddresses ->
                                    for resetInAddr in resetInAddresses do
                                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Homing (sprintf "[VP] Reset input: %s=false" resetInAddr)
                                        RuntimeSessionEffects.addWriteTag effects 0 resetInAddr "false"
                                | None -> ()
                        | None -> ()

                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Going (sprintf "[VP] Out ON: %s -> Device Going" address)

                        if not (String.IsNullOrEmpty(mapping.InAddress)) then
                            let duration =
                                index.WorkDuration
                                |> Map.tryFind txWorkGuid
                                |> Option.map int
                                |> Option.defaultValue 500

                            RuntimeSessionEffects.addForceWorkState effects duration txWorkGuid Status4.Finish
                            RuntimeSessionEffects.addWriteTag effects duration mapping.InAddress "true"
                            RuntimeSessionEffects.addLog effects duration RuntimeHubLogSeverity.Finish (sprintf "[VP] In ON: %s (after %dms)" mapping.InAddress duration)
                    else if not (String.IsNullOrEmpty(mapping.InAddress)) then
                        RuntimeSessionEffects.addWriteTag effects 0 mapping.InAddress "false"
                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Ready (sprintf "[VP] In OFF: %s" mapping.InAddress)
                | None -> ()
            | None -> ()

            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Monitoring ->
            RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Info (sprintf "[Mon] %s=%s (from %s)" address value source)
            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Simulation ->
            ()
        | _ ->
            ()

        effects.ToArray()
