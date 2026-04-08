namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module AasxImporter =

    open AasxImportCore
    open AasxImportGraph
    open AasxImportMetadata

    /// 도메인 서브모델 Import (SequenceSimulation, SequenceControl, etc.)
    let private importDomainSubmodel (sm: Submodel) (store: DsStore) (submodelType: SubmodelType) : unit =
        if sm.SubmodelElements = null then ()
        else
            // SystemProperties SMC 찾기 (간결화)
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
                            PropertyConversion.importSystemProperty submodelType systemSmc system.Properties)))

            // FlowProperties SMC 찾기 (간결화)
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

            // WorkProperties SMC 찾기 (간결화 버전)
            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as smc when smc.IdShort = "WorkProperties" -> Some smc
                | _ -> None)
            |> Option.iter (fun workPropsContainer ->
                if workPropsContainer.Value <> null then
                    workPropsContainer.Value
                    |> Seq.choose (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
                    |> Seq.iter (fun workSmc ->
                        // idShort에서 직접 GUID 추출 (Guid 프로퍼티 불필요)
                        extractGuidFromIdShort workSmc.IdShort
                        |> Option.bind (fun workId -> store.Works.TryGetValue(workId) |> function true, work -> Some work | _ -> None)
                        |> Option.iter (fun work ->
                            PropertyConversion.importWorkProperty submodelType workSmc work.Properties)))

            // CallProperties SMC 찾기 (간결화)
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
                    log.Warn($"AASX 파싱 실패: '{SubmodelModelIdShort}' Submodel을 찾을 수 없습니다 ({path})")
                    None
                | Some (project, store) ->
                    // Nameplate Submodel 파싱
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
                    // Documentation Submodel 파싱
                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                    |> Option.iter (fun sm ->
                        let doc = submodelToDocumentation sm
                        if doc.Documents.Count > 0 then project.HandoverDocumentation <- Some doc)

                    // 도메인별 Submodel import (Simulation, Control, Monitoring, Logging, Maintenance, CostAnalysis, Quality, Hmi)
                    SubmodelType.AllDomains
                    |> List.iter (fun submodelType ->
                        env.Submodels
                        |> Seq.tryPick (fun sm -> if sm.IdShort = submodelType.IdShort then Some (sm :?> Submodel) else None)
                        |> Option.iter (fun sm -> importDomainSubmodel sm store submodelType))

                    Some store)

    let importIntoStore (store: DsStore) (path: string) : bool =
        match importFromAasxFile path with
        | Some imported ->
            store.ReplaceStore(imported)
            true
        | None -> false
