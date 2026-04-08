namespace Ds2.Aasx

open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxExportCore
open Ds2.Aasx.AasxImportCore

module PropertyConversion =

    // ── Record (non-generic, obj-free) ──────────────────────────────────────
    type PropertyOps = {
        GetSystemElems  : DsSystem -> ISubmodelElement list
        GetFlowElems    : Flow     -> ISubmodelElement list
        GetWorkElems    : Work     -> ISubmodelElement list
        GetCallElems    : Call     -> ISubmodelElement list
        SystemFromElems : SubmodelElementCollection -> SystemSubmodelProperty option
        FlowFromElems   : SubmodelElementCollection -> FlowSubmodelProperty   option
        WorkFromElems   : SubmodelElementCollection -> WorkSubmodelProperty   option
        CallFromElems   : SubmodelElementCollection -> CallSubmodelProperty   option
    }

    // ── 헬퍼: 4-튜플 × 4개 → PropertyOps ───────────────────────────────────
    let private mkOps
        (gS, sToE, sFrE, sTDU)
        (gF, fToE, fFrE, fTDU)
        (gW, wToE, wFrE, wTDU)
        (gC, cToE, cFrE, cTDU) = {
        GetSystemElems  = fun s -> gS s |> Option.map sToE |> Option.defaultValue []
        GetFlowElems    = fun f -> gF f |> Option.map fToE |> Option.defaultValue []
        GetWorkElems    = fun w -> gW w |> Option.map wToE |> Option.defaultValue []
        GetCallElems    = fun c -> gC c |> Option.map cToE |> Option.defaultValue []
        SystemFromElems = fun smc -> sFrE smc |> Option.map sTDU
        FlowFromElems   = fun smc -> fFrE smc |> Option.map fTDU
        WorkFromElems   = fun smc -> wFrE smc |> Option.map wTDU
        CallFromElems   = fun smc -> cFrE smc |> Option.map cTDU
    }

    // ── SubmodelType → PropertyOps ──────────────────────────────────────────
    let getPropertyOps = function
        | SequenceModel ->
            { GetSystemElems  = fun _ -> []
              GetFlowElems    = fun _ -> []
              GetWorkElems    = fun _ -> []
              GetCallElems    = fun _ -> []
              SystemFromElems = fun _ -> None
              FlowFromElems   = fun _ -> None
              WorkFromElems   = fun _ -> None
              CallFromElems   = fun _ -> None }

        | SequenceSimulation ->
            mkOps
                ((fun s -> s.GetSimulationProperties()), simulationSystemPropsToElements, elementsToProps<SimulationSystemProperties>, SimulationSystem)
                ((fun f -> f.GetSimulationProperties()), simulationFlowPropsToElements,   elementsToProps<SimulationFlowProperties>,   SimulationFlow)
                ((fun w -> w.GetSimulationProperties()), simulationWorkPropsToElements,   elementsToProps<SimulationWorkProperties>,   SimulationWork)
                ((fun c -> c.GetSimulationProperties()), simulationCallPropsToElements,   elementsToProps<SimulationCallProperties>,   SimulationCall)

        | SequenceControl ->
            mkOps
                ((fun s -> s.GetControlProperties()), controlSystemPropsToElements, elementsToProps<ControlSystemProperties>, ControlSystem)
                ((fun f -> f.GetControlProperties()), controlFlowPropsToElements,   elementsToProps<ControlFlowProperties>,   ControlFlow)
                ((fun w -> w.GetControlProperties()), controlWorkPropsToElements,   elementsToProps<ControlWorkProperties>,   ControlWork)
                ((fun c -> c.GetControlProperties()), controlCallPropsToElements,   elementsToProps<ControlCallProperties>,   ControlCall)

        | SequenceMonitoring ->
            mkOps
                ((fun s -> s.GetMonitoringProperties()), monitoringSystemPropsToElements, elementsToProps<MonitoringSystemProperties>, MonitoringSystem)
                ((fun f -> f.GetMonitoringProperties()), monitoringFlowPropsToElements,   elementsToProps<MonitoringFlowProperties>,   MonitoringFlow)
                ((fun w -> w.GetMonitoringProperties()), monitoringWorkPropsToElements,   elementsToProps<MonitoringWorkProperties>,   MonitoringWork)
                ((fun c -> c.GetMonitoringProperties()), monitoringCallPropsToElements,   elementsToProps<MonitoringCallProperties>,   MonitoringCall)

        | SequenceLogging ->
            mkOps
                ((fun s -> s.GetLoggingProperties()), loggingSystemPropsToElements, elementsToProps<LoggingSystemProperties>, LoggingSystem)
                ((fun f -> f.GetLoggingProperties()), loggingFlowPropsToElements,   elementsToProps<LoggingFlowProperties>,   LoggingFlow)
                ((fun w -> w.GetLoggingProperties()), loggingWorkPropsToElements,   elementsToProps<LoggingWorkProperties>,   LoggingWork)
                ((fun c -> c.GetLoggingProperties()), loggingCallPropsToElements,   elementsToProps<LoggingCallProperties>,   LoggingCall)

        | SequenceMaintenance ->
            mkOps
                ((fun s -> s.GetMaintenanceProperties()), maintenanceSystemPropsToElements, elementsToProps<MaintenanceSystemProperties>, MaintenanceSystem)
                ((fun f -> f.GetMaintenanceProperties()), maintenanceFlowPropsToElements,   elementsToProps<MaintenanceFlowProperties>,   MaintenanceFlow)
                ((fun w -> w.GetMaintenanceProperties()), maintenanceWorkPropsToElements,   elementsToProps<MaintenanceWorkProperties>,   MaintenanceWork)
                ((fun c -> c.GetMaintenanceProperties()), maintenanceCallPropsToElements,   elementsToProps<MaintenanceCallProperties>,   MaintenanceCall)

        | SequenceHmi ->
            mkOps
                ((fun s -> s.GetHMIProperties()), hmiSystemPropsToElements, elementsToProps<HMISystemProperties>, HmiSystem)
                ((fun f -> f.GetHMIProperties()), hmiFlowPropsToElements,   elementsToProps<HMIFlowProperties>,   HmiFlow)
                ((fun w -> w.GetHMIProperties()), hmiWorkPropsToElements,   elementsToProps<HMIWorkProperties>,   HmiWork)
                ((fun c -> c.GetHMIProperties()), hmiCallPropsToElements,   elementsToProps<HMICallProperties>,   HmiCall)

        | SequenceQuality ->
            mkOps
                ((fun s -> s.GetQualityProperties()), qualitySystemPropsToElements, elementsToProps<QualitySystemProperties>, QualitySystem)
                ((fun f -> f.GetQualityProperties()), qualityFlowPropsToElements,   elementsToProps<QualityFlowProperties>,   QualityFlow)
                ((fun w -> w.GetQualityProperties()), qualityWorkPropsToElements,   elementsToProps<QualityWorkProperties>,   QualityWork)
                ((fun c -> c.GetQualityProperties()), qualityCallPropsToElements,   elementsToProps<QualityCallProperties>,   QualityCall)

        | SequenceCostAnalysis ->
            mkOps
                ((fun s -> s.GetCostAnalysisProperties()), costAnalysisSystemPropsToElements, elementsToProps<CostAnalysisSystemProperties>, CostAnalysisSystem)
                ((fun f -> f.GetCostAnalysisProperties()), costAnalysisFlowPropsToElements,   elementsToProps<CostAnalysisFlowProperties>,   CostAnalysisFlow)
                ((fun w -> w.GetCostAnalysisProperties()), costAnalysisWorkPropsToElements,   elementsToProps<CostAnalysisWorkProperties>,   CostAnalysisWork)
                ((fun c -> c.GetCostAnalysisProperties()), costAnalysisCallPropsToElements,   elementsToProps<CostAnalysisCallProperties>,   CostAnalysisCall)


    // ── Export ──────────────────────────────────────────────────────────────
    let getEntityElements (submodelType: SubmodelType) (entity: obj) =
        let ops = getPropertyOps submodelType
        match entity with
        | :? DsSystem as s -> ops.GetSystemElems s
        | :? Flow     as f -> ops.GetFlowElems   f
        | :? Work     as w -> ops.GetWorkElems   w
        | :? Call     as c -> ops.GetCallElems   c
        | _                -> []

    // ── Import ──────────────────────────────────────────────────────────────
    let importSystemProperty t smc (props: ResizeArray<_>) = (getPropertyOps t).SystemFromElems smc |> Option.iter props.Add
    let importFlowProperty   t smc (props: ResizeArray<_>) = (getPropertyOps t).FlowFromElems   smc |> Option.iter props.Add
    let importWorkProperty   t smc (props: ResizeArray<_>) = (getPropertyOps t).WorkFromElems   smc |> Option.iter props.Add
    let importCallProperty   t smc (props: ResizeArray<_>) = (getPropertyOps t).CallFromElems   smc |> Option.iter props.Add
