namespace Ds2.Core

module StandardMcpNames =

    let [<Literal>] ToolSensorList = "maintenance.sensor.list"
    let [<Literal>] ToolSensorGet = "maintenance.sensor.get"
    let [<Literal>] ToolSensorUpsert = "maintenance.sensor.upsert"
    let [<Literal>] ToolSensorDelete = "maintenance.sensor.delete"

    let [<Literal>] ToolThresholdList = "maintenance.threshold.list"
    let [<Literal>] ToolThresholdUpsert = "maintenance.threshold.upsert"
    let [<Literal>] ToolThresholdDelete = "maintenance.threshold.delete"

    let [<Literal>] ToolEquipmentList = "maintenance.equipment.list"
    let [<Literal>] ToolEquipmentUpsert = "maintenance.equipment.upsert"
    let [<Literal>] ToolLifecycleUpsert = "maintenance.lifecycle.upsert"
    let [<Literal>] ToolSparePartUpsert = "maintenance.sparepart.upsert"

    let [<Literal>] ToolRuleList = "maintenance.rule.list"
    let [<Literal>] ToolRuleUpsert = "maintenance.rule.upsert"
    let [<Literal>] ToolRuleDelete = "maintenance.rule.delete"
    let [<Literal>] ToolScheduleUpsert = "maintenance.schedule.upsert"
    let [<Literal>] ToolErrorTrackUpsert = "maintenance.errortrack.upsert"

    let [<Literal>] ToolSamplingPolicyUpsert = "maintenance.sampling.upsert"
    let [<Literal>] ToolActuatorLogAppend = "maintenance.log.actuator.append"
    let [<Literal>] ToolActuatorLogQuery = "maintenance.log.actuator.query"
    let [<Literal>] ToolAnalogLogAppend = "maintenance.log.analog.append"
    let [<Literal>] ToolAnalogLogQuery = "maintenance.log.analog.query"
    let [<Literal>] ToolFailureReport = "maintenance.event.failure.report"
    let [<Literal>] ToolFailureUpdate = "maintenance.event.failure.update"
    let [<Literal>] ToolReplacementReport = "maintenance.event.replacement.report"

    let [<Literal>] ToolAnalysisReliability = "maintenance.analysis.reliability"
    let [<Literal>] ToolAnalysisEol = "maintenance.analysis.eol"
    let [<Literal>] ToolAnalysisAnomaly = "maintenance.analysis.anomaly"
    let [<Literal>] ToolAnalysisFailureTrend = "maintenance.analysis.failuretrend"
    let [<Literal>] ToolAnalysisFailurePredict = "maintenance.analysis.failurepredict"
    let [<Literal>] ToolAnalysisSpareStockRisk = "maintenance.analysis.sparestockrisk"

    let [<Literal>] ToolPlanReplacement = "maintenance.plan.replacement"
    let [<Literal>] ToolPlanWorkOrder = "maintenance.plan.workorder"
    let [<Literal>] ToolPlanScheduleOptimize = "maintenance.plan.scheduleoptimize"
    let [<Literal>] ToolPlanThresholdTune = "maintenance.plan.thresholdtune"

    let [<Literal>] ToolRegistryValidate = "maintenance.registry.validate"
    let [<Literal>] ToolChangeRequestSubmit = "maintenance.change.submit"
    let [<Literal>] ToolChangeRequestApprove = "maintenance.change.approve"
    let [<Literal>] ToolAuditList = "maintenance.audit.list"

    let [<Literal>] ResSensors = "ds2://maintenance/sensors"
    let [<Literal>] ResThresholds = "ds2://maintenance/thresholds"
    let [<Literal>] ResEquipments = "ds2://maintenance/equipments"
    let [<Literal>] ResLifecycles = "ds2://maintenance/lifecycles"
    let [<Literal>] ResSpareParts = "ds2://maintenance/spareparts"
    let [<Literal>] ResRules = "ds2://maintenance/rules"
    let [<Literal>] ResSchedules = "ds2://maintenance/schedules"
    let [<Literal>] ResErrorTrackings = "ds2://maintenance/errortrackings"
    let [<Literal>] ResChangeRequests = "ds2://maintenance/changerequests"
    let [<Literal>] ResAuditLog = "ds2://maintenance/auditlog"
    let [<Literal>] ResReplacementProposals = "ds2://maintenance/replacementproposals"
    let [<Literal>] ResWorkOrderProposals = "ds2://maintenance/workorderproposals"
    let [<Literal>] ResSamplingPolicies = "ds2://maintenance/samplingpolicies"
    let [<Literal>] ResActuatorLog = "ds2://maintenance/log/actuator"
    let [<Literal>] ResAnalogLog = "ds2://maintenance/log/analog"
    let [<Literal>] ResFailureLog = "ds2://maintenance/log/failure"
    let [<Literal>] ResReplacementLog = "ds2://maintenance/log/replacement"

    let [<Literal>] PromptDiagnoseEquipment = "diagnose-equipment"
    let [<Literal>] PromptSuggestRule = "suggest-maintenance-rule"
    let [<Literal>] PromptExplainThreshold = "explain-threshold"
    let [<Literal>] PromptReviewChangeRequest = "review-change-request"
    let [<Literal>] PromptGenerateReport = "generate-maintenance-report"
    let [<Literal>] PromptAnalyzeReliability = "analyze-reliability"
    let [<Literal>] PromptPredictEol = "predict-eol"
    let [<Literal>] PromptProposeReplacement = "propose-replacement-plan"
    let [<Literal>] PromptTuneThreshold = "tune-threshold"
    let [<Literal>] PromptReportFailure = "report-failure-intake"
    let [<Literal>] PromptReportReplacement = "report-replacement-intake"
    let [<Literal>] PromptPredictFailure = "predict-failure-from-history"

