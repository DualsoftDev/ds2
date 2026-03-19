/// dsev2(seq) AASX와 ds2(pro) AASX를 구분하는 감지 모듈.
/// 임시 하위호환용 — 버전 업 시 Compat/ 폴더 통째로 삭제.
namespace Ds2.Aasx.Compat

open AasCore.Aas3_0
open Ds2.Aasx.AasxSemantics
open log4net

module LegacyAasxDetector =

    let private log = LogManager.GetLogger("Ds2.Aasx.Compat")

    /// null-safe하게 SMC 자식 중 조건 맞는 SML을 tryPick
    let private tryPickSml (smc: SubmodelElementCollection) (pred: SubmodelElementList -> SubmodelElementCollection option) =
        if smc.Value = null then None
        else smc.Value |> Seq.tryPick (function :? SubmodelElementList as x -> pred x | _ -> None)

    /// null-safe하게 SML 자식 중 조건 맞는 SMC를 tryPick
    let private tryPickSmc (sml: SubmodelElementList) (pred: SubmodelElementCollection -> SubmodelElementCollection option) =
        if sml.Value = null then None
        else sml.Value |> Seq.tryPick (function :? SubmodelElementCollection as x -> pred x | _ -> None)

    /// Work 또는 Flow SMC 안에서 Calls SML → 첫 번째 Call SMC
    let private tryFindCallInParent (parentSmc: SubmodelElementCollection) =
        tryPickSml parentSmc (fun list ->
            if list.IdShort = Calls_ then tryPickSmc list Some else None)

    /// System SMC 안에서 Works/Flows SML → 내부 SMC → Calls → Call
    let private tryFindCallInSystem (systemSmc: SubmodelElementCollection) =
        tryPickSml systemSmc (fun list ->
            if list.IdShort = Works_ || list.IdShort = Flows_ then
                tryPickSmc list tryFindCallInParent
            else None)

    /// Submodel 내 첫 번째 Call SMC를 찾아 DevicesAlias 유무로 판별.
    /// DevicesAlias 없으면 legacy(seq), 있으면 ds2.
    let isLegacyFormat (sm: ISubmodel) : bool =
        if sm = null || sm.SubmodelElements = null then false
        else
            let firstCall =
                sm.SubmodelElements |> Seq.tryPick (function
                    | :? SubmodelElementCollection as projectSmc ->
                        tryPickSml projectSmc (fun sysList ->
                            if sysList.IdShort = ActiveSystems_ then
                                tryPickSmc sysList tryFindCallInSystem
                            else None)
                    | _ -> None)

            match firstCall with
            | Some callSmc ->
                let hasAlias = callSmc.Value <> null && callSmc.Value |> Seq.exists (fun e -> e.IdShort = DevicesAlias_)
                if not hasAlias then
                    log.Info("Legacy(dsev2) AASX format detected — DevicesAlias field missing in Call")
                not hasAlias
            | None -> false
