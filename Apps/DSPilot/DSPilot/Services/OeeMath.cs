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
    /// 비생산 자동판정 배수 <b>기본값</b> — 무변화 정지 길이가 14일 평균 CT 의 이 배수 이상이면 "비생산"(분모 밖)으로 본다(doc/22 §3.3).
    /// "라인이 평균 사이클의 10배를 넘게 멈춰 있었으면 그 시간은 애초에 생산하던 시간이 아니다"는 가정. 고장신호와 무관(순수 CT).
    /// 2026-07-13 사용자 설정화: 실제 적용값은 <see cref="Models.OeeManualSettings.NonProdCtMultiplier"/>(설비효율 현황에서 조절) —
    /// 이 상수는 그 설정의 기본값이자 설정 미보유 경로의 폴백이다.
    /// </summary>
    public const double NonProductionCtMultiplier = 10.0;

    /// <summary>
    /// 비가동(정지) 판정 배수 기본값 — 사이클 MT(또는 미완료 CT)가 14일 평균 CT 의 이 배수를 <b>초과</b>하면
    /// 비가동으로 본다(doc/22 §3 ①②, 2026-07-13 도입 — 종전 1×). 평균의 1~2.5배 구간 "느린 사이클"은 정상(속도
    /// 손실 → 성능 P 로 재배분)으로 두고, 그 이상 늘어진 것만 정지로 계상한다. 성능 P 의 표준치는 여전히 1×평균
    /// (<see cref="ComputeCyclePerformance"/>) — 판정 경계만 배수가 붙는다. 실제 적용값은
    /// <see cref="Models.OeeManualSettings.IdleCtMultiplier"/>.
    /// </summary>
    public const double IdleCtMultiplierDefault = 2.5;

    /// <summary>
    /// 사용자/자동 분류에서 "비생산"을 뜻하는 reasonCode — 정지 이벤트를 비생산으로 보내면 이 코드가 찍히고
    /// KPI 는 그 구간을 생산가능시간(A 분모) 밖으로 카빙한다(2026-07-08 당일 판정 모델). isFailure=0, MTBF 미반영.
    /// oeeShiftException 의 kind 'non_production' 과 같은 어휘.
    /// </summary>
    public const string NonProductionReasonCode = "non_production";

    /// <summary>
    /// 무변화 정지 지속시간(ms)이 비생산(≥ multiplier×CT이상치)인지 판정(doc/22 §3.3).
    /// thr ≤ 0(표본 부족)이면 판정 불가 → false(=다운타임 유지). 대상은 "변화 없음" 정지뿐(무사이클 갭·미완료 멈춤),
    /// 완료된 느린 사이클(움직였음)은 호출측에서 제외한다. multiplier 는 사용자 설정(기본 10×) — 호출측이
    /// <see cref="Models.OeeManualSettings.ResolveCtMultipliers"/> 값을 넘긴다(미지정 = 기본 상수).
    /// </summary>
    public static bool IsLongStopNonProduction(double idleDurationMs, double ctThresholdMs,
        double multiplier = NonProductionCtMultiplier)
        => ctThresholdMs > 0 && multiplier > 0 && idleDurationMs >= multiplier * ctThresholdMs;

    /// <summary>
    /// 신호 기반 정지 분류 결과 (doc/25 §1 분류표). 무변화 정지(무사이클 갭·미완료 멈춤) 하나를 flow 관점에서
    /// 분류한다. 완료된 MT 과주행 사이클(움직인 증거)은 이 함수 대상이 아니다 — 호출측이 무조건 고장 유지.
    /// </summary>
    public enum StopClass
    {
        /// <summary>고장 — A 손실 + 고장 건수/MTBF 반영.</summary>
        Fault,
        /// <summary>대기(고장 여파, 기준 미만) — 공백으로 귀속: A 손실이지만 고장 건수/MTBF 미반영.</summary>
        WaitSlack,
        /// <summary>대기(고장 여파, 기준 이상) — 비생산(분모 밖)·대기 라벨, 고장 건수/MTBF 미반영.</summary>
        WaitNonProd,
        /// <summary>비가동(무신호, 기준 미만) — 현행 규칙 그대로: A 손실 + 건수 반영.</summary>
        Down,
        /// <summary>비생산(무신호, 기준 이상 승격) — 분모 밖, 건수 미반영 (doc/22 §3.3 현행).</summary>
        NonProduction,
    }

    /// <summary>
    /// 무변화 정지 창의 신호 기반 분류 (doc/25 §1 SSOT — 단위 테스트 대상).
    /// 우선순위: ① 자기 flow 귀속 신호(abnormal) = 유발자 → 고장(길이 무관).
    ///           ② 같은 창에 라인 내 유발자 존재(자기 신호 없음) → 대기: 기준(nonProdMult×thr) 미만 공백 / 이상 비생산.
    ///           ③ usertag(라인 스코프)만 있고 유발자 특정 불가 → 고장 유지(보수 — 형제 강등은 유발자 특정 시에만).
    ///           ④ 라인 전체 무신호(또는 신호 규칙 비활성) → 현행 순수 CT 규칙(기준 이상 비생산 승격 / 미만 비가동).
    /// signalRulesActive=false(설정 OFF 또는 커버리지 게이트 §2.4 발동)면 ④ 만 적용 — 나머지 인자는 무시된다.
    /// 수동 재분류(비생산↔비가동 보내기)는 이 함수 밖에서 항상 우선한다.
    /// </summary>
    public static StopClass ClassifyStopWindow(
        bool signalRulesActive, bool hasOwnSignal, bool lineHasCulprit, bool lineHasAnySignal,
        double durationMs, double ctThresholdMs, double nonProdMultiplier = NonProductionCtMultiplier)
    {
        if (signalRulesActive)
        {
            if (hasOwnSignal) return StopClass.Fault;
            if (lineHasCulprit)
                return IsLongStopNonProduction(durationMs, ctThresholdMs, nonProdMultiplier)
                    ? StopClass.WaitNonProd
                    : StopClass.WaitSlack;
            if (lineHasAnySignal) return StopClass.Fault;   // usertag만 — 유발자 특정 불가, 보수적으로 고장 유지
        }
        return IsLongStopNonProduction(durationMs, ctThresholdMs, nonProdMultiplier)
            ? StopClass.NonProduction
            : StopClass.Down;
    }

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
    /// nonProdMultiplier 는 사용자 설정 비생산 배수(기본 10×) — <see cref="IsLongStopNonProduction"/> 와 동일 경계.
    /// </summary>
    public static GapClass ClassifyGap(double gapMs, double gapMedianMs, double ctThresholdMs,
        double nonProdMultiplier = NonProductionCtMultiplier)
    {
        if (IsLongStopNonProduction(gapMs, ctThresholdMs, nonProdMultiplier)) return GapClass.NonProduction;
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
    /// MTBF = Σ가동시간 / 고장건수. 고장 0건이면 분모 0 → 가짜 수치(max(n,1)) 금지하고 null + 고장없음 표기(doc/21 §10).
    /// NoFault=true 면 UI 가 "🟢 고장없음" 배지를 띄운다.
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf(double runtimeMs, int failureCount)
    {
        if (failureCount <= 0)
            return (null, "고장(분류 unplanned) 건수 0 — MTBF 산출 불가(고장없음).", true);
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
    /// 한 사이클을 정상/비가동으로 분류 (doc/22 §3 ①②). thr = CT이상치(14일 평균, ms), 판정 경계 = thr × idleMultiplier.
    ///   ① MT &gt; thr×mult (완료가 늦게 발화 = 정지를 머금은 과주행)
    ///   ② complete=null(=mt null) AND CT &gt; thr×mult (끝내 완료 못한 CT 폭주)
    /// CT 없는 사이클(마지막 열린)은 Ignore. thr ≤ 0(표본 부족)이면 판정 불가 → 상위에서 산출 게이트.
    /// idleMultiplier 는 사용자 설정 비가동 배수(2026-07-13, 기본 2.5× — 이 함수의 기본 인자는 종전 호환 1.0).
    /// 경계 아래의 느린 사이클은 정상(Σ실측CT 편입 → 성능 P 가 속도 손실로 흡수). 성능 표준치는 여전히 1×thr.
    /// IsIdle(아웃라이어 캡)과는 무관 — IsIdle 은 CT이상치 산출 시만 제외(§3.2).
    /// </summary>
    public static CycleClass ClassifyCycle(int? mt, int? ct, double ctThresholdMs, double idleMultiplier = 1.0)
    {
        if (ct is not int c || c <= 0) return CycleClass.Ignore;
        if (ctThresholdMs <= 0) return CycleClass.Normal;
        var boundary = ctThresholdMs * Math.Max(idleMultiplier, 1.0);
        if (mt is int m)
            return m > boundary ? CycleClass.Downtime : CycleClass.Normal;   // ①
        return c > boundary ? CycleClass.Downtime : CycleClass.Normal;        // ② complete=null
    }

    /// <summary>
    /// 사이클기반 가용성 A = Σ실측CT / (Σ실측CT + Σ비가동CT) (doc/22 §4). 분모 0 이면 null + 사유.
    /// (TEEP 매트릭스 셀·테스트 전용 — 요약 KPI 는 <see cref="ComputeWallClockAvailability"/> 벽시계 모델로 이관.)
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
    /// 벽시계 가용성 A = Σ가동 ÷ Σ생산가능시간 (2026-07-06 단일 모델). 세 뷰(추이·정산·도넛) 공통 SSOT.
    ///   생산가능 = 캘린더 − 비생산(지정 창/14일 학습패턴) − 미계측(수신 공백)
    ///   가동     = 정상 사이클이 실제 돈 구간(runIntervals ∩ 생산가능)
    ///   비가동   = 생산가능 − 가동 (잔여 = 전부 정지 — 무사이클·미완료·인식지연 모두 포함)
    /// 라인(다-Flow)은 호출측이 flow별 합산으로 넘긴다(availableWallMs = 생산가능_1flow × flow수) → 생산가능시간
    /// 가중평균 = flow별 A 평균. 분모 0 이면 null + 사유.
    /// </summary>
    public static (double? Availability, string? Note) ComputeWallClockAvailability(double runWallMs, double availableWallMs)
    {
        if (availableWallMs <= 0)
            return (null, "생산가능시간 0(전 기간 비생산/미계측) — 가용성 산출 불가.");
        return (Math.Clamp(runWallMs / availableWallMs, 0, 1),
            "가동(벽시계) ÷ 생산가능시간(캘린더 − 비생산 − 미계측). 비가동 = 생산가능 − 가동(잔여).");
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
    /// doc/21 의 Σruntime/고장건수 를 대체. 0건이면 고장없음 배지, 1건이면 간격 없음(산출 불가).
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf2(IReadOnlyList<double> onsetsAscMs)
    {
        if (onsetsAscMs is null || onsetsAscMs.Count == 0)
            return (null, "비가동(고장) 0건 — MTBF 산출 불가(고장없음).", true);
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

    /// <summary>
    /// 구간(UTC epoch ms)들을 [clipS, clipE) 로 클립해 minute-of-day(0~1440) 커버리지로 접어 병합
    /// windows 로 반환 — planned-stops/actual 의 "하루 접기"(기간 마지막 날)와 "날짜별 접기"(TEEP
    /// 날짜별 비생산 패턴, 날마다 그 날의 자정 경계로 클립해 호출) 공용 순수함수.
    /// 클립 후 폭이 하루(1440분) 이상인 구간은 전체 채움. 자정을 걸치는 클립은 wrap(% 1440) — 단
    /// 날짜별 호출처럼 클립 자체가 로컬 자정 경계면 wrap 은 발생하지 않는다.
    /// </summary>
    /// <param name="minuteOfDay">epoch ms → 로컬 minute-of-day(0~1439) 변환 — 주입식(테스트 타임존 독립).
    /// 프로덕션은 <see cref="LocalMinuteOfDay"/> 를 넘긴다.</param>
    public static List<PlannedStopWindowDto> FoldIntervalsToMinuteOfDay(
        IEnumerable<(double S, double E)> intervals, double clipS, double clipE,
        Func<double, int> minuteOfDay)
    {
        var covered = new bool[1440];
        foreach (var (s0, e0) in intervals)
        {
            var s = Math.Max(s0, clipS);
            var e = Math.Min(e0, clipE);
            if (e <= s) continue;
            var durMin = (e - s) / 60000.0;
            if (durMin >= 1440) { for (int m = 0; m < 1440; m++) covered[m] = true; continue; }
            int startMin = minuteOfDay(s);
            int span = (int)Math.Ceiling(durMin);
            for (int k = 0; k < span; k++) covered[(startMin + k) % 1440] = true;
        }

        var res = new List<PlannedStopWindowDto>();
        int? wStart = null;
        for (int m = 0; m <= 1440; m++)
        {
            bool has = m < 1440 && covered[m];
            if (has && wStart == null) wStart = m;
            else if (!has && wStart != null) { res.Add(new PlannedStopWindowDto(wStart.Value, m, null)); wStart = null; }
        }
        return res;
    }

    /// <summary>epoch ms → 서버 로컬 minute-of-day (프로덕션용 기본 변환기).</summary>
    public static int LocalMinuteOfDay(double epochMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs).LocalDateTime;
        return local.Hour * 60 + local.Minute;
    }

    // ── 비생산 시간대 학습기 — 일별 샘플 투표제 (doc/22 §3.5, Phase 1 참고 표시 전용) ──
    //
    // 구모델("14일 중 1건이라도 있으면 창")은 단발 정지 하나가 시간대를 영구 오염시켰다.
    // 새 모델: 활동일마다 "그날의 비생산 영역" 샘플 1장을 만들고(하루 1표), 활동일의 promoteRatio 이상이
    // 반복 투표한 슬롯만 창으로 승격 — 14일 이동평균의 구현체다(슬롯별 값 = 비생산이었던 날의 비율).

    /// <summary>슬롯이 그날 표를 얻는 데 필요한 최소 커버 비율 — 정지가 슬롯의 절반 이상을 덮어야 투표.</summary>
    public const double PatternSlotCoverRatio = 0.5;

    /// <summary>
    /// 한 활동일의 비생산 minute-of-day 창들(그날 정지를 <see cref="FoldIntervalsToMinuteOfDay"/> 로 접은 것)을
    /// slotMinutes 단위 슬롯 투표로 변환. 슬롯의 <see cref="PatternSlotCoverRatio"/> 이상을 덮은 창만 그 슬롯에 투표
    /// (경계를 스치는 조각이 슬롯을 통째로 먹지 않게).
    /// </summary>
    public static bool[] SlotVotesFromMinuteWindows(
        IEnumerable<(int StartMin, int EndMin)> dayWindows, int slotMinutes)
    {
        if (slotMinutes <= 0) slotMinutes = 30;
        var slotCount = (1440 + slotMinutes - 1) / slotMinutes;
        var coverMin = new int[slotCount];
        foreach (var (s0, e0) in dayWindows)
        {
            var s = Math.Clamp(s0, 0, 1440);
            var e = Math.Clamp(e0, 0, 1440);
            if (e <= s) continue;
            for (int i = s / slotMinutes; i < slotCount && i * slotMinutes < e; i++)
            {
                var overlap = Math.Min(e, (i + 1) * slotMinutes) - Math.Max(s, i * slotMinutes);
                if (overlap > 0) coverMin[i] += overlap;
            }
        }
        var votes = new bool[slotCount];
        for (int i = 0; i < slotCount; i++)
            votes[i] = coverMin[i] >= slotMinutes * PatternSlotCoverRatio;
        return votes;
    }

    /// <summary>
    /// 활동일별 슬롯 투표 → 승격 창(분 단위, 인접 슬롯 병합). 승격 = 투표 수 ≥ promoteRatio × 활동일 수.
    /// 활동일이 minActiveDays 미만이면 표본 부족 → 창 미성립(빈 목록) — 가짜 창 금지(doc/21 §10 정직성).
    /// </summary>
    public static List<(int StartMin, int EndMin)> BuildNonProdPatternWindows(
        IReadOnlyList<bool[]> dayVotes, int slotMinutes, double promoteRatio, int minActiveDays)
    {
        var res = new List<(int StartMin, int EndMin)>();
        if (slotMinutes <= 0) slotMinutes = 30;
        if (dayVotes.Count == 0 || dayVotes.Count < Math.Max(1, minActiveDays)) return res;

        var slotCount = dayVotes.Max(v => v.Length);
        var counts = new int[slotCount];
        foreach (var v in dayVotes)
            for (int i = 0; i < v.Length && i < slotCount; i++)
                if (v[i]) counts[i]++;

        var need = promoteRatio * dayVotes.Count - 1e-9; // 부동소수 경계(9/15=0.6 등) 보호
        int? wStart = null;
        for (int i = 0; i <= slotCount; i++)
        {
            bool on = i < slotCount && counts[i] >= need;
            if (on && wStart is null) wStart = i;
            else if (!on && wStart is int s0)
            {
                res.Add((s0 * slotMinutes, Math.Min(1440, i * slotMinutes)));
                wStart = null;
            }
        }
        return res;
    }
}
