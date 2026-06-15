// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// OEE 순수 계산 함수 모음 (테스트 가능 — FlowLatchBadge 와 동일한 "순수함수 추출" 패턴).
/// </summary>
public static class OeeMath
{
    /// <summary>
    /// 품질 = (기간 사이클수 − 입력 불량) / 기간 사이클수 (doc/21 §12 개정).
    /// 분모는 항상 dspFlowHistory 기간 사이클수(자동) — production 행의 스냅샷 totalCount 를 분모로 쓰지 않는다.
    /// 일부 날만 불량을 입력해도 미입력일이 분모에서 빠지지 않아(미입력일 = 불량 0) 기간 품질이 왜곡되지 않고,
    /// "100% 에서 시작해 입력된 불량만큼 깎인다"는 운영 모델과 일치한다. 과거 날짜 불량 입력은 on-demand 재계산으로
    /// 즉시 소급 반영된다. 불량 데이터가 전혀 없으면 100% 가정(Source="assumed")을 값으로 제공하되 출처를 명시한다 —
    /// 데이터 계층에서 가짜 행을 만들지 않는 것(§11.1)과 별개로, 계산 계층의 명시적 가정이다.
    /// </summary>
    /// <param name="totalCount">기간 내 사이클 수(비가동 제외, dspFlowHistory 자동).</param>
    /// <param name="prodReject">기간 내 입력 불량 합(oeeProductionCount, plc&gt;manual 단일화).</param>
    /// <param name="hasReject">기간 내 production 행 존재 여부 — true=실측(measured), false=가정(assumed).</param>
    public static (double? Quality, string? Note, string? Source, int? RejectOut, int? GoodOut) ComputeQuality(
        int? totalCount, int prodReject, bool hasReject)
    {
        if (totalCount is null || totalCount <= 0)
            return (null, "기간 내 생산 사이클 0 — 품질 산출 불가.", null, null, null);

        var reject = hasReject ? Math.Max(0, prodReject) : 0;
        var good = Math.Max(0, totalCount.Value - reject);
        var quality = Math.Clamp((double)good / totalCount.Value, 0.0, 1.0);
        return hasReject
            ? (quality, "양품(사이클수 − 입력 불량) ÷ 사이클수.", "measured", reject, good)
            : (quality, "불량 미입력 — 100% 가정. 불량 입력 시 실측 반영됩니다.", "assumed", reject, good);
    }

    /// <summary>
    /// OEE = 가용성 × 성능 × 품질. 한 요소라도 null 이면 산출 불가(null + 사유). 품질이 가정(assumed)이면 노트에 명시.
    /// </summary>
    public static (double? Oee, string? Note) ComputeOee(double? availability, double? performance, double? quality, string? qualitySource)
    {
        if (availability is double a && performance is double p && quality is double q)
            return (a * p * q, qualitySource == "assumed" ? "품질 100% 가정 포함(불량 미입력)." : null);

        var missing = new List<string>();
        if (availability is null) missing.Add("가용성");
        if (performance is null) missing.Add("성능");
        if (quality is null) missing.Add("품질");
        return (null, $"구성요소 미산출({string.Join(", ", missing)}) — OEE 산출 불가.");
    }

    /// <summary>
    /// MTBF = Σ가동시간 / 고장건수. 고장 0건이면 분모 0 → 가짜 수치(max(n,1)) 금지하고 null + 무고장 표기(doc/21 §10).
    /// NoFault=true 면 UI 가 "🟢 무고장" 배지를 띄운다.
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf(double runtimeMs, int failureCount)
    {
        if (failureCount <= 0)
            return (null, "고장(분류 unplanned) 건수 0 — MTBF 산출 불가(무고장).", true);
        return (runtimeMs / failureCount, "Σ가동시간 / 고장건수 (가동시간 = 가용성 분모와 동일 폴백).", false);
    }

    /// <summary>
    /// 표준CT 자동기입 후보 산출 (doc/21 §12 D): 클린샘플 ≥ minClean 이면 best-demonstrated p10(확정, "auto"),
    /// 그보다 적지만 ≥ minMedian 이면 중앙값(임시, "auto-median"), 그 외엔 산출 안 함(null). 승급/덮어쓰기 규칙은
    /// 호출측(AppSettingsService.FillIdealCycleTimesAuto)이 source 로 판단한다 — 이 함수는 "무엇을 기입할지"만 결정.
    /// </summary>
    public static (int? Ms, string? Source) PickAutoIdealCycle(
        int sampleCount, int recommendedMs, int medianMs, int minClean, int minMedian)
    {
        if (sampleCount >= minClean && recommendedMs > 0) return (recommendedMs, "auto");
        if (sampleCount >= minMedian && medianMs > 0) return (medianMs, "auto-median");
        return (null, null);
    }

    /// <summary>
    /// nocycle clear 시 지속시간 기반 자동 분류(doc/21 §12 E): ≥ 8h → 점검(planned), ≥ 5분 → 고장(unplanned),
    /// 그 미만 → 미분류 유지(ShouldClassify=false). isFailure = (category == unplanned).
    /// </summary>
    public static (string? ReasonCode, string? Category, bool IsFailure, bool ShouldClassify) ClassifyByDuration(double durationMs)
    {
        const double failureMs = 5d * 60 * 1000;
        const double maintMs = 8d * 60 * 60 * 1000;
        if (durationMs >= maintMs) return ("planned_maint", "planned", false, true);
        if (durationMs >= failureMs) return ("equipment_fault", "unplanned", true, true);
        return (null, null, false, false);
    }
}
