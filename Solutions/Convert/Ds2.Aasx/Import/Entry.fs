namespace Ds2.Aasx

open System
open System.IO
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module AasxImporter =

    open AasxImportCore
    open AasxImportGraph
    open AasxImportMetadata
    open AasxImportTechnicalData
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
                                let (presets, legacySysBase, legacyFlowBase, _legacyDeviceTemplates) = smcToControlIoConfig systemSmc
                                let hasData =
                                    presets.Count > 0 ||
                                    not (String.IsNullOrEmpty legacySysBase) ||
                                    not (String.IsNullOrEmpty legacyFlowBase)
                                if hasData then
                                    if system.GetControlProperties().IsNone then
                                        system.SetControlProperties(ControlSystemProperties())
                                    let cp = system.GetControlProperties().Value
                                    for kv in presets do cp.FBTagMapPresets.[kv.Key] <- kv.Value
                                    // 레거시 IoSystemBase → FBTagMapPreset.BaseAddresses 이식
                                    if not (String.IsNullOrEmpty legacySysBase) then
                                        ControlIoLegacyMigration.applySystemBaseToPresets cp.FBTagMapPresets legacySysBase |> ignore
                                    // 레거시 IoFlowBase → Flow 별 BaseAddressOverride 이식
                                    if not (String.IsNullOrEmpty legacyFlowBase) then
                                        let flowMap = ControlIoLegacyMigration.parseFlowBase legacyFlowBase
                                        for flow in store.Flows.Values do
                                            match flowMap.TryGetValue flow.Name with
                                            | true, baseSet ->
                                                let cfp =
                                                    match flow.GetControlProperties() with
                                                    | Some c -> c
                                                    | None ->
                                                        let c = ControlFlowProperties()
                                                        flow.SetControlProperties c
                                                        c
                                                cfp.BaseAddressOverride <- Some baseSet
                                            | _ -> ()
                                    // IoDeviceTemplates 는 신 모델에서 FBTagMapPreset.FBTagMapTemplate 으로 대체되며
                                    // 자동 이식이 어렵기 때문에 로그만 남기고 무시.
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
                        // SequenceModel 서브모델 찾기 (구 버전 "SequenceControlSubmodel"도 호환)
                        if sm.IdShort = SubmodelModelIdShort || sm.IdShort = LegacySubmodelIdShort then
                            submodelToProjectStore sm (Some mainDir)
                        else None)
                match result with
                | None ->
                    let idShorts = env.Submodels |> Seq.map (fun sm -> sm.IdShort) |> Seq.toList
                    let hasLegacy = idShorts |> List.exists (fun id -> id = "SequenceControlSubmodel")
                    if hasLegacy then
                        log.Warn($"AASX import 실패: 구 포맷('SequenceControlSubmodel') 파일입니다. ds2 에디터에서 다시 내보내기(Export) 해 주세요. ({path})")
                    else
                        log.Warn($"AASX import 실패: '{SubmodelModelIdShort}' Submodel을 찾을 수 없습니다. (발견된 Submodels: {idShorts}) ({path})")
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

                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = TechnicalDataSubmodelIdShort)
                    |> Option.iter (fun sm ->
                        let td = submodelToTechnicalData sm
                        project.TechnicalData <- Some td)

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
                        newSystem.SystemType <- Some "Cylinder_1"
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

                env.Submodels
                |> Seq.tryFind (fun sm -> sm.IdShort = TechnicalDataSubmodelIdShort)
                |> Option.iter (fun sm ->
                    let td = submodelToTechnicalData sm
                    project.TechnicalData <- Some td)

                SubmodelType.AllDomains
                |> List.iter (fun submodelType ->
                    env.Submodels
                    |> Seq.tryPick (fun sm -> if sm.IdShort = submodelType.IdShort then Some (sm :?> Submodel) else None)
                    |> Option.iter (fun sm -> importDomainSubmodel sm imported project submodelType))

                store.ReplaceStore(imported)

                // 레거시 파일 복구: OriginFlowId 가 누락된 ApiCall 을 Call→Work→Flow 체인으로 자동 설정.
                // (예전 Panel.buildApiCall 경유 생성 시 해당 값이 누락되던 버그의 뒤처리)
                let healed = CallValidation.healMissingOriginFlowIds store
                if healed > 0 then
                    log.Info($"OriginFlowId 자동 복구: {healed}개 ApiCall")

                Ok ()

    let importIntoStore (store: DsStore) (path: string) : bool =
        match importIntoStoreWithError store path with
        | Ok () -> true
        | Error msg ->
            log.Warn($"AASX import failed: {msg}")
            false

    let importIntoStoreOrRaise (store: DsStore) (path: string) : unit =
        match importIntoStoreWithError store path with
        | Ok () -> ()
        | Error msg -> raise (InvalidDataException(msg))
