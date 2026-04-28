namespace Ds2.Runtime.Report

open System
open System.Reflection
open System.Text
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Report.Model

/// 시뮬레이션 결과를 Project.TechnicalData.SimulationResults 에 박제(append) 하는 단일 진입점.
/// ProMaker 빌드 파이프라인에서 시뮬 종료 후 호출하여 AASX export 직전에 시나리오를 첨부한다.
module SimulationSnapshotBuilder =

    /// 호출자가 채워야 하는 시나리오 메타 입력
    type ScenarioInput = {
        ScenarioId: string
        ScenarioName: string
        SimulatorName: string
        SimulatorVersion: string
        Seed: int option
        SignedBy: string
    }

    /// 기본 메타 — UI 에서 별도 입력 없을 때 사용
    let defaultScenarioInput () : ScenarioInput = {
        ScenarioId       = Guid.NewGuid().ToString("N").Substring(0, 8)
        ScenarioName     = "Default"
        SimulatorName    = "Ds2.Runtime.EventDrivenEngine"
        SimulatorVersion =
            try Assembly.GetAssembly(typeof<KpiAggregator.KpiInputs>).GetName().Version.ToString()
            with _ -> "0.0.0"
        Seed             = None
        SignedBy         = ""
    }

    let private toMeta
            (input: ScenarioInput)
            (modelHash: string)
            (runStart: DateTime)
            (runEnd: DateTime) : SimulationMeta =
        let m = SimulationMeta()
        m.SimulatorName    <- input.SimulatorName
        m.SimulatorVersion <- input.SimulatorVersion
        m.Ds2ModelHash     <- modelHash
        m.ScenarioId       <- input.ScenarioId
        m.ScenarioName     <- input.ScenarioName
        m.RunDate          <- runStart
        m.RunDuration_s    <- (runEnd - runStart).TotalSeconds
        m.Seed             <- input.Seed
        m.SignedBy         <- input.SignedBy
        m

    /// 6종 KPI struct + Per-Token KPI → SimulationScenario POCO
    let buildScenarioFromKpis
            (input: ScenarioInput)
            (modelHash: string)
            (runStart: DateTime)
            (runEnd: DateTime)
            (kpis: KpiAggregator.AggregatedKpis)
            (perTokenKpis: KpiPerToken seq) : SimulationScenario =
        let meta = toMeta input modelHash runStart runEnd
        SimulationResultSnapshot.buildScenario
            meta
            kpis.CycleTimes
            (Some kpis.Throughput)
            (Some kpis.Capacity)
            kpis.Constraints
            kpis.ResourceUtilizations
            kpis.OeeItems
            perTokenKpis

    /// Project + 그래프(System/Flow/Work/Call) 의 결정적 정규화 표현을 산출.
    /// - 컬렉션은 Id 로 정렬하여 순서 비결정성 제거
    /// - 시뮬·런타임 상태 필드는 hash 입력에서 제외 (모델 식별이 목적이므로)
    let private buildCanonicalRepresentation (project: Project) (store: DsStore) : string =
        let sb = StringBuilder()
        let inline appendKv (k: string) (v: string) =
            sb.Append(k).Append('=').Append(if isNull v then "" else v).Append('|') |> ignore
        let inline appendInt (k: string) (v: int) = appendKv k (string v)
        let inline appendGuid (k: string) (v: Guid) = appendKv k (v.ToString("N"))
        let nl () = sb.Append('\n') |> ignore

        // ── Project header
        appendKv "Project.Name" (if isNull project.Name then "" else project.Name)
        appendKv "Project.Version" (if isNull project.Version then "" else project.Version)
        appendGuid "Project.Id" project.Id
        nl ()

        // ── Active / Passive systems (Id 정렬)
        let systems =
            try Queries.projectSystemsOf project.Id store with _ -> []
            |> List.sortBy (fun s -> s.Id)
        for sys in systems do
            appendKv "Sys" sys.Name
            appendGuid "Sys.Id" sys.Id
            appendKv "Sys.Type" (sys.SystemType |> Option.defaultValue "")
            appendKv "Sys.IRI"  (sys.IRI |> Option.defaultValue "")
            nl ()

            // Flows
            let flows =
                try Queries.flowsOf sys.Id store with _ -> []
                |> List.sortBy (fun f -> f.Id)
            for flow in flows do
                appendKv "Flow" flow.Name
                appendGuid "Flow.Id" flow.Id
                nl ()

                // Works
                let works =
                    try Queries.worksOf flow.Id store with _ -> []
                    |> List.sortBy (fun w -> w.Id)
                for work in works do
                    appendKv "Work" work.Name
                    appendGuid "Work.Id" work.Id
                    nl ()

                    // Calls
                    let calls =
                        try Queries.callsOf work.Id store with _ -> []
                        |> List.sortBy (fun c -> c.Id)
                    for call in calls do
                        appendKv "Call" call.Name
                        appendGuid "Call.Id" call.Id
                        nl ()

        appendInt "ActiveSystemCount" project.ActiveSystemIds.Count
        appendInt "PassiveSystemCount" project.PassiveSystemIds.Count
        sb.ToString()

    /// Project 그래프 정규화 표현 → SHA-256 (디지털 스레드 키)
    /// store 가 주어지면 전체 그래프(Sys/Flow/Work/Call)를 순회하여 결정적 hash 생성.
    /// store 가 없으면 Project 헤더만 사용 (fallback).
    let computeModelHashWithStore (project: Project) (store: DsStore option) : string =
        let canonical =
            match store with
            | Some s -> buildCanonicalRepresentation project s
            | None ->
                sprintf "ProjectId=%s|Name=%s|Version=%s|ActiveSystems=%d|PassiveSystems=%d"
                    (project.Id.ToString())
                    (if isNull project.Name then "" else project.Name)
                    (if isNull project.Version then "" else project.Version)
                    project.ActiveSystemIds.Count
                    project.PassiveSystemIds.Count
        SimulationResultSnapshot.computeModelHash canonical

    /// 하위호환: store 없이 호출 (fallback hash)
    let computeModelHashFor (project: Project) : string =
        computeModelHashWithStore project None

    /// Project.TechnicalData.SimulationResult 를 단일 항목으로 설정.
    /// TechnicalData 가 아직 없으면 신규 생성. 기존 결과는 덮어씀.
    let setSimulationResult (project: Project) (scenario: SimulationScenario) : unit =
        let td =
            match project.TechnicalData with
            | Some t -> t
            | None ->
                let t = TechnicalData()
                project.TechnicalData <- Some t
                t
        td.SimulationResult <- Some scenario

    /// 부작용 없이 시나리오 빌드만 수행 (in-memory 누적 후 사용자가 선택할 때 사용).
    let buildScenarioOnly
            (project: Project)
            (storeOpt: DsStore option)
            (input: ScenarioInput)
            (kpiInputs: KpiAggregator.KpiInputs)
            (report: SimulationReport)
            (tokenTraversals: KpiAggregator.TokenTraversal seq) : SimulationScenario =
        let kpis = KpiAggregator.aggregate kpiInputs report
        let perToken = KpiAggregator.buildPerTokenKpis tokenTraversals
        let modelHash = computeModelHashWithStore project storeOpt
        let runStart = report.Metadata.StartTime
        let runEnd   = report.Metadata.EndTime
        buildScenarioFromKpis input modelHash runStart runEnd kpis perToken

    /// 시나리오 빌드 + Project.TechnicalData.SimulationResult 갱신 (단일 hook).
    let captureFromReport
            (project: Project)
            (storeOpt: DsStore option)
            (input: ScenarioInput)
            (kpiInputs: KpiAggregator.KpiInputs)
            (report: SimulationReport)
            (tokenTraversals: KpiAggregator.TokenTraversal seq) : SimulationScenario =
        let scenario = buildScenarioOnly project storeOpt input kpiInputs report tokenTraversals
        setSimulationResult project scenario
        scenario
