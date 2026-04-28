namespace Ds2.Aasx

open System
open AasCore.Aas3_1
open Ds2.Core
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

    // ── GeneralInformation ────────────────────────────────────────────────
    let private generalInfoToSmc (gi: TdGeneralInformation) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkMlp  "ManufacturerName"               gi.ManufacturerName
            mkMlp  "ManufacturerProductDesignation" gi.ManufacturerProductDesignation
            mkProp "ManufacturerArticleNumber"      gi.ManufacturerArticleNumber
            mkProp "ManufacturerOrderCode"          gi.ManufacturerOrderCode
            yield! mkSmlProp "ProductImages" (gi.ProductImages |> Seq.map (fun s -> mkProp "ProductImage" s) |> Seq.toList) |> Option.toList
        ]
        mkSmc "GeneralInformation" elems

    // ── ProductClassifications ────────────────────────────────────────────
    let private classificationItemToSmc (c: TdProductClassificationItem) : ISubmodelElement =
        mkSmc "ProductClassificationItem" [
            mkProp "ProductClassificationSystem" c.ClassificationSystem
            mkProp "ClassificationSystemVersion" c.ClassificationVersion
            mkProp "ProductClassId"              c.ProductClassId
        ]

    let private classificationsToSmc (items: ResizeArray<TdProductClassificationItem>) : ISubmodelElement =
        let children = items |> Seq.map classificationItemToSmc |> Seq.toList
        if children.IsEmpty then
            mkSmc "ProductClassifications" []
        else
            match mkSml "ProductClassifications" children with
            | Some sml -> sml
            | None -> mkSmc "ProductClassifications" []

    // ── TechnicalProperties — 도메인 그룹 ─────────────────────────────────
    let private sequenceCharacteristicsToSmc (s: TdSequenceCharacteristics) : ISubmodelElement =
        mkSmc "SequenceCharacteristics" [
            mkProp      "SequenceName"        s.SequenceName
            mkProp      "SequenceVersion"     s.SequenceVersion
            mkDoubleProp "CycleTimeNominal_s" s.CycleTimeNominal_s
            mkDoubleProp "CycleTimeMin_s"     s.CycleTimeMin_s
            mkDoubleProp "CycleTimeMax_s"     s.CycleTimeMax_s
            mkIntProp   "StepCount"           s.StepCount
            mkIntProp   "ParallelBranchCount" s.ParallelBranchCount
            mkProp      "Ds2ModelHash"        s.Ds2ModelHash
            mkProp      "SafetyCategory"      s.SafetyCategory
        ]

    let private ioCharacteristicsToSmc (io: TdIoCharacteristics) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkIntProp "DigitalInputCount"  io.DigitalInputCount
            mkIntProp "DigitalOutputCount" io.DigitalOutputCount
            mkIntProp "AnalogInputCount"   io.AnalogInputCount
            mkIntProp "AnalogOutputCount"  io.AnalogOutputCount
            yield! mkSmlProp "FieldbusProtocols" (io.FieldbusProtocols |> Seq.map (fun p -> mkProp "Protocol" p) |> Seq.toList) |> Option.toList
            mkIntProp "ScanCycle_ms"       io.ScanCycle_ms
        ]
        mkSmc "IOCharacteristics" elems

    let private apiSurfaceToSmc (a: TdApiSurface) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkIntProp "ApiCallCount" a.ApiCallCount
            yield! mkSmlProp "ExposedActions"         (a.ExposedActions         |> Seq.map (fun s -> mkProp "Action" s)   |> Seq.toList) |> Option.toList
            yield! mkSmlProp "ExposedReadProperties"  (a.ExposedReadProperties  |> Seq.map (fun s -> mkProp "ReadProperty" s)  |> Seq.toList) |> Option.toList
            yield! mkSmlProp "ExposedWriteProperties" (a.ExposedWriteProperties |> Seq.map (fun s -> mkProp "WriteProperty" s) |> Seq.toList) |> Option.toList
        ]
        mkSmc "ApiSurface" elems

    let private controllerInfoToSmc (c: TdControllerInfo) : ISubmodelElement =
        mkSmc "ControllerInfo" [
            mkProp "ControllerVendor" c.ControllerVendor
            mkProp "ControllerModel"  c.ControllerModel
            mkProp "FirmwareVersion"  c.FirmwareVersion
            mkProp "EngineeringTool"  c.EngineeringTool
        ]

    // ── SimulationResults — 시뮬결과 박제 ─────────────────────────────────
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
        ]

    let private cycleTimesToSmcGroup (items: ResizeArray<KpiCycleTime>) : ISubmodelElement option =
        if items.Count = 0 then None
        else
            let children = items |> Seq.map cycleTimeToSmc |> Seq.toList
            mkSml "KPI_CycleTime" children
            |> Option.map (withSem SimKpiCycleTimeSemanticId)

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
        if items.Count = 0 then None
        else
            mkSml "KPI_ResourceUtilization" (items |> Seq.map resourceItemToSmc |> Seq.toList)
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
        if items.Count = 0 then None
        else
            mkSml "KPI_OEE" (items |> Seq.map oeeItemToSmc |> Seq.toList)
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
        if items.Count = 0 then None
        else
            mkSml "KPI_PerToken" (items |> Seq.map perTokenItemToSmc |> Seq.toList)
            |> Option.map (withSem SimKpiPerTokenSemanticId)

    /// 단일 SimulationResult SMC (없으면 None)
    let private simulationResultToSmcOpt (resultOpt: SimulationScenario option) : ISubmodelElement option =
        match resultOpt with
        | None -> None
        | Some s ->
            let elems : ISubmodelElement list = [
                simMetaToSmc s.Meta
                yield! cycleTimesToSmcGroup s.CycleTimes |> Option.toList
                yield! (s.Throughput |> Option.map throughputToSmc |> Option.toList)
                yield! (s.Capacity   |> Option.map capacityToSmc   |> Option.toList)
                yield! constraintsToSmcGroup s.Constraints |> Option.toList
                yield! resourcesToSmcGroup   s.ResourceUtilizations |> Option.toList
                yield! oeeToSmcGroup         s.OeeItems |> Option.toList
                yield! perTokensToSmcGroup   s.PerTokenKpis |> Option.toList
            ]
            Some (withSem SimulationResultSemanticId (mkSmc "SimulationResult" elems))

    // ── TechnicalProperties 컨테이너 ──────────────────────────────────────
    let private technicalPropertiesToSmc (td: TechnicalData) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            sequenceCharacteristicsToSmc td.SequenceCharacteristics
            ioCharacteristicsToSmc       td.IoCharacteristics
            apiSurfaceToSmc              td.ApiSurface
            controllerInfoToSmc          td.ControllerInfo
            yield! simulationResultToSmcOpt td.SimulationResult |> Option.toList
        ]
        mkSmc "TechnicalProperties" elems

    // ── FurtherInformation ────────────────────────────────────────────────
    let private furtherInfoToSmc (fi: TdFurtherInformation) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            mkMlp  "TextStatement" fi.TextStatement
            mkProp "ValidDate"     fi.ValidDate
            yield! mkSmlProp "ReferenceDocuments" (fi.ReferenceDocuments |> Seq.map (fun s -> mkProp "Document" s) |> Seq.toList) |> Option.toList
        ]
        mkSmc "FurtherInformation" elems

    // ── Submodel 진입점 ───────────────────────────────────────────────────
    let technicalDataToSubmodel (td: TechnicalData) (projectId: Guid) : Submodel =
        let elems : ISubmodelElement list = [
            generalInfoToSmc       td.GeneralInformation
            classificationsToSmc   td.ProductClassifications
            technicalPropertiesToSmc td
            furtherInfoToSmc       td.FurtherInformation
        ]
        mkSubmodel
            $"urn:dualsoft:technicaldata:{projectId}"
            TechnicalDataSubmodelIdShort
            TechnicalDataSemanticId
            elems
