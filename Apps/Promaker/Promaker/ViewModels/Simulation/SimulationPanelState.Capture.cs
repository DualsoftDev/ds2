using System;
using System.Collections.ObjectModel;
using System.Linq;
using AasCore.Aas3_1;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Report;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

/// <summary>
/// 시뮬레이션 결과 수집 / 박제 정책.
///  • In-memory: 시뮬 정지 시마다 시나리오를 <see cref="CapturedRuns"/> 에 누적 (최신이 head).
///  • Persistence: <see cref="Project.TechnicalData.SimulationResult"/> 는 단일 — 사용자 선택 1개만 보관.
///  • 디폴트: 새 시나리오가 누적되면 자동으로 SimulationResult 로 선택 (사용자가 변경 가능).
///  • 게이팅: 원본 AASX 에 TechnicalData 가 이미 있으면 SimulationResult 갱신을 건너뜀
///    (export 시 보존되어 무시될 것이므로). In-memory 누적은 계속 됨.
/// </summary>
public partial class SimulationPanelState
{
    /// <summary>시뮬 정지 시마다 누적되는 in-memory 시나리오 목록 (최신이 [0]).</summary>
    public ObservableCollection<Ds2.Core.SimulationResultSnapshotTypes.SimulationScenario> CapturedRuns { get; }
        = new();
    /// <summary>UI 캡처 버튼 — 현재 시뮬 결과를 시나리오로 박제.</summary>
    [RelayCommand(CanExecute = nameof(CanCaptureScenario))]
    private void CaptureScenarioToProject()
    {
        var scenarioName = $"Run_{DateTime.Now:yyyyMMdd_HHmmss}";
        var captured = TryCaptureScenario(scenarioName);
        if (captured != null)
            _setStatusText(SimText.ScenarioCaptured(captured.Meta.ScenarioName));
        else
            _setStatusText(SimText.ScenarioCaptureFailed);
    }

    private bool CanCaptureScenario() => HasReportData && Store != null;

    /// <summary>
    /// 시뮬 결과 수집 진입점.
    ///  1. 시나리오 빌드 → CapturedRuns 에 누적 (최신을 head)
    ///  2. 원본 AASX 에 TechnicalData 가 없을 때만 → SimulationResult 를 새 시나리오로 갱신 (디폴트 선택)
    /// 시뮬 데이터 없으면 null 반환.
    /// </summary>
    internal Ds2.Core.SimulationResultSnapshotTypes.SimulationScenario? TryCaptureScenario(string scenarioName)
    {
        if (_stateChangeRecords.Count == 0) return null;

        var store = Store;
        var projects = Queries.allProjects(store);
        if (projects.IsEmpty) return null;
        var project = projects.Head;

        var report = BuildReport();
        if (report.Entries.IsEmpty) return null;

        var input = SimulationSnapshotBuilder.defaultScenarioInput();
        input = new SimulationSnapshotBuilder.ScenarioInput(
            scenarioId: input.ScenarioId,
            scenarioName: scenarioName,
            simulatorName: input.SimulatorName,
            simulatorVersion: input.SimulatorVersion,
            seed: input.Seed,
            signedBy: input.SignedBy);

        var kpiInputs = BuildKpiInputs(store, project);
        var storeOpt = FSharpOption<DsStore>.Some(store);
        var traversals = CollectTraversalsSnapshot();

        // 1) 부작용 없이 시나리오만 빌드
        var scenario = SimulationSnapshotBuilder.buildScenarioOnly(
            project, storeOpt, input, kpiInputs, report, traversals);

        // 2) in-memory 누적 (최신이 [0])
        CapturedRuns.Insert(0, scenario);

        // 3) 원본 보존 게이팅: 원본 AASX 에 TechnicalData 있으면 SimulationResult 갱신 skip
        if (!HasOriginalTechnicalData(project))
            SimulationSnapshotBuilder.setSimulationResult(project, scenario);

        return scenario;
    }

    /// <summary>UI 에서 체크박스로 선택된 시나리오를 SimulationResult 로 적용.</summary>
    internal void ApplySelectedScenario(Ds2.Core.SimulationResultSnapshotTypes.SimulationScenario scenario)
    {
        if (scenario == null) return;
        var projects = Queries.allProjects(Store);
        if (projects.IsEmpty) return;
        var project = projects.Head;
        if (HasOriginalTechnicalData(project)) return;
        SimulationSnapshotBuilder.setSimulationResult(project, scenario);
    }

    /// <summary>다이얼로그에서 결과 클리어 시 호출 — in-memory 와 SimulationResult 모두 비움.</summary>
    internal void ClearAllCapturedRuns()
    {
        CapturedRuns.Clear();
        var projects = Queries.allProjects(Store);
        if (projects.IsEmpty) return;
        var project = projects.Head;
        if (FSharpOption<TechnicalDataTypes.TechnicalData>.get_IsSome(project.TechnicalData))
            project.TechnicalData.Value.SimulationResult =
                FSharpOption<SimulationResultSnapshotTypes.SimulationScenario>.None;
    }

    /// <summary>원본 AASX 에 TechnicalData 서브모델이 있는지 검사 (캐시된 Environment 기반).</summary>
    private static bool HasOriginalTechnicalData(Project project)
    {
        try
        {
            var envOpt = AasxProjectCache.tryGetEnvironment(project);
            if (!FSharpOption<object>.get_IsSome(envOpt)) return false;
            if (envOpt.Value is not AasCore.Aas3_1.Environment env || env.Submodels == null) return false;
            return env.Submodels.Any(sm =>
                string.Equals(sm.IdShort, AasxSemantics.TechnicalDataSubmodelIdShort, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    private static KpiAggregator.KpiInputs BuildKpiInputs(DsStore store, Project project)
    {
        // 첫 번째 활성 시스템의 SimulationSystemProperties 를 기준으로 사용 (없으면 None)
        FSharpOption<SimulationSystemProperties> simProps = FSharpOption<SimulationSystemProperties>.None;
        var activeSystems = Queries.activeSystemsOf(project.Id, store);
        if (!activeSystems.IsEmpty)
        {
            var firstSys = activeSystems.Head;
            var maybe = firstSys.GetSimulationProperties();
            if (FSharpOption<SimulationSystemProperties>.get_IsSome(maybe))
                simProps = maybe;
        }

        // Work 이름 → DesignCycleTime (SimulationWorkProperties.DesignCycleTime)
        Func<string, double> designCtFor = (workName) =>
        {
            try
            {
                foreach (var work in store.Works.Values)
                {
                    if (string.Equals(work.Name, workName, StringComparison.Ordinal))
                    {
                        var wp = work.GetSimulationProperties();
                        if (FSharpOption<SimulationWorkProperties>.get_IsSome(wp))
                            return wp.Value.DesignCycleTime;
                    }
                }
            }
            catch { /* best-effort */ }
            return 0.0;
        };

        var fnDesignCt = FuncConvert.FromFunc(designCtFor);
        return new KpiAggregator.KpiInputs(
            simSystemProps: simProps,
            designCycleTimeFor: fnDesignCt);
    }
}
