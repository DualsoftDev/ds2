using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    [RelayCommand(CanExecute = nameof(CanForceWork))]
    private void ForceWorkStart()
    {
        if (_simEngine is null || SelectedSimWork is null) return;
        var engine = _simEngine;

        // 자동선택: 모든 Source Work 일괄 시작
        if (SelectedSimWork.IsAutoStart)
        {
            BatchStartSources(engine);
            return;
        }

        if (!TryGetSelectedSimWork(out _, out var selectedWork)) return;
        SingleStartWork(engine, selectedWork);
    }

    [RelayCommand(CanExecute = nameof(CanForceWork))]
    private void ForceWorkReset()
    {
        if (!TryGetSelectedSimWork(out var engine, out var selectedWork)) return;

        var token = engine.GetWorkToken(selectedWork.Guid);
        if (token is not null)
        {
            var label = FormatTokenDisplay(token.Value);
            var result = ShowPausedMessageBox(
                $"{selectedWork.Name}에 토큰 {label}이(가) 있습니다.\n토큰을 제거하고 리셋하시겠습니까?",
                "토큰 확인",
                System.Windows.MessageBoxButton.YesNo,
                "?");
            if (result != System.Windows.MessageBoxResult.Yes) return;
            engine.DiscardToken(selectedWork.Guid);
        }

        engine.ForceWorkState(selectedWork.Guid, Status4.Ready);
        AddSimLog(SimText.ManualWorkReset(selectedWork.Name));
    }

    private bool CanForceWork() => IsSimulating && !IsSimPaused && SelectedSimWork is not null;

    // ── 배치 시작 ──────────────────────────────────────────────────

    private void BatchStartSources(ISimulationEngine engine)
    {
        WarnSourcesWithoutTokenSpec(engine);

        var finishedSources = CollectSourcesByState(engine, s => s == Status4.Finish || s == Status4.Homing);
        if (finishedSources.Count > 0)
            WarnFinishedSources(finishedSources, engine);

        var blockedSources = CollectBlockedSources(engine);
        if (blockedSources.Count > 0)
        {
            var names = string.Join("\n", blockedSources.Select(x => $"  - {x.Name}"));
            var answer = ShowPausedMessageBox(
                $"다음 Source Work의 선행 조건이 충족되지 않았습니다:\n{names}\n\n강제로 시작하시겠습니까?",
                "선행 조건 미충족",
                System.Windows.MessageBoxButton.YesNo,
                "⚠",
                suppressKey: "source_pred_batch");
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        foreach (var sourceGuid in engine.Index.TokenSourceGuids)
        {
            var currentState = _stateCache.GetOrDefault(sourceGuid, Status4.Ready);
            if (currentState != Status4.Ready) continue;

            StartSourceWork(engine, sourceGuid);
        }
        AddSimLog("Source Work 일괄 시작");
    }

    // ── 단일 시작 ──────────────────────────────────────────────────

    private void SingleStartWork(ISimulationEngine engine, SimWorkItem selectedWork)
    {
        var guid = selectedWork.Guid;
        if (!TryPrepareWorkStart(engine, selectedWork)) return;
        engine.ForceWorkState(guid, Status4.Going);
        AddSimLog(SimText.ManualWorkStarted(selectedWork.Name));
    }

    // ── 공용 헬퍼 ──────────────────────────────────────────────────

    private List<(Guid Guid, string Name)> CollectSourcesByState(
        ISimulationEngine engine, Func<Status4, bool> predicate)
    {
        return engine.Index.TokenSourceGuids
            .Where(g => predicate(_stateCache.GetOrDefault(g, Status4.Ready)))
            .Select(g => (Guid: g, Name: engine.Index.WorkName.TryFind(g)))
            .Where(x => x.Name is not null)
            .Select(x => (x.Guid, Name: x.Name!.Value))
            .ToList();
    }

    private List<(Guid Guid, string Name)> CollectBlockedSources(ISimulationEngine engine)
    {
        return engine.Index.TokenSourceGuids
            .Where(g => _stateCache.GetOrDefault(g, Status4.Ready) == Status4.Ready
                     && !WorkConditionChecker.canStartWorkPredOnly(engine.Index, engine.State, g))
            .Select(g => (Guid: g, Name: engine.Index.WorkName.TryFind(g)))
            .Where(x => x.Name is not null)
            .Select(x => (x.Guid, Name: x.Name!.Value))
            .ToList();
    }

    private void WarnFinishedSources(List<(Guid Guid, string Name)> sources, ISimulationEngine engine)
    {
        var details = sources.Select(x =>
        {
            var hasReset = WorkConditionChecker.collectResetPreds(engine.Index, x.Guid) != null;
            return hasReset
                ? $"  - {x.Name} (리셋 연결 있음 — 자동 리셋 대기)"
                : $"  - {x.Name} (리셋 연결 없음 — 직접 리셋 필요)";
        });
        ShowPausedMessageBox(
            $"다음 Source Work가 Finish 상태입니다:\n{string.Join("\n", details)}",
            "Source 시작 불가",
            System.Windows.MessageBoxButton.OK,
            "ℹ",
            suppressKey: "source_finished_batch");
    }

    private void WarnWorkNotReady(ISimulationEngine engine, string name, Guid guid, Status4 state)
    {
        var hasResetPred = WorkConditionChecker.collectResetPreds(engine.Index, guid) != null;
        var msg = hasResetPred
            ? $"{name}이(가) {SimText.StateCode(state)} 상태입니다.\n리셋 연결이 있으므로 자동 리셋될 때까지 기다려 주세요."
            : $"{name}이(가) {SimText.StateCode(state)} 상태입니다.\n리셋 연결이 없으므로 직접 리셋(R) 후 시작해 주세요.";
        ShowPausedMessageBox(msg, "Work 시작 불가", System.Windows.MessageBoxButton.OK, "ℹ");
    }

    private void WarnSourcesWithoutTokenSpec(ISimulationEngine engine)
    {
        var specs = Store.GetTokenSpecs();
        var specWorkIds = new HashSet<Guid>(
            specs.Where(s => s.WorkId != null).Select(s => s.WorkId.Value));

        var missing = engine.Index.TokenSourceGuids
            .Where(g => !specWorkIds.Contains(g))
            .Select(g => engine.Index.WorkName.TryFind(g))
            .Where(n => n != null)
            .Select(n => n!.Value)
            .ToList();

        if (missing.Count == 0) return;

        var names = string.Join("\n", missing.Select(n => $"  - {n}"));
        ShowPausedMessageBox(
            $"다음 Source Work에 TokenSpec이 설정되지 않았습니다:\n{names}\n\n토큰 이름이 \"Work이름#번호\" 형식으로 표시됩니다.",
            "TokenSpec 미설정 안내",
            System.Windows.MessageBoxButton.OK,
            "ℹ",
            suppressKey: "tokenspec_batch");
    }

    private void WarnSourceWithoutTokenSpec(ISimulationEngine engine, Guid workGuid, string workName)
    {
        var specs = Store.GetTokenSpecs();
        var hasSpec = specs.Any(s => s.WorkId != null && s.WorkId.Value == workGuid);
        if (hasSpec) return;

        ShowPausedMessageBox(
            $"{workName}에 TokenSpec이 설정되지 않았습니다.\n토큰 이름이 \"{workName}#번호\" 형식으로 표시됩니다.",
            "TokenSpec 미설정 안내",
            System.Windows.MessageBoxButton.OK,
            "ℹ",
            suppressKey: "tokenspec_" + workName);
    }

    private bool TryPrepareWorkStart(ISimulationEngine engine, SimWorkItem selectedWork)
    {
        var guid = selectedWork.Guid;
        var state = _stateCache.GetOrDefault(guid, Status4.Ready);
        if (state == Status4.Going) return false;

        if (state == Status4.Finish || state == Status4.Homing)
        {
            WarnWorkNotReady(engine, selectedWork.Name, guid, state);
            return false;
        }

        if (!engine.Index.TokenSourceGuids.Contains(guid))
            return true;

        return TryPrepareSourceStart(
            engine,
            guid,
            selectedWork.Name,
            suppressKey: "source_pred_" + selectedWork.Name,
            warnMissingSpec: true);
    }

    private bool TryPrepareSourceStart(
        ISimulationEngine engine,
        Guid workGuid,
        string workName,
        string suppressKey,
        bool warnMissingSpec)
    {
        if (!WorkConditionChecker.canStartWorkPredOnly(engine.Index, engine.State, workGuid))
        {
            var answer = ShowPausedMessageBox(
                $"{workName}의 선행 조건이 충족되지 않았습니다.\n강제로 시작하시겠습니까?",
                "선행 조건 미충족",
                System.Windows.MessageBoxButton.YesNo,
                "⚠",
                suppressKey: suppressKey);
            if (answer != System.Windows.MessageBoxResult.Yes) return false;
        }

        if (warnMissingSpec)
            WarnSourceWithoutTokenSpec(engine, workGuid, workName);

        EnsureSourceToken(engine, workGuid);
        return true;
    }

    private static void StartSourceWork(ISimulationEngine engine, Guid sourceGuid)
    {
        EnsureSourceToken(engine, sourceGuid);
        engine.ForceWorkState(sourceGuid, Status4.Going);
    }

    /// <summary>Source Work에 토큰이 없으면 자동 시드합니다.</summary>
    private static void EnsureSourceToken(ISimulationEngine engine, Guid workGuid)
    {
        if (engine.GetWorkToken(workGuid) is not null) return;
        var token = engine.NextToken();
        engine.SeedToken(workGuid, token);
    }

    private bool TryGetSelectedSimWork(
        [NotNullWhen(true)] out ISimulationEngine? engine,
        [NotNullWhen(true)] out SimWorkItem? selectedWork)
    {
        engine = _simEngine;
        selectedWork = SelectedSimWork;
        return engine is not null && selectedWork is not null && selectedWork.Guid != Guid.Empty;
    }
}
