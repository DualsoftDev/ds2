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
        string CompletionStatus,
        bool   HasCallStructureViolations);

    /// <param name="ioRowCount">Step 3 IO 신호 행 수.</param>
    /// <param name="dummyRowCount">Step 3 Dummy 신호 행 수.</param>
    /// <param name="successCount">Step 3 패턴 적용 성공 건수 (0 이면 미적용 안내).</param>
    /// <param name="extraWarnings">템플릿 단계에서 수집된 추가 경고.</param>
    public static Result Build(
        DsStore store,
        int successCount,
        int ioRowCount,
        int dummyRowCount,
        IReadOnlyList<string>? extraWarnings = null)
    {
        var invalidCalls = Ds2.Core.CallValidation.detectInvalidCalls(store).ToList();
        var warnings = extraWarnings ?? Array.Empty<string>();

        return new Result(
            SignalStats:               FormatSignalStats(ioRowCount, dummyRowCount),
            CompletionStatus:          FormatCompletionStatus(successCount, warnings, invalidCalls),
            HasCallStructureViolations: invalidCalls.Count > 0);
    }

    public static string FormatSignalStats(int ioRowCount, int dummyRowCount) =>
        $"• 생성된 IO 신호: {ioRowCount}개\n• Dummy 신호: {dummyRowCount}개";

    public static string FormatCompletionStatus(
        int successCount,
        IReadOnlyList<string> warnings,
        List<Tuple<Guid, Microsoft.FSharp.Collections.FSharpList<string>>> invalidCalls)
    {
        var applyStatus = successCount > 0
            ? $"✓ 패턴 {successCount}개 ApiCall 에 적용됨"
            : "ℹ 패턴 미적용 (Step 3 '패턴 적용' 버튼을 누르지 않음 — IoBatch 의 수동 값 보존)";

        var validation = warnings.Count == 0
            ? $"✓ 모든 바인딩 검증 통과 ({DateTime.Now:HH:mm:ss})"
            : $"⚠ 경고 {warnings.Count}건: " + string.Join(" | ", warnings.Take(3));

        string callStructure;
        if (invalidCalls.Count == 0)
            callStructure = "✓ Call 구조 검증 통과 (1 Call = 1 FB, 동종 Device)";
        else
        {
            var lines = invalidCalls
                .Take(5)
                .Select(t => "  · " + string.Join(" / ", t.Item2.ToList()));
            callStructure =
                $"🚨 Call 구조 위반 {invalidCalls.Count}건 — PLC 생성 전 해결 필요:\n" +
                string.Join("\n", lines) +
                (invalidCalls.Count > 5 ? $"\n  … 외 {invalidCalls.Count - 5}건" : "");
        }

        return applyStatus + "\n" + validation + "\n" + callStructure;
    }
}
