using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.Store;

namespace Promaker.Services;

/// <summary>
/// TagWizard Step 4 요약 텍스트 조립 — 순수 함수 (UI 의존 없음).
/// </summary>
public static class WizardSummaryBuilder
{
    public sealed record Result(
        string SignalStats,
        string BindingStats,
        string CompletionStatus,
        bool   HasCallStructureViolations);

    /// <param name="successCount">Step 3 패턴 적용 성공 건수 (0 이면 미적용 안내).</param>
    /// <param name="ioRowCount">IoBatch Grid 행 수.</param>
    public static Result Build(DsStore store, int successCount, int ioRowCount)
    {
        var entries = IoListEntryStore.LoadAll(store);
        var dummies = IoListEntryStore.LoadDummies(store);
        var validationErrors = IoListEntryStore.ValidateBindings(store);
        var invalidCalls = Ds2.Core.CallValidation.detectInvalidCalls(store).ToList();

        return new Result(
            SignalStats:               FormatSignalStats(entries, dummies.Count, ioRowCount),
            BindingStats:              FormatBindingStats(entries),
            CompletionStatus:          FormatCompletionStatus(successCount, validationErrors, invalidCalls),
            HasCallStructureViolations: invalidCalls.Count > 0);
    }

    public static string FormatSignalStats(List<IoListEntryDto> entries, int dummyCount, int ioRowCount)
    {
        var inSignals  = entries.Count(e => e.Direction == "Input");
        var outSignals = entries.Count(e => e.Direction == "Output");
        return
            $"• 생성된 IO 신호: {ioRowCount}개\n" +
            $"• IoList 엔트리 총 {entries.Count}개 (Input {inSignals}개 / Output {outSignals}개)\n" +
            $"• Dummy 신호: {dummyCount}개";
    }

    public static string FormatBindingStats(List<IoListEntryDto> entries)
    {
        var bound   = entries.Count(e => !string.IsNullOrEmpty(e.TargetFBType) && !string.IsNullOrEmpty(e.TargetFBPort));
        var unbound = entries.Count - bound;
        var fbTypeGroups = entries
            .Where(e => !string.IsNullOrEmpty(e.TargetFBType))
            .GroupBy(e => e.TargetFBType)
            .OrderBy(g => g.Key)
            .Select(g => $"  · {g.Key}: {g.Count()}개")
            .ToList();
        var breakdown = fbTypeGroups.Count > 0 ? "\n" + string.Join("\n", fbTypeGroups) : "";

        return
            $"• 바인딩 완료: {bound}개\n" +
            (unbound > 0 ? $"• 미바인딩: {unbound}개 (I/O 일괄 편집에서 설정 필요)\n" : "") +
            $"• 사용된 FB 타입: {fbTypeGroups.Count}개{breakdown}";
    }

    public static string FormatCompletionStatus(
        int successCount,
        List<string> validationErrors,
        List<Tuple<Guid, Microsoft.FSharp.Collections.FSharpList<string>>> invalidCalls)
    {
        var applyStatus = successCount > 0
            ? $"✓ 패턴 {successCount}개 ApiCall 에 적용됨"
            : "ℹ 패턴 미적용 (Step 3 '패턴 적용' 버튼을 누르지 않음 — IoBatch 의 수동 값 보존)";

        var validation = validationErrors.Count == 0
            ? $"✓ 모든 바인딩 검증 통과 ({DateTime.Now:HH:mm:ss})"
            : $"⚠ 검증 경고 {validationErrors.Count}건: " + string.Join(" | ", validationErrors.Take(3));

        string callStructure;
        if (invalidCalls.Count == 0)
        {
            callStructure = "✓ Call 구조 검증 통과 (1 Call = 1 FB, 동종 Device)";
        }
        else
        {
            var lines = invalidCalls
                .Take(5)
                .Select(t => "  · " + string.Join(" / ", t.Item2.ToList()))
                .ToList();
            callStructure =
                $"🚨 Call 구조 위반 {invalidCalls.Count}건 — PLC 생성 전 해결 필요:\n" +
                string.Join("\n", lines) +
                (invalidCalls.Count > 5 ? $"\n  … 외 {invalidCalls.Count - 5}건" : "");
        }

        return applyStatus + "\n" + validation + "\n" + callStructure;
    }
}
