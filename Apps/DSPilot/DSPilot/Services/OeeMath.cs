// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Oee;

namespace DSPilot.Services;

/// <summary>
/// OEE 순수 계산 함수 모음 (테스트 가능 — FlowLatchBadge 와 동일한 "순수함수 추출" 패턴).
/// </summary>
public static class OeeMath
{
    /// <summary>
    /// 비생산 자동판정 배수 — 무변화 정지 길이가 14일 평균 CT 의 이 배수 이상이면 "비생산"(분모 밖)으로 본다(doc/22 §3.3).
    /// "라인이 평균 사이클의 10배를 넘게 멈춰 있었으면 그 시간은 애초에 생산하던 시간이 아니다"는 가정. 고장신호와 무관(순수 CT).
    /// </summary>
    public const double NonProductionCtMultiplier = 10.0;

    /// <summary>
    /// 무변화 정지 지속시간(ms)이 비생산(≥ <see cref="NonProductionCtMultiplier"/>×CT이상치)인지 판정(doc/22 §3.3).
    /// thr ≤ 0(표본 부족)이면 판정 불가 → false(=다운타임 유지). 대상은 "변화 없음" 정지뿐(무사이클 갭·미완료 멈춤),
    /// 완료된 느린 사이클(움직였음)은 호출측에서 제외한다.
    /// </summary>
    public static bool IsLongStopNonProduction(double idleDurationMs, double ctThresholdMs)
        => ctThresholdMs > 0 && idleDurationMs >= NonProductionCtMultiplier * ctThresholdMs;

    /// <summary>
    /// 비가동 gap 판정 배수(doc/23 §5) — gap(완료→다음 가동 간격)이 flow 자신의 클린 gap 중앙값(gap')의
    /// 이 배수를 넘으면 비가동. ×1 은 중앙값 정의상 정상 gap 절반이 초과해 오탐 → 마진 필수, ×2 는 작고
    /// 변동 큰 gap 에서 튐 → 3 을 기본으로 한다.
    /// </summary>
    public const double DowntimeGapMultiplier = 3.0;

    /// <summary>gap(완료 후 대기 간격) 분류 결과 (doc/23 §5).</summary>
    public enum GapClass
    {
        /// <summary>정상 대기 — 생산 시간에 포함.</summary>
        Normal,
        /// <summary>비가동 — 가용성 A 분모 안에서 깎임.</summary>
        Downtime,
        /// <summary>비생산 — A 분모 밖(≥10×CT, 기존 규칙 재사용).</summary>
        NonProduction
    }

    /// <summary>
    /// gap(ms)을 정상/비가동/비생산으로 분류 (doc/23 §5).
    ///   gap ≥ <see cref="NonProductionCtMultiplier"/>(10) × ctThresholdMs → 비생산 (기존 10×CT 규칙과 동일 경계)
    ///   gap &gt; <see cref="DowntimeGapMultiplier"/>(3) × gapMedianMs      → 비가동
    ///   그 외                                                              → 정상
    /// gapMedianMs ≤ 0(표본 부족)이면 비가동 판정 불가 → 비생산 경계만 적용(가짜 정지 금지, doc/21 §10).
    /// 비생산을 먼저 검사한다 — 정상 데이터에선 항상 3×gap' &lt; 10×CT (gap'=WT ⊂ CT) 라 순서 무해하나,
    /// 표본 왜곡 시에도 "더 긴 정지 = 더 관대한 분류(분모 밖)" 방향으로 안전.
    /// </summary>
    public static GapClass ClassifyGap(double gapMs, double gapMedianMs, double ctThresholdMs)
    {
        if (IsLongStopNonProduction(gapMs, ctThresholdMs)) return GapClass.NonProduction;
        if (gapMedianMs > 0 && gapMs > DowntimeGapMultiplier * gapMedianMs) return GapClass.Downtime;
        return GapClass.Normal;
    }

