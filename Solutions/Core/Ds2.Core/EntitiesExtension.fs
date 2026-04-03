namespace Ds2.Core

open System.Runtime.CompilerServices

[<AutoOpen>]
module EntitiesExtension =

    let inline private tryGet (ps: ResizeArray<_>) picker =
        ps |> Seq.tryPick picker

    let inline private trySet (ps: ResizeArray<_>) picker wrap value =
        let idxs =
            ps |> Seq.mapi (fun i p -> i, p)
               |> Seq.choose (fun (i, p) -> if picker p |> Option.isSome then Some i else None)
               |> Seq.toArray
        match idxs with
        | [||] -> ps.Add(wrap value)
        | _    -> idxs |> Array.skip 1 |> Array.rev |> Array.iter ps.RemoveAt; ps[idxs[0]] <- wrap value

    // 각 엔티티별 get/set 쌍을 한 줄로 정의하는 인라인 헬퍼
    let inline private get ps picker       = tryGet ps (function p when (picker p |> Option.isSome) -> picker p | _ -> None)
    let inline internal g  (ps: ResizeArray<_>) f   = tryGet ps f
    let inline internal s  (ps: ResizeArray<_>) f w = trySet ps f w

[<Extension>]
type EntityCSharpExtensions =

        // DsSystem
        [<Extension>] static member GetSimulationProperties(x: DsSystem)        = g x.Properties (function SimulationSystem   p -> Some p | _ -> None)
        [<Extension>] static member SetSimulationProperties(x: DsSystem, v)     = s x.Properties (function SimulationSystem   p -> Some p | _ -> None) SimulationSystem v
        [<Extension>] static member GetControlProperties(x: DsSystem)           = g x.Properties (function ControlSystem      p -> Some p | _ -> None)
        [<Extension>] static member SetControlProperties(x: DsSystem, v)        = s x.Properties (function ControlSystem      p -> Some p | _ -> None) ControlSystem v
        [<Extension>] static member GetMonitoringProperties(x: DsSystem)        = g x.Properties (function MonitoringSystem   p -> Some p | _ -> None)
        [<Extension>] static member SetMonitoringProperties(x: DsSystem, v)     = s x.Properties (function MonitoringSystem   p -> Some p | _ -> None) MonitoringSystem v
        [<Extension>] static member GetLoggingProperties(x: DsSystem)           = g x.Properties (function LoggingSystem      p -> Some p | _ -> None)
        [<Extension>] static member SetLoggingProperties(x: DsSystem, v)        = s x.Properties (function LoggingSystem      p -> Some p | _ -> None) LoggingSystem v
        [<Extension>] static member GetMaintenanceProperties(x: DsSystem)       = g x.Properties (function MaintenanceSystem  p -> Some p | _ -> None)
        [<Extension>] static member SetMaintenanceProperties(x: DsSystem, v)    = s x.Properties (function MaintenanceSystem  p -> Some p | _ -> None) MaintenanceSystem v
        [<Extension>] static member GetCostAnalysisProperties(x: DsSystem)      = g x.Properties (function CostAnalysisSystem p -> Some p | _ -> None)
        [<Extension>] static member SetCostAnalysisProperties(x: DsSystem, v)   = s x.Properties (function CostAnalysisSystem p -> Some p | _ -> None) CostAnalysisSystem v

        // Flow
        [<Extension>] static member GetSimulationProperties(x: Flow)            = g x.Properties (function SimulationFlow   p -> Some p | _ -> None)
        [<Extension>] static member SetSimulationProperties(x: Flow, v)         = s x.Properties (function SimulationFlow   p -> Some p | _ -> None) SimulationFlow v
        [<Extension>] static member GetControlProperties(x: Flow)               = g x.Properties (function ControlFlow      p -> Some p | _ -> None)
        [<Extension>] static member SetControlProperties(x: Flow, v)            = s x.Properties (function ControlFlow      p -> Some p | _ -> None) ControlFlow v
        [<Extension>] static member GetMonitoringProperties(x: Flow)            = g x.Properties (function MonitoringFlow   p -> Some p | _ -> None)
        [<Extension>] static member SetMonitoringProperties(x: Flow, v)         = s x.Properties (function MonitoringFlow   p -> Some p | _ -> None) MonitoringFlow v
        [<Extension>] static member GetLoggingProperties(x: Flow)               = g x.Properties (function LoggingFlow      p -> Some p | _ -> None)
        [<Extension>] static member SetLoggingProperties(x: Flow, v)            = s x.Properties (function LoggingFlow      p -> Some p | _ -> None) LoggingFlow v
        [<Extension>] static member GetMaintenanceProperties(x: Flow)           = g x.Properties (function MaintenanceFlow  p -> Some p | _ -> None)
        [<Extension>] static member SetMaintenanceProperties(x: Flow, v)        = s x.Properties (function MaintenanceFlow  p -> Some p | _ -> None) MaintenanceFlow v
        [<Extension>] static member GetCostAnalysisProperties(x: Flow)          = g x.Properties (function CostAnalysisFlow p -> Some p | _ -> None)
        [<Extension>] static member SetCostAnalysisProperties(x: Flow, v)       = s x.Properties (function CostAnalysisFlow p -> Some p | _ -> None) CostAnalysisFlow v

        // Work
        [<Extension>] static member GetSimulationProperties(x: Work)            = g x.Properties (function SimulationWork   p -> Some p | _ -> None)
        [<Extension>] static member SetSimulationProperties(x: Work, v)         = s x.Properties (function SimulationWork   p -> Some p | _ -> None) SimulationWork v
        [<Extension>] static member GetControlProperties(x: Work)               = g x.Properties (function ControlWork      p -> Some p | _ -> None)
        [<Extension>] static member SetControlProperties(x: Work, v)            = s x.Properties (function ControlWork      p -> Some p | _ -> None) ControlWork v
        [<Extension>] static member GetMonitoringProperties(x: Work)            = g x.Properties (function MonitoringWork   p -> Some p | _ -> None)
        [<Extension>] static member SetMonitoringProperties(x: Work, v)         = s x.Properties (function MonitoringWork   p -> Some p | _ -> None) MonitoringWork v
        [<Extension>] static member GetLoggingProperties(x: Work)               = g x.Properties (function LoggingWork      p -> Some p | _ -> None)
        [<Extension>] static member SetLoggingProperties(x: Work, v)            = s x.Properties (function LoggingWork      p -> Some p | _ -> None) LoggingWork v
        [<Extension>] static member GetMaintenanceProperties(x: Work)           = g x.Properties (function MaintenanceWork  p -> Some p | _ -> None)
        [<Extension>] static member SetMaintenanceProperties(x: Work, v)        = s x.Properties (function MaintenanceWork  p -> Some p | _ -> None) MaintenanceWork v
        [<Extension>] static member GetCostAnalysisProperties(x: Work)          = g x.Properties (function CostAnalysisWork p -> Some p | _ -> None)
        [<Extension>] static member SetCostAnalysisProperties(x: Work, v)       = s x.Properties (function CostAnalysisWork p -> Some p | _ -> None) CostAnalysisWork v

        // Call
        [<Extension>] static member GetSimulationProperties(x: Call)            = g x.Properties (function SimulationCall   p -> Some p | _ -> None)
        [<Extension>] static member SetSimulationProperties(x: Call, v)         = s x.Properties (function SimulationCall   p -> Some p | _ -> None) SimulationCall v
        [<Extension>] static member GetControlProperties(x: Call)               = g x.Properties (function ControlCall      p -> Some p | _ -> None)
        [<Extension>] static member SetControlProperties(x: Call, v)            = s x.Properties (function ControlCall      p -> Some p | _ -> None) ControlCall v
        [<Extension>] static member GetMonitoringProperties(x: Call)            = g x.Properties (function MonitoringCall   p -> Some p | _ -> None)
        [<Extension>] static member SetMonitoringProperties(x: Call, v)         = s x.Properties (function MonitoringCall   p -> Some p | _ -> None) MonitoringCall v
        [<Extension>] static member GetLoggingProperties(x: Call)               = g x.Properties (function LoggingCall      p -> Some p | _ -> None)
        [<Extension>] static member SetLoggingProperties(x: Call, v)            = s x.Properties (function LoggingCall      p -> Some p | _ -> None) LoggingCall v
        [<Extension>] static member GetMaintenanceProperties(x: Call)           = g x.Properties (function MaintenanceCall  p -> Some p | _ -> None)
        [<Extension>] static member SetMaintenanceProperties(x: Call, v)        = s x.Properties (function MaintenanceCall  p -> Some p | _ -> None) MaintenanceCall v
        [<Extension>] static member GetCostAnalysisProperties(x: Call)          = g x.Properties (function CostAnalysisCall p -> Some p | _ -> None)
        [<Extension>] static member SetCostAnalysisProperties(x: Call, v)       = s x.Properties (function CostAnalysisCall p -> Some p | _ -> None) CostAnalysisCall v
