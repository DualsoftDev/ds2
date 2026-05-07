namespace Ds2.Aasx

open AasCore.Aas3_1
open Ds2.Core

/// IDTA 02003 TechnicalData → Ds2.Core POCO 역직렬화 (Round-trip)
module internal AasxImportTechnicalData =

    open AasxImportCore

    let private tryFindSmc (parent: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection option =
        if parent.Value = null then None
        else
            parent.Value |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

    let private tryFindSml (parent: SubmodelElementCollection) (idShort: string) : SubmodelElementList option =
        if parent.Value = null then None
        else
            parent.Value |> Seq.tryPick (function
                | :? SubmodelElementList as l when l.IdShort = idShort -> Some l
                | _ -> None)

    let private tryFindSmcInSubmodel (sm: ISubmodel) (idShort: string) : SubmodelElementCollection option =
        if sm.SubmodelElements = null then None
        else
            sm.SubmodelElements |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

    let private smlChildSmcs (sml: SubmodelElementList) : SubmodelElementCollection list =
        if sml.Value = null then []
        else
            sml.Value
            |> Seq.choose (function :? SubmodelElementCollection as c -> Some c | _ -> None)
            |> Seq.toList

    // ── 그룹 단위 디시리얼라이저 ──────────────────────────────────────────
    let private smcToGeneralInfo (smc: SubmodelElementCollection) : TdGeneralInformation =
        let gi = elementsToProps<TdGeneralInformation> smc |> Option.defaultWith TdGeneralInformation
        // MLP 처리 (mkMlp 로 export 한 필드들)
        if smc.Value <> null then
            for elem in smc.Value do
                match elem with
                | :? MultiLanguageProperty as mlp when mlp.Value <> null && mlp.Value.Count > 0 ->
                    let v = mlp.Value.[0].Text
                    match mlp.IdShort with
                    | "ManufacturerName"               -> gi.ManufacturerName <- v
                    | "ManufacturerProductDesignation" -> gi.ManufacturerProductDesignation <- v
                    | _ -> ()
                | _ -> ()
        // ProductImages SML
        match tryFindSml smc "ProductImages" with
        | Some sml when sml.Value <> null ->
            for item in sml.Value do
                match item with
                | :? Property as p when p.Value <> null -> gi.ProductImages.Add(p.Value)
                | _ -> ()
        | _ -> ()
        gi

    let private smcToClassificationItem (smc: SubmodelElementCollection) : TdProductClassificationItem =
        let c = TdProductClassificationItem()
        // v2.0: ClassificationSystem / v1.x: ProductClassificationSystem (둘 다 시도)
        c.ClassificationSystem  <-
            (getProp smc "ClassificationSystem"
             |> Option.orElseWith (fun () -> getProp smc "ProductClassificationSystem")
             |> Option.defaultValue "")
        c.ClassificationVersion <- getProp smc "ClassificationSystemVersion" |> Option.defaultValue ""
        c.ProductClassId        <- getProp smc "ProductClassId"              |> Option.defaultValue ""
        c

    let private smcToFurtherInfo (smc: SubmodelElementCollection) : TdFurtherInformation =
        let fi = elementsToProps<TdFurtherInformation> smc |> Option.defaultWith TdFurtherInformation
        if smc.Value <> null then
            for elem in smc.Value do
                match elem with
                | :? MultiLanguageProperty as mlp when mlp.IdShort = "TextStatement" && mlp.Value <> null && mlp.Value.Count > 0 ->
                    fi.TextStatement <- mlp.Value.[0].Text
                | _ -> ()
        match tryFindSml smc "ReferenceDocuments" with
        | Some sml when sml.Value <> null ->
            for item in sml.Value do
                match item with
                | :? Property as p when p.Value <> null -> fi.ReferenceDocuments.Add(p.Value)
                | _ -> ()
        | _ -> ()
        fi

    let private smcToSimMeta (smc: SubmodelElementCollection) : SimulationMeta =
        elementsToProps<SimulationMeta> smc |> Option.defaultWith SimulationMeta

    let private smcToCycleTime (smc: SubmodelElementCollection) : KpiCycleTime =
        elementsToProps<KpiCycleTime> smc |> Option.defaultWith KpiCycleTime

    let private smcToCallCycleTime (smc: SubmodelElementCollection) : KpiCallCycleTime =
        elementsToProps<KpiCallCycleTime> smc |> Option.defaultWith KpiCallCycleTime

    let private smcToThroughput (smc: SubmodelElementCollection) : KpiThroughput =
        elementsToProps<KpiThroughput> smc |> Option.defaultWith KpiThroughput

    let private smcToCapacity (smc: SubmodelElementCollection) : KpiCapacity =
        elementsToProps<KpiCapacity> smc |> Option.defaultWith KpiCapacity

    let private smcToConstraintItem (smc: SubmodelElementCollection) : KpiConstraintItem =
        elementsToProps<KpiConstraintItem> smc |> Option.defaultWith KpiConstraintItem

    let private smcToResourceItem (smc: SubmodelElementCollection) : KpiResourceItem =
        elementsToProps<KpiResourceItem> smc |> Option.defaultWith KpiResourceItem

    let private smcToOeeItem (smc: SubmodelElementCollection) : KpiOeeItem =
        elementsToProps<KpiOeeItem> smc |> Option.defaultWith KpiOeeItem

    let private smcToWorkBreakdown (smc: SubmodelElementCollection) : KpiPerTokenWorkBreakdown =
        elementsToProps<KpiPerTokenWorkBreakdown> smc |> Option.defaultWith KpiPerTokenWorkBreakdown

    let private smcToPerToken (smc: SubmodelElementCollection) : KpiPerToken =
        let r = elementsToProps<KpiPerToken> smc |> Option.defaultWith KpiPerToken
        match tryFindSml smc "WorkBreakdown" with
        | Some sml -> for c in smlChildSmcs sml do r.WorkBreakdown.Add(smcToWorkBreakdown c)
        | None -> ()
        r

    /// 외부 노출 — SequenceSimulation 서브모델에서 SimulationResult SMC 를 발견하면 본 함수로 디시리얼라이즈.
    let smcToScenario (smc: SubmodelElementCollection) : SimulationScenario =
        let s = SimulationScenario()
        match tryFindSmc smc "SimulationMeta" with
        | Some m -> s.Meta <- smcToSimMeta m
        | None -> ()
        match tryFindSml smc "KPI_CycleTime" with
        | Some sml -> for c in smlChildSmcs sml do s.CycleTimes.Add(smcToCycleTime c)
        | None -> ()
        match tryFindSml smc "KPI_CallCycleTime" with
        | Some sml -> for c in smlChildSmcs sml do s.CallCycleTimes.Add(smcToCallCycleTime c)
        | None -> ()
        s.Throughput <- tryFindSmc smc "KPI_Throughput" |> Option.map smcToThroughput
        s.Capacity   <- tryFindSmc smc "KPI_Capacity"   |> Option.map smcToCapacity
        match tryFindSml smc "KPI_Constraints" with
        | Some sml -> for c in smlChildSmcs sml do s.Constraints.Add(smcToConstraintItem c)
        | None -> ()
        match tryFindSml smc "KPI_ResourceUtilization" with
        | Some sml -> for c in smlChildSmcs sml do s.ResourceUtilizations.Add(smcToResourceItem c)
        | None -> ()
        match tryFindSml smc "KPI_OEE" with
        | Some sml -> for c in smlChildSmcs sml do s.OeeItems.Add(smcToOeeItem c)
        | None -> ()
        match tryFindSml smc "KPI_PerToken" with
        | Some sml -> for c in smlChildSmcs sml do s.PerTokenKpis.Add(smcToPerToken c)
        | None -> ()
        s

    // ── 진입점 — TD 는 표준 3 블록만 ────────────────────────────────────────
    let submodelToTechnicalData (sm: ISubmodel) : TechnicalData =
        let td = TechnicalData()

        tryFindSmcInSubmodel sm "GeneralInformation"
        |> Option.iter (fun smc -> td.GeneralInformation <- smcToGeneralInfo smc)

        // ProductClassifications: SubmodelElementList 또는 비어있는 SMC
        if sm.SubmodelElements <> null then
            for elem in sm.SubmodelElements do
                match elem with
                | :? SubmodelElementList as sml when sml.IdShort = "ProductClassifications" ->
                    for c in smlChildSmcs sml do td.ProductClassifications.Add(smcToClassificationItem c)
                | _ -> ()

        tryFindSmcInSubmodel sm "FurtherInformation"
        |> Option.iter (fun smc -> td.FurtherInformation <- smcToFurtherInfo smc)

        td

    /// SequenceSimulation 서브모델의 SystemProperties SMC 안에서 SimulationResult SMC 를 찾아 디시리얼라이즈.
    /// (구버전 호환: top-level SimulationResult 도 폴백 검색)
    let trySimulationResultFromSequenceSimulation (sm: ISubmodel) : SimulationScenario option =
        // 1순위: SystemProperties 안에 있는 SimulationResult (현재 구조)
        let viaSystemProps =
            tryFindSmcInSubmodel sm "SystemProperties"
            |> Option.bind (fun sp -> tryFindSmc sp "SimulationResult")
        // 2순위: top-level (이전 잠시 사용된 위치) — 후방호환.
        let viaTopLevel = tryFindSmcInSubmodel sm "SimulationResult"
        viaSystemProps
        |> Option.orElse viaTopLevel
        |> Option.map smcToScenario
