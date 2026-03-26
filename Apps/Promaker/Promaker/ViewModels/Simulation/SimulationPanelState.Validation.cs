using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Store;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    [RelayCommand]
    private void CheckModel()
    {
        try
        {
            var store = Store;
            var index = SimIndexModule.build(store, 10);
            _warningGuids.Clear();
            SimEventLog.Clear();

            var sections = new List<GraphWarningSection>();
            CollectWarning(sections, "순환 데드락 위험", WarningSeverity.Red,
                GraphValidator.findDeadlockCandidates(index),
                "(해당 Work의 Start 선행조건에 순환 후속 Work가 포함되어 있습니다)");
            CollectWarning(sections, "Source 자동 시작 불가", WarningSeverity.Red,
                GraphValidator.findSourcesWithPredecessors(index),
                "(predecessor가 있어 자동 시작되지 않습니다. 순환 경로에 있으면 데드락이 발생합니다)");
            CollectGroupIgnoreWarning(sections, index);
            CollectTokenUnreachableWarning(sections, index);
            CollectWarning(sections, "Reset 연결 누락", WarningSeverity.Yellow,
                GraphValidator.findUnresetWorks(index));
            CollectWarning(sections, "Source 후보", WarningSeverity.Yellow,
                GraphValidator.findSourceCandidates(index),
                "(이 Work들을 Token Source로 지정하면 자동 시작/데드락 해소가 가능합니다)");
            CollectDurationWarning(sections, index);
            CollectTokenSpecWarning(sections, index);

            ApplyWarningsToCanvas();

            if (sections.Count > 0)
            {
                AddGraphWarningLogs(sections);
                Dialogs.DialogHelpers.ShowGraphWarnings(sections);
                _setStatusText($"모델 검증: {sections.Count}건의 경고 발견");
                return;
            }

            var systems = store.SystemsReadOnly.Count;
            var flows = store.FlowsReadOnly.Count;
            var works = store.WorksReadOnly.Count;
            var calls = store.CallsReadOnly.Count;
            var arrows = store.ArrowWorksReadOnly.Count + store.ArrowCallsReadOnly.Count;

            Dialogs.DialogHelpers.Info(
                $"모델 검증 완료 — 문제 없음\n\n" +
                $"System: {systems}  Flow: {flows}\n" +
                $"Work: {works}  Call: {calls}\n" +
                $"Arrow: {arrows}");
            _setStatusText("모델 검증: 문제 없음");
        }
        catch (Exception ex)
        {
            SimLog.Error("Model check failed", ex);
            _setStatusText($"모델 검증 실패: {ex.Message}");
        }
    }

    private void CollectWarning(
        List<GraphWarningSection> sections,
        string title,
        WarningSeverity severity,
        IEnumerable<Tuple<Guid, string, string>> items,
        string? detail = null)
    {
        var itemList = items.ToList();
        foreach (var item in itemList)
            _warningGuids.Add(item.Item1);

        var lines = itemList
            .Select(static item => $"  - {item.Item2}.{item.Item3}")
            .ToList();
        if (lines.Count == 0)
            return;

        sections.Add(new GraphWarningSection(title, severity, lines, detail));
    }

    private static List<string> FormatGroupLines(
        IEnumerable<Tuple<string, Microsoft.FSharp.Collections.FSharpList<Tuple<Guid, string, string>>>> groups)
    {
        var lines = new List<string>();
        foreach (var group in groups)
        {
            var names = group.Item2.Select(m => m.Item3);
            lines.Add($"  - [{string.Join(", ", names)}]");
        }

        return lines;
    }

    private void CollectGroupIgnoreWarning(List<GraphWarningSection> sections, SimIndex index)
    {
        var groups = GraphValidator.findGroupWorksWithoutIgnore(index);
        if (!groups.Any()) return;

        foreach (var group in groups)
            foreach (var member in group.Item2)
                _warningGuids.Add(member.Item1);

        sections.Add(new GraphWarningSection(
            "Group Ignore 누락", WarningSeverity.Red, FormatGroupLines(groups),
            "(그룹 내 Work 중 1개를 제외한 나머지는 TokenRole.Ignore를 지정해야 합니다)"));
    }

    private void CollectDurationWarning(List<GraphWarningSection> sections, SimIndex index)
    {
        var lines = new List<string>();
        foreach (var workGuid in index.AllWorkGuids)
        {
            var workOpt = DsQuery.getWork(workGuid, index.Store);
            if (workOpt is null) continue;

            var periodOpt = workOpt.Value.Properties.Period;
            var userMs = periodOpt != null ? (int)periodOpt.Value.TotalMilliseconds : 0;

            var deviceOpt = DsQuery.tryGetDeviceDurationMs(workGuid, index.Store);
            if (deviceOpt is null) continue;
            var deviceMs = deviceOpt.Value;

            if (userMs > 0 && userMs < deviceMs)
            {
                var sysName = index.WorkSystemName.TryFind(workGuid)?.Value ?? "";
                var wName = index.WorkName.TryFind(workGuid)?.Value ?? "";
                _warningGuids.Add(workGuid);
                lines.Add($"  - {sysName}.{wName} (설정: {userMs}ms, Critical Path: {deviceMs}ms)");
            }
        }

        if (lines.Count == 0) return;

        sections.Add(new GraphWarningSection(
            "Work Duration < Critical Path", WarningSeverity.Yellow, lines,
            "(설정된 기간이 Device Critical Path보다 짧습니다. 실제 실행 시간은 Critical Path 기준으로 적용됩니다)"));
    }

    private void CollectTokenUnreachableWarning(List<GraphWarningSection> sections, SimIndex index)
    {
        var works = GraphValidator.findTokenUnreachableWorks(index).ToList();
        if (works.Count == 0) return;

        foreach (var w in works)
            _warningGuids.Add(w.Item1);

        sections.Add(new GraphWarningSection(
            "토큰 도달 불가", WarningSeverity.Red,
            works.Select(w => $"  - {w.Item2}.{w.Item3}").ToList(),
            "(모든 선행 Work가 Ignore 상태여서 토큰이 전달되지 않습니다)"));
    }

    private void CollectTokenSpecWarning(List<GraphWarningSection> sections, SimIndex index)
    {
        var specs = DsQuery.getTokenSpecs(Store);
        var specWorkIds = new HashSet<Guid>(
            specs.Where(s => s.WorkId != null).Select(s => s.WorkId.Value));

        var missing = index.TokenSourceGuids
            .Where(g => !specWorkIds.Contains(g))
            .Select(g => index.WorkName.TryFind(g))
            .Where(n => n != null)
            .Select(n => n!.Value)
            .ToList();

        if (missing.Count == 0) return;

        sections.Add(new GraphWarningSection(
            "TokenSpec 미설정", WarningSeverity.Yellow,
            missing.Select(n => $"  - {n}").ToList(),
            "(토큰 이름이 \"Work이름#번호\" 형식으로 표시됩니다)"));
    }
}
