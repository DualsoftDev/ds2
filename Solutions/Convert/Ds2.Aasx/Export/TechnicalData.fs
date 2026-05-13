namespace Ds2.Aasx

open System
open System.Collections.Generic
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Store
open Ds2.Aasx.AasxSemantics

/// IDTA 02003 TechnicalData → AAS Submodel 직렬화
/// + Sequence Model 시뮬결과 박제(SimulationResult SMC)
module internal AasxExportTechnicalData =

    open AasxExportCore

    // ── Qualifier helpers (Provenance / DataSource 태깅) ──────────────────
    /// 모든 KPI Property 에 부착되는 표준 Qualifier — DataSource = Simulation/Measurement/...
    let mkDataSourceQualifier (dataSource: string) : IQualifier =
        let q = Qualifier(``type`` = DataSourceQualifierType, valueType = DataTypeDefXsd.String)
        q.Value <- dataSource
        q :> IQualifier

    /// 주어진 SubmodelElement 에 DataSource Qualifier 부착
    let private withDataSource (dataSource: string) (elem: ISubmodelElement) : ISubmodelElement =
        let qualifiable = elem :> IQualifiable
        if qualifiable.Qualifiers = null then
            qualifiable.Qualifiers <- ResizeArray<IQualifier>()
        qualifiable.Qualifiers.Add(mkDataSourceQualifier dataSource)
        elem

    let private tagSim (elem: ISubmodelElement) = withDataSource DataSourceSimulation elem

    /// SemanticId 부착 + 반환
    let private withSem (semanticId: string) (elem: ISubmodelElement) : ISubmodelElement =
        (elem :> IHasSemantics).SemanticId <- mkSemanticRef semanticId
        elem

    // ── SimulationResults — 시뮬결과 박제 (SequenceSimulation 서브모델로 emit) ───
    let private simMetaToSmc (m: SimulationMeta) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkProp    "SimulatorName"    m.SimulatorName
            mkProp    "SimulatorVersion" m.SimulatorVersion
            mkProp    "Ds2ModelHash"     m.Ds2ModelHash
            mkProp    "ScenarioId"       m.ScenarioId
            mkProp    "ScenarioName"     m.ScenarioName
            mkProp    "RunDate"          (m.RunDate.ToString("O"))
            mkDoubleProp "RunDuration_s" m.RunDuration_s
            yield! (match m.Seed with Some s -> [mkIntProp "Seed" s] | None -> [])
            mkProp    "SignedBy"         m.SignedBy
        ]
        withSem SimulationMetaSemanticId (mkSmc "SimulationMeta" elems)

    let private cycleTimeToSmc (k: KpiCycleTime) : ISubmodelElement =
        mkSmc "CycleTimeItem" [
            mkProp       "WorkName"                     k.WorkName |> tagSim
            mkDoubleProp "DesignCycleTime_s"            k.DesignCycleTime_s |> tagSim
            mkDoubleProp "ActualCycleTime_s"            k.ActualCycleTime_s |> tagSim
            mkDoubleProp "MinCycleTime_s"               k.MinCycleTime_s |> tagSim
            mkDoubleProp "MaxCycleTime_s"               k.MaxCycleTime_s |> tagSim
            mkDoubleProp "StandardDeviation_s"          k.StandardDeviation_s |> tagSim
            mkDoubleProp "VariationCoefficient"         k.VariationCoefficient |> tagSim
            mkIntProp    "CycleCount"                   k.CycleCount |> tagSim
            mkDoubleProp "ConfidenceInterval95_Lower_s" k.ConfidenceInterval95_Lower_s |> tagSim
            mkDoubleProp "ConfidenceInterval95_Upper_s" k.ConfidenceInterval95_Upper_s |> tagSim
            mkBoolProp   "IsNormalDistribution"         k.IsNormalDistribution |> tagSim
            mkDoubleProp "DeviationFromDesign_pct"      k.DeviationFromDesign_pct |> tagSim
            mkBoolProp   "IsExceedingWarning"           k.IsExceedingWarning |> tagSim
            mkBoolProp   "IsBottleneck"                 k.IsBottleneck |> tagSim
            mkProp       "BottleneckSeverity"           k.BottleneckSeverity |> tagSim
            mkDoubleProp "UtilizationRate_pct"          k.UtilizationRate_pct |> tagSim
            mkDoubleProp "ImprovementPotential_pct"     k.ImprovementPotential_pct |> tagSim
            mkDoubleProp "RecommendedTargetCT_s"        k.RecommendedTargetCT_s |> tagSim
            mkDoubleProp "IdleGapBetweenCycles_s"       k.IdleGapBetweenCycles_s |> tagSim
            mkDoubleProp "EfficiencyRate_pct"           k.EfficiencyRate_pct |> tagSim
        ]

    /// Active 시스템에 속한 entity 이름(work/call/flow/system 등) 의 단일 진실 set.
    /// passive 시스템 의 KPI 항목을 필터링할 때 사용.
    let mutable private activeNameSet : HashSet<string> = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    /// 빈 set 또는 미초기화 시 모든 항목 통과(레거시 동작 보존).
    let private isActiveName (name: string) : bool =
        if String.IsNullOrEmpty name || activeNameSet.Count = 0 then true
        else
            // 정확 매치 → fast path.
            if activeNameSet.Contains name then true
            else
                // resource name 이 "{a}.{b}" 형태일 때 분해해 후보 매칭.
                // 어느 부분 이름이라도 active set 에 있으면 active 로 간주.
                let parts = name.Split([| '.'; '/'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                parts |> Array.exists activeNameSet.Contains

    /// 호출 측이 export 시작 시 한 번 호출 — active 시스템의 모든 하위 이름 수집.
    let setActiveContext (store: DsStore) (project: Project) : unit =
        let s = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for sys in Queries.activeSystemsOf project.Id store do
            s.Add sys.Name |> ignore
            for flow in Queries.flowsOf sys.Id store do
                s.Add flow.Name |> ignore
                s.Add (sprintf "%s_%s" sys.Name flow.Name) |> ignore
                s.Add (sprintf "%s.%s"  sys.Name flow.Name) |> ignore
                for work in Queries.worksOf flow.Id store do
                    s.Add work.Name |> ignore
                    s.Add (sprintf "%s.%s" flow.Name work.Name) |> ignore
                    s.Add (sprintf "%s_%s.%s" sys.Name flow.Name work.Name) |> ignore
                    for call in Queries.callsOf work.Id store do
                        s.Add call.Name |> ignore
                        s.Add (sprintf "%s.%s" work.Name call.Name) |> ignore
                        s.Add (sprintf "%s.%s" flow.Name call.Name) |> ignore
                        s.Add (sprintf "%s_%s.%s" sys.Name flow.Name call.Name) |> ignore
        activeNameSet <- s

    let clearActiveContext () : unit =
        activeNameSet <- HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let private cycleTimesToSmcGroup (items: ResizeArray<KpiCycleTime>) : ISubmodelElement option =
        let filtered = items |> Seq.filter (fun k -> isActiveName k.WorkName) |> Seq.toList
        if filtered.IsEmpty then None
        else
            let children = filtered |> List.map cycleTimeToSmc
            mkSml "KPI_CycleTime" children
            |> Option.map (withSem SimKpiCycleTimeSemanticId)

    let private callCycleTimeToSmc (k: KpiCallCycleTime) : ISubmodelElement =
        mkSmc "CallCycleTimeItem" [
            mkProp       "CallName"               k.CallName |> tagSim
            mkProp       "ParentWorkName"         k.ParentWorkName |> tagSim
            mkDoubleProp "ActualCycleTime_s"      k.ActualCycleTime_s |> tagSim
            mkDoubleProp "MinCycleTime_s"         k.MinCycleTime_s |> tagSim
            mkDoubleProp "MaxCycleTime_s"         k.MaxCycleTime_s |> tagSim
            mkIntProp    "CycleCount"             k.CycleCount |> tagSim
            mkDoubleProp "IdleGapBetweenCycles_s" k.IdleGapBetweenCycles_s |> tagSim
            mkDoubleProp "EfficiencyRate_pct"     k.EfficiencyRate_pct |> tagSim
        ]

    let private callCycleTimesToSmcGroup (items: ResizeArray<KpiCallCycleTime>) : ISubmodelElement option =
        let filtered =
            items
            |> Seq.filter (fun k -> isActiveName k.CallName || isActiveName k.ParentWorkName)
            |> Seq.toList
        if filtered.IsEmpty then None
        else
            let children = filtered |> List.map callCycleTimeToSmc
            mkSml "KPI_CallCycleTime" children

    let private throughputToSmc (t: KpiThroughput) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkProp       "StartTime"             (t.StartTime.ToString("O")) |> tagSim
            mkProp       "EndTime"               (t.EndTime.ToString("O"))   |> tagSim
            mkDoubleProp "ElapsedTime_s"         t.ElapsedTime_s |> tagSim
            mkIntProp    "TotalUnitsProduced"    t.TotalUnitsProduced |> tagSim
            mkDoubleProp "ThroughputPerHour"     t.ThroughputPerHour |> tagSim
            mkDoubleProp "ThroughputPerDay"      t.ThroughputPerDay |> tagSim
            mkDoubleProp "ThroughputPerWeek"     t.ThroughputPerWeek |> tagSim
            mkDoubleProp "ThroughputPerMonth"    t.ThroughputPerMonth |> tagSim
            mkDoubleProp "TaktTime_s"            t.TaktTime_s |> tagSim
            mkDoubleProp "AverageCycleTime_s"    t.AverageCycleTime_s |> tagSim
            mkDoubleProp "CycleTimeMargin_pct"   t.CycleTimeMargin_pct |> tagSim
            mkDoubleProp "TargetThroughput"      t.TargetThroughput |> tagSim
            mkDoubleProp "AchievementRate_pct"   t.AchievementRate_pct |> tagSim
            mkDoubleProp "EfficiencyRate_pct"    t.EfficiencyRate_pct |> tagSim
            mkProp       "HourlyThroughputJson"  t.HourlyThroughputJson |> tagSim
        ]
        withSem SimKpiThroughputSemanticId (mkSmc "KPI_Throughput" elems)

    let private capacityToSmc (c: KpiCapacity) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkProp       "AnalysisDate"             (c.AnalysisDate.ToString("O")) |> tagSim
            mkDoubleProp "DesignCapacity"           c.DesignCapacity |> tagSim
            mkDoubleProp "EffectiveCapacity"        c.EffectiveCapacity |> tagSim
            mkDoubleProp "ActualCapacity"           c.ActualCapacity |> tagSim
            mkDoubleProp "PlannedCapacity"          c.PlannedCapacity |> tagSim
            mkDoubleProp "DesignUtilization_pct"    c.DesignUtilization_pct |> tagSim
            mkDoubleProp "EffectiveUtilization_pct" c.EffectiveUtilization_pct |> tagSim
            mkDoubleProp "CapacityUtilization_pct"  c.CapacityUtilization_pct |> tagSim
            mkDoubleProp "CapacityGap"              c.CapacityGap |> tagSim
            yield! mkSmlProp "Bottlenecks"        (c.Bottlenecks        |> Seq.map (fun s -> mkProp "Item" s) |> Seq.toList) |> Option.toList
            yield! mkSmlProp "RecommendedActions" (c.RecommendedActions |> Seq.map (fun s -> mkProp "Item" s) |> Seq.toList) |> Option.toList
            mkProp "CapacityHorizon"  c.CapacityHorizon |> tagSim
            mkProp "CapacityStrategy" c.CapacityStrategy |> tagSim
        ]
        withSem SimKpiCapacitySemanticId (mkSmc "KPI_Capacity" elems)

    let private constraintItemToSmc (k: KpiConstraintItem) : ISubmodelElement =
        mkSmc "ConstraintItem" [
            mkProp       "ResourceName"            k.ResourceName |> tagSim
            mkProp       "ConstraintType"          k.ConstraintType |> tagSim
            mkDoubleProp "CurrentLoad"             k.CurrentLoad |> tagSim
            mkDoubleProp "MaxCapacity"             k.MaxCapacity |> tagSim
            mkDoubleProp "RemainingCapacity"       k.RemainingCapacity |> tagSim
            mkBoolProp   "IsConstraining"          k.IsConstraining |> tagSim
            mkProp       "Severity"                k.Severity |> tagSim
            mkDoubleProp "ImpactOnThroughput"      k.ImpactOnThroughput |> tagSim
            mkDoubleProp "EstimatedGainIfResolved" k.EstimatedGainIfResolved |> tagSim
            mkProp       "CurrentTocStep"          k.CurrentTocStep |> tagSim
            yield! mkSmlProp "RecommendedActions" (k.RecommendedActions |> Seq.map (fun s -> mkProp "Item" s) |> Seq.toList) |> Option.toList
        ]

    let private constraintsToSmcGroup (items: ResizeArray<KpiConstraintItem>) : ISubmodelElement option =
        if items.Count = 0 then None
        else
            mkSml "KPI_Constraints" (items |> Seq.map constraintItemToSmc |> Seq.toList)
            |> Option.map (withSem SimKpiConstraintsSemanticId)

    let private resourceItemToSmc (k: KpiResourceItem) : ISubmodelElement =
        mkSmc "ResourceItem" [
            mkProp       "ResourceName"           k.ResourceName |> tagSim
            mkProp       "ResourceType"           k.ResourceType |> tagSim
            mkDoubleProp "AvailableTime_s"        k.AvailableTime_s |> tagSim
            mkDoubleProp "UsedTime_s"             k.UsedTime_s |> tagSim
            mkDoubleProp "ProductionTime_s"       k.ProductionTime_s |> tagSim
            mkDoubleProp "ChangeoverTime_s"       k.ChangeoverTime_s |> tagSim
            mkDoubleProp "IdleTime_s"             k.IdleTime_s |> tagSim
            mkDoubleProp "DownTime_s"             k.DownTime_s |> tagSim
            mkDoubleProp "UtilizationRate_pct"    k.UtilizationRate_pct |> tagSim
            mkDoubleProp "ProductiveRate_pct"     k.ProductiveRate_pct |> tagSim
            mkDoubleProp "AvailabilityRate_pct"   k.AvailabilityRate_pct |> tagSim
            mkDoubleProp "IdleRate_pct"           k.IdleRate_pct |> tagSim
            mkDoubleProp "DownRate_pct"           k.DownRate_pct |> tagSim
            mkDoubleProp "IndustryBenchmark"      k.IndustryBenchmark |> tagSim
            mkDoubleProp "TargetUtilization"      k.TargetUtilization |> tagSim
            mkDoubleProp "PerformanceGap"         k.PerformanceGap |> tagSim
        ]

    let private resourcesToSmcGroup (items: ResizeArray<KpiResourceItem>) : ISubmodelElement option =
        let filtered = items |> Seq.filter (fun k -> isActiveName k.ResourceName) |> Seq.toList
        if filtered.IsEmpty then None
        else
            mkSml "KPI_ResourceUtilization" (filtered |> List.map resourceItemToSmc)
            |> Option.map (withSem SimKpiResourceUtilSemanticId)

    let private oeeItemToSmc (k: KpiOeeItem) : ISubmodelElement =
        mkSmc "OeeItem" [
            mkProp       "ResourceName"           k.ResourceName |> tagSim
            mkProp       "CalculationDate"        (k.CalculationDate.ToString("O")) |> tagSim
            mkDoubleProp "CalculationPeriod_s"    k.CalculationPeriod_s |> tagSim
            mkDoubleProp "Availability"           k.Availability |> tagSim
            mkDoubleProp "Performance"            k.Performance |> tagSim
            mkDoubleProp "Quality"                k.Quality |> tagSim
            mkDoubleProp "OEE"                    k.OEE |> tagSim
            mkDoubleProp "PlannedOperatingTime_s" k.PlannedOperatingTime_s |> tagSim
            mkDoubleProp "ActualOperatingTime_s"  k.ActualOperatingTime_s |> tagSim
            mkIntProp    "PlannedProductionQty"   k.PlannedProductionQty |> tagSim
            mkIntProp    "ActualProductionQty"    k.ActualProductionQty |> tagSim
            mkIntProp    "GoodProductQty"         k.GoodProductQty |> tagSim
            mkIntProp    "DefectQty"              k.DefectQty |> tagSim
            mkDoubleProp "TimeLoss_pct"           k.TimeLoss_pct |> tagSim
            mkDoubleProp "SpeedLoss_pct"          k.SpeedLoss_pct |> tagSim
            mkDoubleProp "QualityLoss_pct"        k.QualityLoss_pct |> tagSim
            mkDoubleProp "TargetOEE"              k.TargetOEE |> tagSim
            mkDoubleProp "OeeGap"                 k.OeeGap |> tagSim
            mkProp       "OeeClass"               k.OeeClass |> tagSim
        ]

    let private oeeToSmcGroup (items: ResizeArray<KpiOeeItem>) : ISubmodelElement option =
        let filtered = items |> Seq.filter (fun k -> isActiveName k.ResourceName) |> Seq.toList
        if filtered.IsEmpty then None
        else
            mkSml "KPI_OEE" (filtered |> List.map oeeItemToSmc)
            |> Option.map (withSem SimKpiOeeSemanticId)

    let private perTokenWorkBreakdownToSmc (b: KpiPerTokenWorkBreakdown) : ISubmodelElement =
        mkSmc "WorkBreakdownItem" [
            mkProp       "WorkName"          b.WorkName |> tagSim
            mkIntProp    "VisitCount"        b.VisitCount |> tagSim
            mkDoubleProp "AvgGoingTime_s"    b.AvgGoingTime_s |> tagSim
            mkDoubleProp "AvgFinishTime_s"   b.AvgFinishTime_s |> tagSim
        ]

    let private perTokenItemToSmc (k: KpiPerToken) : ISubmodelElement =
        let baseElems : ISubmodelElement list = [
            mkProp       "OriginName"             k.OriginName |> tagSim
            mkProp       "SpecLabel"              k.SpecLabel |> tagSim
            mkIntProp    "InstanceCount"          k.InstanceCount |> tagSim
            mkIntProp    "CompletedCount"         k.CompletedCount |> tagSim
            mkDoubleProp "AvgTraversalTime_s"     k.AvgTraversalTime_s |> tagSim
            mkDoubleProp "MinTraversalTime_s"     k.MinTraversalTime_s |> tagSim
            mkDoubleProp "MaxTraversalTime_s"     k.MaxTraversalTime_s |> tagSim
            mkDoubleProp "StdDevTraversalTime_s"  k.StdDevTraversalTime_s |> tagSim
            mkDoubleProp "ThroughputPerHour"      k.ThroughputPerHour |> tagSim
            mkProp       "FirstSeed"              (k.FirstSeed.ToString("O")) |> tagSim
            mkProp       "LastComplete"           (k.LastComplete.ToString("O")) |> tagSim
            yield! mkSml "WorkBreakdown" (k.WorkBreakdown |> Seq.map perTokenWorkBreakdownToSmc |> Seq.toList) |> Option.toList
        ]
        mkSmc "PerTokenItem" baseElems

    let private perTokensToSmcGroup (items: ResizeArray<KpiPerToken>) : ISubmodelElement option =
        let filtered = items |> Seq.filter (fun k -> isActiveName k.OriginName) |> Seq.toList
        if filtered.IsEmpty then None
        else
            mkSml "KPI_PerToken" (filtered |> List.map perTokenItemToSmc)
            |> Option.map (withSem SimKpiPerTokenSemanticId)

    /// SimulationResult SMC — Project.SimulationResult 를 SequenceSimulation 서브모델 안에 emit.
    /// 호출 측은 SequenceSimulation export 시 본 함수를 사용해 결과 SMC 를 SM elements 에 추가.
    let simulationResultToSmcOpt (resultOpt: SimulationScenario option) : ISubmodelElement option =
        match resultOpt with
        | None -> None
        | Some s ->
            let elems : ISubmodelElement list = [
                simMetaToSmc s.Meta
                yield! cycleTimesToSmcGroup s.CycleTimes |> Option.toList
                yield! callCycleTimesToSmcGroup s.CallCycleTimes |> Option.toList
                yield! (s.Throughput |> Option.map throughputToSmc |> Option.toList)
                yield! (s.Capacity   |> Option.map capacityToSmc   |> Option.toList)
                yield! constraintsToSmcGroup s.Constraints |> Option.toList
                yield! resourcesToSmcGroup   s.ResourceUtilizations |> Option.toList
                yield! oeeToSmcGroup         s.OeeItems |> Option.toList
                yield! perTokensToSmcGroup   s.PerTokenKpis |> Option.toList
            ]
            Some (withSem SimulationResultSemanticId (mkSmc "SimulationResult" elems))

    // ── Submodel 진입점 — Template-driven (IDTA 02003-2-0) ─────────────────
    /// 임베디드 TechnicalData.aasx 템플릿이 GeneralInformation / ProductClassifications /
    /// TechnicalPropertyAreas / FurtherInformation / SpecificDescriptions 구조를 정의.
    /// ds2 TechnicalData 의 표준 3 블록(GeneralInformation/ProductClassifications/FurtherInformation)
    /// 만 inject. SimulationResult 등 도메인 데이터는 SequenceSimulation SM 으로 분리됨.
    ///
    /// v1.x → v2.0 idShort 차이 매핑:
    ///   ds2.GI.ManufacturerName → "ManufacturerName" (Property in v2, was MLP in v1)
    ///   ds2.GI.ManufacturerProductDesignation → "ManufacturerProductDesignation" (MLP)
    ///   ds2.GI.ManufacturerArticleNumber → "ManufacturerArticleNumber"
    ///   ds2.GI.ManufacturerOrderCode → "ManufacturerOrderCode"
    ///   ds2.PC[i].ClassificationSystem → "ClassificationSystem"
    ///   ds2.PC[i].ClassificationVersion → "ClassificationSystemVersion"
    ///   ds2.PC[i].ProductClassId → "ProductClassId"
    ///   ds2.FI.TextStatement → "TextStatement" (MLP)
    ///   ds2.FI.ValidDate → "ValidDate"
    let technicalDataToSubmodel (td: TechnicalData) (projectId: Guid) : Submodel =
        let sm =
            match AasxTemplateLoader.tryLoadSubmodel
                    AasxTemplateLoader.TechnicalDataResource TechnicalDataSubmodelIdShort with
            | Some sm -> sm :?> Submodel
            | None ->
                failwith "TechnicalData.aasx 템플릿을 임베디드 리소스에서 로드할 수 없습니다 (Concepts/Templates/TechnicalData.aasx)"

        AasxTemplateScaffold.assignInstanceId sm $"urn:dualsoft:technicaldata:{projectId}"

        // ── GeneralInformation ────────────────────────────────────────────
        let gi = td.GeneralInformation
        AasxTemplateScaffold.setProp sm "GeneralInformation/ManufacturerName" gi.ManufacturerName |> ignore
        AasxTemplateScaffold.setMlpEn sm "GeneralInformation/ManufacturerProductDesignation" gi.ManufacturerProductDesignation |> ignore
        AasxTemplateScaffold.setProp sm "GeneralInformation/ManufacturerArticleNumber" gi.ManufacturerArticleNumber |> ignore
        AasxTemplateScaffold.setProp sm "GeneralInformation/ManufacturerOrderCode" gi.ManufacturerOrderCode |> ignore

        // ── ProductClassifications SML ────────────────────────────────────
        if td.ProductClassifications.Count > 0 then
            AasxTemplateScaffold.expandSml sm "ProductClassifications" td.ProductClassifications.Count (Some "ProductClassification") |> ignore
            td.ProductClassifications |> Seq.iteri (fun i c ->
                let p = sprintf "ProductClassifications/ProductClassification%02d" i
                AasxTemplateScaffold.setProp sm (p + "/ClassificationSystem") c.ClassificationSystem |> ignore
                AasxTemplateScaffold.setProp sm (p + "/ClassificationSystemVersion") c.ClassificationVersion |> ignore
                AasxTemplateScaffold.setProp sm (p + "/ProductClassId") c.ProductClassId |> ignore)

        // ── FurtherInformation ────────────────────────────────────────────
        let fi = td.FurtherInformation
        AasxTemplateScaffold.setMlpEn sm "FurtherInformation/TextStatement" fi.TextStatement |> ignore
        AasxTemplateScaffold.setProp sm "FurtherInformation/ValidDate" fi.ValidDate |> ignore

        sm
