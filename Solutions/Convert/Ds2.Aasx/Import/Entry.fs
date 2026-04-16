namespace Ds2.Aasx

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module AasxImporter =

    open AasxImportCore
    open AasxImportGraph
    open AasxImportMetadata
    open FieldValidation

    let private importDomainSubmodel (sm: Submodel) (store: DsStore) (_project: Project) (submodelType: SubmodelType) : unit =
        if sm.SubmodelElements = null then ()
        else
            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as smc when smc.IdShort = "SystemProperties" -> Some smc
                | _ -> None)
            |> Option.iter (fun systemPropsContainer ->
                if systemPropsContainer.Value <> null then
                    systemPropsContainer.Value
                    |> Seq.choose (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
                    |> Seq.iter (fun systemSmc ->
                        extractGuidFromIdShort systemSmc.IdShort
                        |> Option.bind (fun systemId -> store.Systems.TryGetValue(systemId) |> function true, sys -> Some sys | _ -> None)
                        |> Option.iter (fun system ->
                            PropertyConversion.importSystemProperty submodelType systemSmc system.Properties
                            match submodelType with
                            | SequenceControl ->
                                let (presets, sysBase, flowBase, deviceTemplates) = smcToControlIoConfig systemSmc
                                let hasData = presets.Count > 0 || not (String.IsNullOrEmpty sysBase) || not (String.IsNullOrEmpty flowBase) || deviceTemplates.Count > 0
                                if hasData then
                                    if system.GetControlProperties().IsNone then
                                        system.SetControlProperties(ControlSystemProperties())
                                    let cp = system.GetControlProperties().Value
                                    for kv in presets do cp.FBTagMapPresets.[kv.Key] <- kv.Value
                                    if not (String.IsNullOrEmpty sysBase) then cp.IoSystemBase <- sysBase
                                    if not (String.IsNullOrEmpty flowBase) then cp.IoFlowBase <- flowBase
                                    for kv in deviceTemplates do cp.IoDeviceTemplates.[kv.Key] <- kv.Value
                            | _ -> ())))

            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as smc when smc.IdShort = "FlowProperties" -> Some smc
                | _ -> None)
            |> Option.iter (fun flowPropsContainer ->
                if flowPropsContainer.Value <> null then
                    flowPropsContainer.Value
                    |> Seq.choose (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
                    |> Seq.iter (fun flowSmc ->
                        extractGuidFromIdShort flowSmc.IdShort
                        |> Option.bind (fun flowId -> store.Flows.TryGetValue(flowId) |> function true, flow -> Some flow | _ -> None)
                        |> Option.iter (fun flow ->
                            PropertyConversion.importFlowProperty submodelType flowSmc flow.Properties)))

            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as smc when smc.IdShort = "WorkProperties" -> Some smc
                | _ -> None)
            |> Option.iter (fun workPropsContainer ->
                if workPropsContainer.Value <> null then
                    workPropsContainer.Value
                    |> Seq.choose (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
                    |> Seq.iter (fun workSmc ->
                        extractGuidFromIdShort workSmc.IdShort
                        |> Option.bind (fun workId -> store.Works.TryGetValue(workId) |> function true, work -> Some work | _ -> None)
                        |> Option.iter (fun work ->
                            PropertyConversion.importWorkProperty submodelType workSmc work.Properties)))

            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as smc when smc.IdShort = "CallProperties" -> Some smc
                | _ -> None)
            |> Option.iter (fun callPropsContainer ->
                if callPropsContainer.Value <> null then
                    callPropsContainer.Value
                    |> Seq.choose (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
                    |> Seq.iter (fun callSmc ->
                        extractGuidFromIdShort callSmc.IdShort
                        |> Option.bind (fun callId -> store.Calls.TryGetValue(callId) |> function true, call -> Some call | _ -> None)
                        |> Option.iter (fun call ->
                            PropertyConversion.importCallProperty submodelType callSmc call.Properties)))


    let internal importFromAasxFile (path: string) : DsStore option =
        let mainDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))
        readEnvironment path
        |> Option.bind (fun env ->
            if env.Submodels = null then
                log.Warn($"AASX 파싱 실패: Submodels null ({path})")
                None
            else
                let result =
                    env.Submodels
                    |> Seq.tryPick (fun sm ->
                        if sm.IdShort = SubmodelModelIdShort then submodelToProjectStore sm (Some mainDir)
                        else None)
                match result with
                | None ->
                    log.Warn($"AASX 파싱 실패: '{SubmodelModelIdShort}' Submodel을 찾을 수 없습니다 ({path})")
                    None
                | Some (project, store) ->
                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = NameplateSubmodelIdShort)
                    |> Option.iter (fun sm ->
                        let np = submodelToNameplate sm
                        let isEmpty =
                            String.IsNullOrEmpty(np.ManufacturerName)
                            && String.IsNullOrEmpty(np.URIOfTheProduct)
                            && String.IsNullOrEmpty(np.SerialNumber)
                            && np.Markings.Count = 0
                        if not isEmpty then project.Nameplate <- Some np)
                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                    |> Option.iter (fun sm ->
                        let doc = submodelToDocumentation sm
                        if doc.Documents.Count > 0 then project.HandoverDocumentation <- Some doc)

                    SubmodelType.AllDomains
                    |> List.iter (fun submodelType ->
                        env.Submodels
                        |> Seq.tryPick (fun sm -> if sm.IdShort = submodelType.IdShort then Some (sm :?> Submodel) else None)
                        |> Option.iter (fun sm -> importDomainSubmodel sm store project submodelType))

                    Some store)

    let importIntoStoreWithError (store: DsStore) (path: string) : Result<unit, string> =
        validateAll () |> ignore

        match readEnvironmentWithError path with
        | Error msg -> Error msg
        | Ok env ->
            if env.Submodels = null then
                Error "AASX 파일 구조 오류:\n\nSubmodels가 없습니다."
            else
                let mainDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))
                let result =
                    env.Submodels
                    |> Seq.tryPick (fun sm ->
                        if sm.IdShort = SubmodelModelIdShort then submodelToProjectStore sm (Some mainDir)
                        else None)

                let (project, imported) =
                    match result with
                    | Some (p, s) -> (p, s)
                    | None ->
                        log.Info($"'{SubmodelModelIdShort}' Submodel을 찾을 수 없습니다. 기본 프로젝트를 생성합니다.")
                        let newProject = Project("Imported AASX")
                        let newStore = DsStore()
                        newStore.DirectWrite(newStore.Projects, newProject)
                        let newSystem = DsSystem("NewSystem")
                        newSystem.SystemType <- Some "Unit"
                        newStore.DirectWrite(newStore.Systems, newSystem)
                        newProject.ActiveSystemIds.Add(newSystem.Id)
                        let newFlow = Flow("NewFlow", newSystem.Id)
                        newStore.DirectWrite(newStore.Flows, newFlow)
                        (newProject, newStore)

                let entries = readAllZipEntries path |> Option.defaultValue (System.Collections.Generic.Dictionary<string, byte[]>())
                AasxProjectCache.set project (box env) entries

                env.Submodels
                |> Seq.tryFind (fun sm -> sm.IdShort = NameplateSubmodelIdShort)
                |> Option.iter (fun sm ->
                    let np = submodelToNameplate sm
                    let isEmpty =
                        String.IsNullOrEmpty(np.ManufacturerName)
                        && String.IsNullOrEmpty(np.URIOfTheProduct)
                        && String.IsNullOrEmpty(np.SerialNumber)
                        && np.Markings.Count = 0
                    if not isEmpty then project.Nameplate <- Some np)

                env.Submodels
                |> Seq.tryFind (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                |> Option.iter (fun sm ->
                    let doc = submodelToDocumentation sm
                    if doc.Documents.Count > 0 then project.HandoverDocumentation <- Some doc)

                SubmodelType.AllDomains
                |> List.iter (fun submodelType ->
                    env.Submodels
                    |> Seq.tryPick (fun sm -> if sm.IdShort = submodelType.IdShort then Some (sm :?> Submodel) else None)
                    |> Option.iter (fun sm -> importDomainSubmodel sm imported project submodelType))

                store.ReplaceStore(imported)
                Ok ()

    let importIntoStore (store: DsStore) (path: string) : bool =
        match importIntoStoreWithError store path with
        | Ok () -> true
        | Error msg ->
            log.Warn($"AASX import failed: {msg}")
            false
