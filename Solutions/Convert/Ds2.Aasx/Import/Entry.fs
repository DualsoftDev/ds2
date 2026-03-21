namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module AasxImporter =

    open AasxImportCore
    open AasxImportGraph
    open AasxImportMetadata
    open Ds2.Aasx.Compat

    let internal importFromAasxFile (path: string) : DsStore option =
        readEnvironment path
        |> Option.bind (fun env ->
            if env.Submodels = null then
                log.Warn($"AASX 파싱 실패: Submodels null ({path})")
                None
            else
                let result =
                    env.Submodels
                    |> Seq.tryPick (fun sm ->
                        if sm.IdShort = SubmodelIdShort then
                            // legacy(dsev2) 감지 → 호환 경로, 아니면 정규 경로
                            if LegacyAasxDetector.isLegacyFormat sm then
                                LegacyImportGraph.legacySubmodelToProjectStore sm
                            else
                                submodelToProjectStore sm
                        else None)
                match result with
                | None ->
                    log.Warn($"AASX 파싱 실패: '{SubmodelIdShort}' Submodel을 찾을 수 없습니다 ({path})")
                    None
                | Some (project, store) ->
                    // Nameplate Submodel 파싱
                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = NameplateSubmodelIdShort)
                    |> Option.iter (fun sm -> project.Nameplate <- submodelToNameplate sm)
                    // Documentation Submodel 파싱
                    env.Submodels
                    |> Seq.tryFind (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                    |> Option.iter (fun sm -> project.HandoverDocumentation <- submodelToDocumentation sm)
                    Some store)

    let importIntoStore (store: DsStore) (path: string) : bool =
        match importFromAasxFile path with
        | Some imported ->
            store.ReplaceStore(imported)
            true
        | None -> false