    /// <summary>
    /// 무사이클 정지 onset 임계(ms) 폴백 체인 (doc/23 §6 Phase 1). 위에서부터:
    ///   ① gap' 학습됨 → max(3×gap', floor) — floor 는 초고속 flow 잡음성 미세정지 onset 방지
    ///   ② gap' 없지만 14일평균CT 학습됨 → 3×평균CT (여전히 per-flow)
    ///   ③ 학습 전무(콜드스타트) → bootstrapMs(기존 NoCycleSeconds, 기본 120s)
    /// 고정 120초를 제거하지 않고 ③ 부트스트랩으로 격하 — Day 0 첫날 정지 감지는 유지하고,
    /// 학습되면(보통 Day 1+) 자동으로 ①/② per-flow 로 승격돼 느린 flow 상시 오탐이 사라진다.
    /// </summary>
    public static double ResolveNoCycleThresholdMs(
        double gapMedianMs, double ctAvgMs, double floorMs, double bootstrapMs)
    {
        if (gapMedianMs > 0) return Math.Max(DowntimeGapMultiplier * gapMedianMs, floorMs);
        if (ctAvgMs > 0) return DowntimeGapMultiplier * ctAvgMs;
        return bootstrapMs;
    }

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
    /// 품질 결정 — 사용자가 직접 설정한 전반 품질(%)이 있으면 그 값을 우선(measured/assumed 폴백 위에 덮음, source="manual").
    /// 사용자가 "이 생산의 전반적 양품률은 대략 N%" 라고 직접 지정하는 단순 오버라이드(doc/21 §12). 미설정(null)이면
    /// 불량 입력 기반 <see cref="ComputeQuality"/>(measured) 또는 100% 가정(assumed)으로 폴백. good/reject 는 표시용 환산값.
    /// </summary>
    public static (double? Quality, string? Note, string? Source, int? RejectOut, int? GoodOut) ResolveQuality(
        double? manualQualityPercent, int? totalCount, int prodReject, bool hasReject)
    {
        if (manualQualityPercent is double pct)
        {
            var q = Math.Clamp(pct / 100.0, 0.0, 1.0);
            if (totalCount is > 0)
            {
                var good = (int)Math.Round(totalCount.Value * q, MidpointRounding.AwayFromZero);
                good = Math.Clamp(good, 0, totalCount.Value);
                return (q, "사용자 직접 입력(전반 품질).", "manual", totalCount.Value - good, good);
            }
            return (q, "사용자 직접 입력(전반 품질).", "manual", null, null);
        }
        return ComputeQuality(totalCount, prodReject, hasReject);
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
    /// MTBF '고장' 판정 단일 소스 (2026-06-15 사용자 선택 = "설비고장만"). 진짜 설비 고장(reasonCode='equipment_fault')만
    /// 고장으로 센다. 자재대기·작업자대기·금형공구·기타는 <b>계획외(unplanned) 정지</b>라 가용성(A)은 깎지만 MTBF 고장은
    /// 아니다 — isFailure 를 category(계획/계획외)와 분리. Classify/BulkClassify/CauseBit/휴리스틱이 공유.
    /// (정의 변경 시 여기 한 곳 + OeeRepositoryAdapter 의 isFailure 재정렬 마이그레이션 SQL 만 맞추면 됨.)
    /// </summary>
    public static bool IsFailureReason(string? reasonCode)
        => string.Equals(reasonCode?.Trim(), "equipment_fault", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// nocycle clear 시 지속시간 기반 자동 분류: ≥ 5분 → 설비고장(unplanned, isFailure=true),
    /// 그 미만 → 분류 불필요(onset 이 이미 isFailure=1 기본값). ShouldClassify=false 시 호출부가 skip.
    /// (8h→planned_maint 휴리스틱 제거 — 비생산 시간대 에디터가 대체)
    /// </summary>
    public static (string? ReasonCode, string? Category, bool IsFailure, bool ShouldClassify) ClassifyByDuration(double durationMs)
    {
        const double failureMs = 5d * 60 * 1000;
        if (durationMs >= failureMs) return ("equipment_fault", "unplanned", true, true);
        return (null, null, false, false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  사이클기반 OEE (doc/22 — P5 v4 CT/MT/WT 모델). 시간기반(달력/시프트) 대신
    //  관측된 사이클 CT 합을 분모로 쓴다. 모두 순수함수 — 입력은 컨트롤러가 사이클에서 집계.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>한 사이클의 비가동 판정 결과 (doc/22 §3).</summary>
    public enum CycleClass
    {
        /// <summary>CT 없는(마지막 열린) 사이클 — 집계 제외.</summary>
        Ignore,
        /// <summary>정상 사이클 — CT 를 Σ실측CT 에 가산.</summary>
        Normal,
        /// <summary>비가동 사이클 — CT 전체를 Σ비가동CT 에 가산(인식지연+고장+회복 포함).</summary>
        Downtime
    }

    /// <summary>
    /// 한 사이클을 정상/비가동으로 분류 (doc/22 §3 ①②). thr = CT이상치(14일 평균, ms).
    ///   ① MT &gt; thr (완료가 늦게 발화 = 정지를 머금은 과주행)
    ///   ② complete=null(=mt null) AND CT &gt; thr (끝내 완료 못한 CT 폭주)
    /// CT 없는 사이클(마지막 열린)은 Ignore. thr ≤ 0(표본 부족)이면 판정 불가 → 상위에서 산출 게이트.
    /// IsIdle(아웃라이어 캡)과는 무관 — IsIdle 은 CT이상치 산출 시만 제외(§3.2).
    /// </summary>
    public static CycleClass ClassifyCycle(int? mt, int? ct, double ctThresholdMs)
    {
        if (ct is not int c || c <= 0) return CycleClass.Ignore;
        if (ctThresholdMs <= 0) return CycleClass.Normal;
        if (mt is int m)
            return m > ctThresholdMs ? CycleClass.Downtime : CycleClass.Normal;   // ①
        return c > ctThresholdMs ? CycleClass.Downtime : CycleClass.Normal;        // ② complete=null
    }

    /// <summary>
    /// 사이클기반 가용성 A = Σ실측CT / (Σ실측CT + Σ비가동CT) (doc/22 §4). 분모 0 이면 null + 사유.
    /// </summary>
    public static (double? Availability, string? Note) ComputeCycleAvailability(double normalCtMs, double idleCtMs)
    {
        var denom = normalCtMs + idleCtMs;
        if (denom <= 0)
            return (null, "기간 내 사이클 CT 합 0 — 사이클기반 가용성 산출 불가.");
        return (Math.Clamp(normalCtMs / denom, 0, 1),
            "Σ실측CT ÷ (Σ실측CT + Σ비가동CT). 비가동 = MT>CT이상치 / 미완료 CT폭주 / 무사이클 정지.");
    }

    /// <summary>
    /// 사이클기반 성능 P = (N × CT이상치) / Σ실측CT, min 1.0 (doc/22 §4). CT이상치=14일 평균.
    /// 정상상태에서 P≈100% 로 수렴 — "최속 대비 손실"이 아니라 "14일 추세 대비 당기 저하" 지표(§6 ①).
    /// CT이상치 미산출(표본 부족) 또는 정상 사이클 0 이면 null + 사유.
    /// </summary>
    public static (double? Performance, string? Note) ComputeCyclePerformance(
        int normalCycleCount, double? ctThresholdMs, double normalCtMs)
    {
        // 기간 내 완료된 정상 사이클이 없으면 측정 대상 자체가 없음 → "클린샘플 부족"과 구분.
        // (이 순서가 중요: 라인 합산 시 사이클 0이면 표시 임계도 null 이라, 임계 체크를 먼저 두면
        //  '오늘 사이클 0'을 '클린샘플 부족'으로 오인 표기했던 버그가 생긴다.)
        if (normalCycleCount <= 0 || normalCtMs <= 0)
            return (null, "이 기간에 완료된 사이클 0 — 성능 산출 불가(기간 내 끝난 사이클이 1개 이상 필요).");
        if (ctThresholdMs is not double thr || thr <= 0)
            return (null, "CT이상치(14일 평균) 미산출 — 성능 산출 불가(클린샘플 부족).");
        return (Math.Min(1.0, normalCycleCount * thr / normalCtMs),
            "(정상 사이클수 × CT이상치) ÷ Σ실측CT. 14일 추세 대비 당기 속도저하 지표.");
    }

    /// <summary>
    /// MTBF (doc/22 §5 / P5 §④) = 연속 비가동 onset 간격 평균. onset 은 오름차순 ms.
    /// doc/21 의 Σruntime/고장건수 를 대체. 0건이면 무고장 배지, 1건이면 간격 없음(산출 불가).
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf2(IReadOnlyList<double> onsetsAscMs)
    {
        if (onsetsAscMs is null || onsetsAscMs.Count == 0)
            return (null, "비가동(고장) 0건 — MTBF 산출 불가(무고장).", true);
        if (onsetsAscMs.Count < 2)
            return (null, "비가동 1건 — 연속 onset 간격 없음(MTBF 산출 불가).", false);

        double sum = 0; int gaps = 0;
        for (int i = 1; i < onsetsAscMs.Count; i++)
        {
            var g = onsetsAscMs[i] - onsetsAscMs[i - 1];
            if (g > 0) { sum += g; gaps++; }
        }
        if (gaps == 0) return (null, "유효 onset 간격 없음 — MTBF 산출 불가.", false);
        return (sum / gaps, "연속 비가동 onset 간격 평균 (P5 §④).", false);
    }

    /// <summary>
    /// MTTR (doc/22 §5 / P5 §④) = mean(고장 onset → going 회복). 입력은 각 비가동 이벤트의 복구구간 ms.
    /// going 회복 = 사이클 complete(MT 종료) 시점, 미완료 사이클은 CT 종료(다음 start)/무사이클은 이벤트 EndAt.
    /// 음수 구간은 방어적으로 제외. 빈 입력이면 산출 불가.
    /// </summary>
    public static (double? Mttr, string? Note) ComputeMttr(IReadOnlyList<double> repairMsList)
    {
        var valid = (repairMsList ?? new List<double>()).Where(x => x >= 0).ToList();
        if (valid.Count == 0)
            return (null, "비가동 복구 구간 없음 — MTTR 산출 불가.");
        return (valid.Average(), "비가동 onset → going 회복 구간 평균 (P5 §④).");
    }

    /// <summary>
    /// 생산효율 TEEP = 가동(Σ실측CT) ÷ 캘린더시간 (전체, 비생산 포함) — 표준 TEEP(24×365 관점, P6 생산효율 탭).
    /// 단순 가동형: 분자=가동시간만(P·Q 미반영 — 설비효율 탭이 A·P·Q 담당). 캘린더 ≤ 0 이면 null.
    /// 라인은 호출측이 캘린더=기간×flow수 로 넘긴다(가동이 flow별 합산이므로 분모도 배수 — 병렬 flow 과다계상 방지).
    /// </summary>
    public static double? ComputeTeep(double runningMs, double calendarMs)
        => calendarMs > 0 ? Math.Clamp(runningMs / calendarMs, 0, 1) : (double?)null;

    /// <summary>
    /// 가동률(보조) = (캘린더 − 비생산) ÷ 캘린더. "운영하기로 한 시간 대비" 관점 — TEEP 와 달리 비생산을 분모서 뺀다.
    /// 캘린더 ≤ 0 이면 null. 음수 방지 clamp.
    /// </summary>
    public static double? ComputeUtilization(double calendarMs, double nonProdMs)
        => calendarMs > 0 ? Math.Clamp((calendarMs - nonProdMs) / calendarMs, 0, 1) : (double?)null;

    /// <summary>
    /// 생산효율 매트릭스(P6 L0) 셀 — 한 flow 의 시간버킷별 TEEP·OEE 산출 (순수함수, /api/oee/teep/matrix).
    /// 귀속 규칙(차트용 근사, 셀 간 이중계상 없음):
    ///   가동·사이클수 = 정상 사이클을 <b>시작 시각이 속한 버킷</b>에 통째 귀속 — 사이클이 짧아(수십 초) 경계 오차 무시 수준.
    ///   정지·비생산  = Union 구간을 버킷 겹침(overlap)으로 분배 — 다일 무사이클 갭(주말정지)이 시작일에 몰리는 왜곡 방지.
    /// 셀 지표는 기간 KPI 와 동일 정의: TEEP=가동÷버킷캘린더(단순 가동형), A=<see cref="ComputeCycleAvailability"/>,
    /// P=<see cref="ComputeCyclePerformance"/>, OEE=A×P×Q(Q=수기 전역, 기본 100% 가정). 산출 불가 셀은 null 유지(정직 표기).
    /// </summary>
    /// <param name="buckets">버킷 [시작,끝) UTC epoch ms — 오름차순, 서로 겹치지 않음(로컬 달력 클립).</param>
    /// <param name="normalCycles">정상 사이클 (시작 ms, CT ms) — 비생산 시간대 시작분 제외(KPI 가동과 동일 기준).</param>
    /// <param name="idleIntervals">비가동(정지) Union 구간 ms.</param>
    /// <param name="nonProdIntervals">비생산(자동 10× + 수동 시간대) Union 구간 ms.</param>
    /// <param name="ctThresholdMs">flow CT이상치(14일 평균) — 성능 P 의 표준.</param>
    /// <param name="quality">품질 Q (0~1) — 수기 전역값(미설정 = 1.0 가정).</param>
    public static List<OeeTeepMatrixCellDto> BuildTeepMatrixCells(
        IReadOnlyList<(double S, double E)> buckets,
        IReadOnlyList<(double StartMs, double CtMs)> normalCycles,
        IReadOnlyList<(double S, double E)> idleIntervals,
        IReadOnlyList<(double S, double E)> nonProdIntervals,
        double ctThresholdMs, double quality)
    {
        static double Overlap(IReadOnlyList<(double S, double E)> iv, double s, double e)
        {
            double sum = 0;
            foreach (var (a, b) in iv) { var o = Math.Min(b, e) - Math.Max(a, s); if (o > 0) sum += o; }
            return sum;
        }

        // 시작 시각 귀속은 정렬 후 두 포인터로 O(N log N) — 버킷이 오름차순·비중첩이라 한 번만 전진.
        var cycles = normalCycles.OrderBy(c => c.StartMs).ToList();
        int ci = 0;

        var cells = new List<OeeTeepMatrixCellDto>(buckets.Count);
        foreach (var (s, e) in buckets)
        {
            var calendarMs = Math.Max(0, e - s);
            double runningMs = 0; int count = 0;
            while (ci < cycles.Count && cycles[ci].StartMs < s) ci++;          // 첫 버킷 이전 시작분 스킵
            while (ci < cycles.Count && cycles[ci].StartMs < e) { runningMs += cycles[ci].CtMs; count++; ci++; }

            var downMs = Overlap(idleIntervals, s, e);
            var nonProdMs = Overlap(nonProdIntervals, s, e);

            var teep = ComputeTeep(runningMs, calendarMs);
            var (a, _) = ComputeCycleAvailability(runningMs, downMs);
            var (p, _) = ComputeCyclePerformance(count, ctThresholdMs, runningMs);
            double? oee = a is double av && p is double pv ? av * pv * quality : null;

            cells.Add(new OeeTeepMatrixCellDto(calendarMs, runningMs, downMs, nonProdMs, count, teep, a, p, oee));
        }
        return cells;
    }
}