module MaintenanceRegistry =

    let inline private findFirst (pred: 'T -> bool) (items: ResizeArray<'T>) =
        let mutable result: 'T option = None
        let mutable i = 0
        while result.IsNone && i < items.Count do
            let x = items.[i]
            if pred x then result <- Some x
            i <- i + 1
        result

    let findSensor (sys: MaintenanceSystemProperties) id =
        findFirst (fun (s: SensorSpec) -> s.Id = id) sys.Sensors

    let findEquipment (sys: MaintenanceSystemProperties) id =
        findFirst (fun (e: EquipmentSpec) -> e.Id = id) sys.Equipments

    let findLifecycleByEquipment (sys: MaintenanceSystemProperties) equipmentId =
        findFirst (fun (l: LifecycleSpec) -> l.EquipmentId = equipmentId) sys.Lifecycles

    let findThreshold (sys: MaintenanceSystemProperties) id =
        findFirst (fun (t: SensorThresholdSpec) -> t.Id = id) sys.Thresholds

    let findThresholdsForSensor (sys: MaintenanceSystemProperties) sensorId =
        sys.Thresholds
        |> Seq.filter (fun (t: SensorThresholdSpec) -> t.SensorId = sensorId)
        |> Seq.toArray

    let findRule (sys: MaintenanceSystemProperties) id =
        findFirst (fun (r: MaintenanceRuleSpec) -> r.Id = id) sys.Rules

    let findActiveRulesForEquipment (sys: MaintenanceSystemProperties) equipmentId =
        sys.Rules
        |> Seq.filter (fun (r: MaintenanceRuleSpec) -> r.Enabled && r.EquipmentId = equipmentId)
        |> Seq.toArray

    let findSparePartsForEquipment (sys: MaintenanceSystemProperties) equipmentId =
        sys.SpareParts
        |> Seq.filter (fun (p: SparePartSpec) -> p.CompatibleEquipmentIds |> Array.contains equipmentId)
        |> Seq.toArray

    let findMcpTool (sys: MaintenanceSystemProperties) toolName =
        findFirst (fun (t: McpToolSpec) -> t.ToolName = toolName) sys.McpTools

    let findMcpResource (sys: MaintenanceSystemProperties) uri =
        findFirst (fun (r: McpResourceSpec) -> r.Uri = uri) sys.McpResources

    let findMcpPrompt (sys: MaintenanceSystemProperties) name =
        findFirst (fun (p: McpPromptSpec) -> p.PromptName = name) sys.McpPrompts

    let findPendingChangeRequests (sys: MaintenanceSystemProperties) =
        sys.ChangeRequests
        |> Seq.filter (fun (c: ChangeRequestSpec) -> c.Status = PendingApproval)
        |> Seq.toArray

    let validate (sys: MaintenanceSystemProperties) =
        let errors = ResizeArray<string>()

        for t in sys.Thresholds do
            if (findSensor sys t.SensorId).IsNone then
                errors.Add(sprintf "Threshold '%s' references unknown SensorId '%s'" t.Id t.SensorId)

        for l in sys.Lifecycles do
            if (findEquipment sys l.EquipmentId).IsNone then
                errors.Add(sprintf "Lifecycle '%s' references unknown EquipmentId '%s'" l.Id l.EquipmentId)
            match l.IndicatorSensorId with
            | Some sid when (findSensor sys sid).IsNone ->
                errors.Add(sprintf "Lifecycle '%s' indicator sensor '%s' not found" l.Id sid)
            | _ -> ()

        for r in sys.Rules do
            if (findEquipment sys r.EquipmentId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown EquipmentId '%s'" r.Id r.EquipmentId)
            if (findSensor sys r.SensorId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown SensorId '%s'" r.Id r.SensorId)
            if r.ThresholdSpecId <> "" && (findThreshold sys r.ThresholdSpecId).IsNone then
                errors.Add(sprintf "Rule '%s' references unknown ThresholdSpecId '%s'" r.Id r.ThresholdSpecId)

        for s in sys.Schedules do
            if (findEquipment sys s.EquipmentId).IsNone then
                errors.Add(sprintf "Schedule '%s' references unknown EquipmentId '%s'" s.Id s.EquipmentId)

        for e in sys.ErrorTrackings do
            if e.EquipmentId <> "" && (findEquipment sys e.EquipmentId).IsNone then
                errors.Add(sprintf "ErrorTracking '%s' references unknown EquipmentId '%s'" e.Id e.EquipmentId)

        let checkDup name (ids: seq<string>) =
            ids
            |> Seq.groupBy id
            |> Seq.filter (fun (_, g) -> Seq.length g > 1)
            |> Seq.iter (fun (k, _) -> errors.Add(sprintf "Duplicate %s Id '%s'" name k))

        checkDup "Sensor" (sys.Sensors |> Seq.map (fun x -> x.Id))
        checkDup "Threshold" (sys.Thresholds |> Seq.map (fun x -> x.Id))
        checkDup "Equipment" (sys.Equipments |> Seq.map (fun x -> x.Id))
        checkDup "Lifecycle" (sys.Lifecycles |> Seq.map (fun x -> x.Id))
        checkDup "SparePart" (sys.SpareParts |> Seq.map (fun x -> x.Id))
        checkDup "Rule" (sys.Rules |> Seq.map (fun x -> x.Id))
        checkDup "Schedule" (sys.Schedules |> Seq.map (fun x -> x.Id))
        checkDup "ErrorTracking" (sys.ErrorTrackings |> Seq.map (fun x -> x.Id))
        checkDup "McpTool" (sys.McpTools |> Seq.map (fun x -> x.ToolName))
        checkDup "McpResource" (sys.McpResources |> Seq.map (fun x -> x.Uri))
        checkDup "McpPrompt" (sys.McpPrompts |> Seq.map (fun x -> x.PromptName))

        errors.ToArray()
