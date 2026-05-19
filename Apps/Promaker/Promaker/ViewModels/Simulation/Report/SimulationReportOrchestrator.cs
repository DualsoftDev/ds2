using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AasCore.Aas3_1;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Exporters;
using Ds2.Runtime.Report.Model;
using log4net;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Promaker.ViewModels;

/// <summary>
/// 시뮬 결과 누적/박제/내보내기 collaborator.
/// SimulationPanelState 의 partial 에서 분리되어 자체 ObservableObject 로 동작.
/// 상위(SimulationPanelState) 는 engine/clock/store/setStatusText/traversals 를 주입.
/// 외부 노출 표면: Report(_xxx) Commands, CapturedRuns, HasReportData,
/// 그리고 Record/Clear/CurrentReport/TryCaptureScenario/ApplySelectedScenario/ClearAllCapturedRuns.
/// </summary>
public sealed partial class SimulationReportOrchestrator : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger("Simulation");

    private const int MaxStateChangeRecords = 50_000;
    private const int StateChangeTrimChunk  = 5_000;
    private const int MaxCapturedRuns       = 20;

    private const string ReportEmpty        = "내보낼 시뮬레이션 데이터가 없습니다.";
    private const string ReportDialogTitle  = "시뮬레이션 리포트 내보내기";

    private static string ReportSaved(string path)        => $"리포트 저장 완료: {path}";
    private static string ReportSaveFailed(string msg)    => $"리포트 저장 실패: {msg}";
    private static string ReportError(string msg)         => $"리포트 오류: {msg}";
    private static string ScenarioCaptured(string name)   => $"시뮬 시나리오 저장됨: {name}";
    private const string ScenarioCaptureFailed            = "시뮬 시나리오 저장 실패: 데이터가 없거나 프로젝트를 찾을 수 없습니다.";

    private readonly List<StateChangeRecord> _records = [];

    private readonly Func<ISimulationEngine?> _engineProvider;
    private readonly Func<DateTime>           _simStartTimeProvider;
    private readonly Func<DsStore>            _storeProvider;
    private readonly Action<string>           _setStatusText;
    private readonly Func<IReadOnlyList<KpiAggregator.TokenTraversal>> _traversalsProvider;

    /// <summary>시뮬 정지 시마다 누적되는 in-memory 시나리오 목록 (최신이 [0]).</summary>
    public ObservableCollection<SimulationResultSnapshotTypes.SimulationScenario> CapturedRuns { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureScenarioToProjectCommand))]
    private bool _hasReportData;

    public SimulationReportOrchestrator(
        Func<ISimulationEngine?> engineProvider,
        Func<DateTime>           simStartTimeProvider,
        Func<DsStore>            storeProvider,
        Action<string>           setStatusText,
        Func<IReadOnlyList<KpiAggregator.TokenTraversal>> traversalsProvider)
    {
        _engineProvider = engineProvider;
        _simStartTimeProvider = simStartTimeProvider;
        _storeProvider = storeProvider;
        _setStatusText = setStatusText;
        _traversalsProvider = traversalsProvider;
    }

    // ── State change 기록 ────────────────────────────────────────

    public void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateString = Presentation.Status4Visuals.ShortCode(state);
        var timestamp = CurrentSimulationTimestamp();

        var engine = _engineProvider();
        var tokenItem = FSharpOption<int>.None;
        var originName = string.Empty;
        if (engine is not null && nodeType == "Work" && Guid.TryParse(nodeId, out var wid))
        {
            var token = engine.GetWorkToken(wid);
            if (FSharpOption<TokenValue>.get_IsSome(token))
            {
                tokenItem = FSharpOption<int>.Some(token.Value.Item);
                var origin = engine.GetTokenOrigin(token.Value);
                if (FSharpOption<Tuple<string, int>>.get_IsSome(origin))
                    originName = origin.Value.Item1 ?? string.Empty;
            }
        }

        _records.Add(new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateString, timestamp,
            tokenItem, originName));
        if (_records.Count > MaxStateChangeRecords)
            _records.RemoveRange(0, StateChangeTrimChunk);
        HasReportData = _records.Count > 0;
    }

    /// <summary>시뮬 reset/start 시 호출 — 기록과 HasReportData 를 0 으로.</summary>
    public void Clear()
    {
        _records.Clear();
        HasReportData = false;
    }

    private DateTime CurrentSimulationTimestamp()
    {
        var clock = _engineProvider()?.State.Clock ?? TimeSpan.Zero;
        return _simStartTimeProvider() + clock;
    }

    private SimulationReport BuildReport()
    {
        if (_records.Count == 0) return ReportService.empty();

        var currentTime = CurrentSimulationTimestamp();
        var lastRecordTime = _records[^1].Timestamp;
        var endTime = currentTime >= lastRecordTime ? currentTime : lastRecordTime;
        return ReportService.fromStateChanges(_simStartTimeProvider(), endTime, _records);
    }

    /// <summary>Live SimulationReport — Call/Work 의 raw StateSegment 타임라인 접근용.</summary>
    public SimulationReport CurrentReport() => BuildReport();

    // ── Export ───────────────────────────────────────────────────

    private bool CanExportReport() => HasReportData;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            _setStatusText(ReportEmpty);
            return;
        }

        var fmtDlg = new Promaker.Dialogs.ReportExportDialog();
        if (fmtDlg.ShowDialog() != true) return;

        var format = fmtDlg.SelectedFormat;
        var openAfter = fmtDlg.OpenAfter;

        var filter = ExportHelper.getFilter(format);
        var ext = ExportHelper.getExtension(format);

        var dlg = new SaveFileDialog
        {
            Title = ReportDialogTitle,
            Filter = filter,
            DefaultExt = ext,
            FileName = $"SimReport_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var options = new ExportOptions
            {
                Format = format,
                FilePath = dlg.FileName,
                IncludeGanttChart = true,
                IncludeSummary = true,
                IncludeDetails = true,
                PixelsPerSecond = 10.0
            };
            var result = ReportService.export(report, options);

            if (result.IsSuccess)
            {
                _setStatusText(ReportSaved(dlg.FileName));
                if (openAfter && System.IO.File.Exists(dlg.FileName))
                    OpenFileInDefaultApp(dlg.FileName);
            }
            else if (result.IsError)
            {
                _setStatusText(ReportSaveFailed(((ExportResult.Error)result).message));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Report export failed", ex);
            _setStatusText(ReportError(ex.Message));
        }
    }

    private static void OpenFileInDefaultApp(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"파일 열기 실패: {ex.Message}"); }
    }

    // ── Capture (scenario snapshot) ──────────────────────────────

    private bool CanCaptureScenario() => HasReportData && _storeProvider() != null;

    [RelayCommand(CanExecute = nameof(CanCaptureScenario))]
    private void CaptureScenarioToProject()
    {
        var scenarioName = $"Run_{DateTime.Now:yyyyMMdd_HHmmss}";
        var captured = TryCaptureScenario(scenarioName);
        if (captured != null)
            _setStatusText(ScenarioCaptured(captured.Meta.ScenarioName));
        else
            _setStatusText(ScenarioCaptureFailed);
    }

    /// <summary>
    /// 시뮬 결과 수집 진입점.
    ///  1. 시나리오 빌드 → CapturedRuns 에 누적 (최신을 head)
    ///  2. 원본 AASX 에 TechnicalData 가 없을 때만 → SimulationResult 를 새 시나리오로 갱신 (디폴트 선택)
    /// 시뮬 데이터 없으면 null 반환.
    /// </summary>
    internal SimulationResultSnapshotTypes.SimulationScenario? TryCaptureScenario(string scenarioName)
    {
        if (_records.Count == 0) return null;

        var store = _storeProvider();
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
        var traversals = _traversalsProvider();

        var scenario = SimulationSnapshotBuilder.buildScenarioOnly(
            project, storeOpt, input, kpiInputs, report, traversals);

        CapturedRuns.Insert(0, scenario);
        while (CapturedRuns.Count > MaxCapturedRuns)
            CapturedRuns.RemoveAt(CapturedRuns.Count - 1);

        if (!HasOriginalTechnicalData(project))
            SimulationSnapshotBuilder.setSimulationResult(project, scenario);

        return scenario;
    }

    /// <summary>UI 에서 체크박스로 선택된 시나리오를 SimulationResult 로 적용.</summary>
    public void ApplySelectedScenario(SimulationResultSnapshotTypes.SimulationScenario scenario)
    {
        if (scenario == null) return;
        var projects = Queries.allProjects(_storeProvider());
        if (projects.IsEmpty) return;
        var project = projects.Head;
        if (HasOriginalTechnicalData(project)) return;
        SimulationSnapshotBuilder.setSimulationResult(project, scenario);
    }

    /// <summary>다이얼로그에서 결과 클리어 시 호출 — in-memory 와 SimulationResult 모두 비움.</summary>
    public void ClearAllCapturedRuns()
    {
        CapturedRuns.Clear();
        var projects = Queries.allProjects(_storeProvider());
        if (projects.IsEmpty) return;
        var project = projects.Head;
        project.SimulationResult = FSharpOption<SimulationResultSnapshotTypes.SimulationScenario>.None;
    }

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
        FSharpOption<SimulationSystemProperties> simProps = FSharpOption<SimulationSystemProperties>.None;
        var activeSystems = Queries.activeSystemsOf(project.Id, store);
        if (!activeSystems.IsEmpty)
        {
            var firstSys = activeSystems.Head;
            var maybe = firstSys.GetSimulationProperties();
            if (FSharpOption<SimulationSystemProperties>.get_IsSome(maybe))
                simProps = maybe;
        }

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
