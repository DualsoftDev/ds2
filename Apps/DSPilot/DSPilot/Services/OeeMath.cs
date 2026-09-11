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
    // ════════════════════════════════════════════════════════════════════════
    //  판정 규칙 (2026-09-11 두 규칙 모델, doc/28). 사이클 = 동작(MT) + 대기(WT). 판정·손실·표시·전환의 단위는
    //  <b>사이클 행 하나</b>다 — 행을 쪼개 일부만 고장이라 부르지 않는다(행 안에서 고장이 언제 시작됐는지는 모른다).
    //    · 완료 행(mt 있음)                ①-a mt > 중앙MT × 고장배수 → 고장 / ②-a wt ≥ 비생산 경계(WT) → 비생산 / 나머지 정상
    //    · 불인정 행(mt NULL, ct=시작~다음 시작) ①-b ct > 중앙CT × 고장배수 → 고장 / ②-b ct ≥ 비생산 경계(CT) → 비생산
    //  불인정 행의 ct 는 동작+대기의 합이라 사이클 전체 기준선(중앙 CT)에 댄다 — MT 기준선에 대면 WT 비중이 큰 flow
    //  (실측 셔틀 WT 87%)에서 정상 길이 사이클도 tail 누락 한 번에 고장이 된다.
    //  고장은 길이 무관 비생산으로 승격하지 않는다(주말 60시간도 고장 — 교정은 사용자 전환, doc/28 §2.6). 종전 "정지(비가동)"
    //  중간 밴드와 신호 기반 유발자·형제 판별(doc/25)은 폐기. 사용자가 움직이는 배수는 고장·비생산 2개.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 비생산 자동판정 배수 <b>기본값</b> — 완료 행의 대기(wt)가 14일 <b>중앙 WT × 이 배수</b> 이상이면 비생산(분모 밖).
    /// 불인정 행은 같은 배수를 <b>중앙 CT</b> 에 곱해 ct 와 비교한다(<see cref="ResolveCtNonProdBoundaryMs"/>).
    /// 실제 적용값은 <see cref="Models.OeeManualSettings.NonProdWtMultiplier"/>.
    /// <para>왜 30 인가: 종전 CT 축 기본 15× 를 현장 실측 WT 비중(#121 CT 의 43% / 셔틀 89%)으로 환산하면
    /// 17~35× 에 흩어진다. 단일 등가값은 없으므로 중간값을 잡고 flow별 환산(분·시간)을 화면에 보여 조절하게 한다.</para>
    /// </summary>
    public const double NonProductionWtMultiplier = 30.0;

    /// <summary>
    /// 비생산 경계의 하한 = 중앙 CT × 이 배수(=10 사이클). 중앙 WT 가 극소인 flow(항상 소재가 대기하는 설비 —
    /// 실측 kit Turn Zone 중앙 WT 0.6초)에선 배수만 곱하면 18초 대기가 비생산(분모 밖)이 되어 가용성을 부풀린다.
    /// "열 사이클도 못 채운 대기는 비생산이 아니다"는 가정. CT 축(불인정 행) 경계에도 같은 하한을 둔다.
    /// </summary>
    public const double WtNonProdFloorCtMultiples = 10.0;

    /// <summary>
    /// 고장 판정 배수 기본값 (doc/28 §3, 2026-09-11: 2.5 → 5.0). 완료 행의 MT 가 <b>14일 중앙 MT × 이 배수</b>를
    /// 넘으면 고장, 불인정 행은 ct 가 <b>중앙 CT × 이 배수</b>를 넘으면 고장. 기존 저장값은 변하지 않는다(신규 설치만).
    /// <para>축이 CT 가 아니라 <b>MT</b> 인 이유: 라인이 서면 모든 flow 의 CT 가 동시에 늘어나 유발자를 못 가린다.
    /// MT 는 자기 설비가 실제로 움직인 시간이라 유발자만 늘어난다(2026-08-24 실측 — 같은 4분 정지에서
    /// 조립 46.3× / 이송 8.0× / 나머지 3개 1.0×).</para>
    /// <para>실제 적용값은 <see cref="Models.OeeManualSettings.FaultMtMultiplier"/>.</para>
    /// </summary>
    public const double FaultMtMultiplierDefault = 5.0;

    /// <summary>
    /// 고장 경계의 절대 하한(ms) (2026-08-30). 초저 MT flow 에선 중앙값×배수가 지터 수준으로 내려가 잡음이
    /// 고장으로 등록된다 — 실측 2026-08-28: Prog2 중앙값 mt=22ms → 경계 55ms, mt=238ms 지터가 고장. 1초 미만
    /// 초과는 물리 고장의 증거가 될 수 없다는 가정 — 진짜 고장은 실측에서 항상 수십 초 이상이었다(225~715s).
    /// </summary>
    public const double FaultMtBoundaryFloorMs = 1_000;

    /// <summary>
    /// 표본 게이트(doc/28 §1) — 14일 창의 완료 사이클이 이 수 미만이면 기준선은 만들되 판정(①②)을 적용하지 않는다
    /// (전부 정상). 화면 '잠정' 배지도 같은 값. 3분 사이클 라인은 첫 한 시간 안에 통과한다.
    /// </summary>
    public const int MinBaselineSamples = 10;

    /// <summary>
    /// 행↔구간 <b>과반</b> 겹침 비율(doc/28 §2.6) — 저장된 수동 라벨·유지보수 이벤트를 재도출된 행에 다시 붙일 때의
    /// 내부 조인 허용치(사용자 규칙이 아니다: 전환 객체는 행이고 사용자는 시간 범위를 고르지 않는다). 한 행이 서로
    /// 겹치지 않는 두 구간에 동시에 과반일 수 없어 이중 분류가 불가능하다.
    /// </summary>
    public const double MajorityCoverRatio = 0.5;

    /// <summary>
    /// 고장 경계(MT) = max(14일 중앙 MT × 고장배수, 절대 하한). 중앙 MT 미보유(≤0)면 0 = 판정 비활성 —
    /// 0 을 그대로 비교값에 쓰면 모든 완료 행이 고장이 되므로 호출측은 0 을 "비활성"으로 다뤄야 한다.
    /// dtCond @MtThr·행 단위 판정·정지 로그 문구·'진행 중' 기준이 모두 이 함수 하나를 쓴다(경계 SSOT).
    /// </summary>
    public static double ResolveMtFaultBoundaryMs(double medianMtMs, double faultMultiplier)
        => medianMtMs <= 0 ? 0 : Math.Max(medianMtMs * faultMultiplier, FaultMtBoundaryFloorMs);

    /// <summary>
    /// 고장 경계(CT) — 불인정 행(mt NULL) 전용 = max(14일 중앙 CT × 고장배수, 절대 하한). 중앙 CT 미보유면 0(비활성).
    /// 불인정 행 판정은 MT 기준선을 보유한 flow 에서만 의미가 있다(mt NULL 이 증거인 이유는 "tail 이 와야 했는데
    /// 안 왔다"이므로 tail 미정의 flow 엔 적용 불가) — 호출측이 hasMtBaseline 으로 게이트한다.
    /// </summary>
    public static double ResolveCtFaultBoundaryMs(double medianCtMs, double faultMultiplier)
        => medianCtMs <= 0 ? 0 : Math.Max(medianCtMs * faultMultiplier, FaultMtBoundaryFloorMs);

    /// <summary>
    /// 비생산 경계(WT) — 완료 행 전용 = max(중앙 WT × 비생산배수, 중앙 CT × <see cref="WtNonProdFloorCtMultiples"/>).
    /// 중앙 WT 0(항상 즉시 재시작하는 설비)도 정상 기준선 — 그때 경계는 하한(10 사이클)이 맡는다. 중앙 CT ≤0 이면 0(비활성).
    /// </summary>
    public static double ResolveWtNonProdBoundaryMs(double medianWtMs, double medianCtMs, double nonProdMultiplier)
        => medianCtMs <= 0 ? 0
            : Math.Max(Math.Max(0, medianWtMs) * nonProdMultiplier, medianCtMs * WtNonProdFloorCtMultiples);

    /// <summary>
    /// 비생산 경계(CT) — 불인정 행(mt NULL) 전용 = max(중앙 CT × 비생산배수, 중앙 CT × 하한 배수). 중앙 CT ≤0 이면 0.
    /// tail 미정의 flow(mt·wt 항상 NULL)의 유일한 판정 경계이기도 하다(고장 판별 불가, 비생산만).
    /// '확인 필요' 플래그(<see cref="IsReviewPending"/>)의 임계로도 쓴다 — "길이만 보면 비생산 기준을 넘는 고장".
    /// </summary>
    public static double ResolveCtNonProdBoundaryMs(double medianCtMs, double nonProdMultiplier)
        => medianCtMs <= 0 ? 0
            : Math.Max(medianCtMs * nonProdMultiplier, medianCtMs * WtNonProdFloorCtMultiples);

    /// <summary>
    /// '확인 필요'(needsReview, doc/28 §2.8) — 고장 행의 길이(ct)가 비생산 경계(CT) 이상이면 사람이 볼 대상이다.
    /// 판정을 바꾸지 않는 표시 플래그. 뜻: 이 행이 대기로 서 있었다면 비생산이 됐을 길이 = "끄고 간 정지가 아닌지 확인".
    /// 해소 = '비생산으로' 전환 또는 '고장으로' 확정(둘 다 수동 라벨 → 호출측이 플래그를 내린다).
    /// </summary>
    public static bool IsReviewPending(double ctMs, double ctNonProdBoundaryMs)
        => ctNonProdBoundaryMs > 0 && ctMs >= ctNonProdBoundaryMs;

    /// <summary>
    /// 사용자/자동 분류에서 "비생산"을 뜻하는 reasonCode — 정지 이벤트를 비생산으로 보내면 이 코드가 찍히고
    /// KPI 는 그 행을 생산가능시간(A 분모) 밖으로 뺀다. isFailure=0, MTBF 미반영. oeeShiftException 의 kind 'non_production' 과 같은 어휘.
    /// </summary>
    public const string NonProductionReasonCode = "non_production";

    /// <summary>정지 길이가 (하한이 반영된) 비생산 경계 이상인가. 경계 ≤ 0 = 판정 불가 → false.</summary>
    public static bool IsNonProductionLength(double durationMs, double nonProdBoundaryMs)
        => nonProdBoundaryMs > 0 && durationMs >= nonProdBoundaryMs;

    // (구 StopClass/ClassifyStopWindow[doc/25 신호 기반 분류]·ResolveWtStopBoundaryMs[정지 경계]·IsLongStopNonProduction·
    //  ResolveDowntimeAccrualMs[초과분 적립]·ResolveLogStopClass[대기 판정]·ResolveWaitMs 는 2026-09-11 doc/28 두 규칙
    //  모델로 삭제. 구 ClassifyGap/GapClass 는 2026-09-09 삭제.)

    /// <summary>
    /// '가동중' 박제 해제(abandon) 경계의 <b>자동 폴백</b>(ms) — 순수 함수.
    /// 사용자가 이상치 제외 Max 를 넣지 않았을 때(=0, 기본값) 워치독이 아예 동작하지 않아 CCTV 오버레이·
    /// 대시보드가 영구 '가동중'으로 박제되는 것을 막는다. 설비마다 사이클 길이가 수 초~수 분으로 달라
    /// 고정 초를 쓸 수 없으므로 flow 자신의 실측 분포에서 만든다(1.5s 라인=30초, 20s 라인=32분 수준).
    /// <list type="bullet">
    /// <item>중앙값 기준(<paramref name="medianMult"/>×) — 평균은 정지를 머금은 사이클(예: 주말 62시간
    ///   1건)에 끌려가므로 못 쓴다. 중앙값은 그 오염에 견딘다(실측: 중앙값 20.4s vs 평균 138s).</item>
    /// <item>p99 기준(<paramref name="p99Mult"/>×)과 함께 <b>더 큰 쪽</b> — 사이클이 들쭉날쭉한 설비에서
    ///   정상 장주기 사이클을 잘라 미기록시키지 않기 위한 여유. 둘 중 관대한 값을 택한다.</item>
    /// <item>표본 부족(&lt; <paramref name="minSample"/>)이면 0 = 해제 안 함(종전 동작). 부팅 직후 몇 건으로
    ///   경계를 만들어 정상 사이클을 자르는 것보다 박제를 잠깐 유지하는 쪽이 보수적이다.</item>
    /// <item>하한(<paramref name="floorMs"/>)은 <b>설비 사례가 아니라 관측 해상도</b>에서 온다 — 호출측이
    ///   워치독 판정 주기(StateReconcile tick)의 배수를 넣는다.</item>
    /// <item>상한(<paramref name="ceilingMs"/>) — p99 가 이상치를 물어도 언젠가는 해제되도록 보장.</item>
    /// </list>
    /// 이 값은 <b>워치독 전용</b>이다. IsIdle 박제·평균CT·OEE 집계에는 쓰지 않으므로 과거 수치가 바뀌지 않는다.
    /// </summary>
    public static double ResolveAutoAbandonBoundaryMs(
        double medianMs, double p99Ms, int sample,
        double floorMs, double ceilingMs = 6 * 60 * 60 * 1000,
        double medianMult = 20, double p99Mult = 3, int minSample = 5)
    {
        if (sample < minSample || medianMs <= 0) return 0;
        var byMedian = medianMs * medianMult;
        var byP99 = p99Ms > 0 ? p99Ms * p99Mult : 0;
        var boundary = Math.Max(byMedian, byP99);
        return Math.Clamp(boundary, floorMs, ceilingMs);
    }

    /// <summary>
    /// 품질 = (기간 사이클수 − 입력 불량) / 기간 사이클수 (doc/21 §12 개정).
    /// 분모는 항상 dspFlowHistory 기간 사이클수(자동) — production 행의 스냅샷 totalCount 를 분모로 쓰지 않는다.
    /// 일부 날만 불량을 입력해도 미입력일이 분모에서 빠지지 않아(미입력일 = 불량 0) 기간 품질이 왜곡되지 않고,
    /// "100% 에서 시작해 입력된 불량만큼 깎인다"는 운영 모델과 일치한다. 과거 날짜 불량 입력은 on-demand 재계산으로
    /// 즉시 소급 반영된다. 불량 데이터가 전혀 없으면 100% 가정(Source="assumed")을 값으로 제공하되 출처를 명시한다.
    /// </summary>
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
    /// 품질 결정 — 사용자가 직접 설정한 전반 품질(%)이 있으면 그 값을 우선(source="manual"). 미설정(null)이면
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
    /// NoFault=true 면 UI 가 "🟢 고장없음" 배지를 띄운다. (시프트 기반 요약 전용 — 사이클 모델은 <see cref="ComputeMtbf2"/>.)
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf(double runtimeMs, int failureCount)
    {
        if (failureCount <= 0)
            return (null, "고장(분류 unplanned) 건수 0 — 평균 고장 간격 산출 불가(고장없음).", true);
        return (runtimeMs / failureCount, "Σ가동시간 / 고장건수 (가동시간 = 가용성 분모와 동일 폴백).", false);
    }

    /// <summary>
    /// 표준CT 자동기입 후보 산출 (doc/21 §12 D): 클린샘플 ≥ minClean 이면 best-demonstrated p10(확정, "auto"),
    /// 그보다 적지만 ≥ minMedian 이면 중앙값(임시, "auto-median"), 그 외엔 산출 안 함(null).
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
    /// </summary>
    public static bool IsFailureReason(string? reasonCode)
        => string.Equals(reasonCode?.Trim(), "equipment_fault", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 행이 어떤 구간(수동 라벨·유지보수 이벤트)에 <b>과반</b> 덮였는가 — 행↔구간 조인의 단일 규칙
    /// (<see cref="MajorityCoverRatio"/>). '조금이라도 겹치면'은 경계 1~2초 스침으로 이웃 행을 오분류하고,
    /// '전부 덮이면'은 재도출로 경계가 1초만 어긋나도 라벨이 떨어진다. 과반이 두 오류를 모두 피한다.
    /// </summary>
    public static bool IsMajorityCovered(double rowMs, double overlapMs)
        => rowMs > 0 && overlapMs > rowMs * MajorityCoverRatio;

    /// <summary>
    /// 유지보수 확정 정지 판정 (2026-07-30) — 고장 행이 '분류된 비-고장 정지' 이벤트(GetDowntimeIntervalsAsync Kind 0=계획정비 /
    /// 2=계획외이나 isFailure=0)에 과반 덮이면 고장 통계(건수·MTBF onset·고장 사이클 평균 시간)에서 제외한다.
    /// <b>가용성(A)은 깎인 채로 둔다</b> — 의도된 정지라도 그 시간에 생산은 없었다(빠지는 건 '고장' 귀속뿐). doc/28 §2.6 꼬리표.
    /// </summary>
    public static bool IsMaintenanceCovered(double measuredMs, double maintOverlapMs)
        => IsMajorityCovered(measuredMs, maintOverlapMs);

    // ════════════════════════════════════════════════════════════════════════
    //  사이클기반 OEE (doc/22 — P5 v4 CT/MT/WT 모델, doc/26 행 집합, doc/28 두 규칙). 관측된 사이클 행을
    //  분모로 쓴다. 모두 순수함수 — 입력은 컨트롤러가 사이클에서 집계.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>한 사이클 행의 판정 결과 (doc/28 §1 — 정상 / 고장 / 비생산 3분류 + CT 없는 열린 행 Ignore).</summary>
    public enum CycleClass
    {
        /// <summary>CT 없는(마지막 열린) 사이클 — 집계 제외.</summary>
        Ignore,
        /// <summary>정상 — 행 전체가 가동(분자). 경계 아래의 느린 동작·긴 대기는 성능 P 가 속도 손실로 흡수.</summary>
        Normal,
        /// <summary>고장 — 행 전체가 A 손실(비가동), 고장 건수·MTBF 반영. 길이 무관 비생산으로 승격하지 않는다.</summary>
        Fault,
        /// <summary>비생산 — 행 전체가 분모 밖(생산가능시간 아님). 건수·MTBF 미반영.</summary>
        NonProduction,
    }

    /// <summary>
    /// 한 사이클 행을 정상/고장/비생산으로 분류 (doc/28 §1 SSOT — dtCond SQL 과 같은 규칙, 한쪽만 바꾸지 말 것).
    /// <list type="bullet">
    ///   <item>완료 행(mt 있음): ①-a <c>mt &gt; mtFaultMs</c> → 고장 / ②-a <c>wt ≥ wtNonProdMs</c> → 비생산 / 나머지 정상.
    ///     wt 가 비어 있으면 <c>ct − mt</c>(행 단위 항등 CT = MT + WT).</item>
    ///   <item>불인정 행(mt NULL): ①-b <c>ct &gt; ctFaultMs</c> → 고장 / ②-b <c>ct ≥ ctNonProdMs</c> → 비생산 / 나머지 정상.</item>
    /// </list>
    /// 경계 ≤ 0 은 그 절이 비활성(예: MT 기준선 미보유 flow 는 mtFaultMs=ctFaultMs=0 — 고장 판별 불가).
    /// <paramref name="sampleCount"/> &lt; <see cref="MinBaselineSamples"/> 면 표본 게이트 — 전부 정상.
    /// 사용자 라벨(고장으로/비생산으로)·비생산 지정 시각대는 이 함수 밖에서 호출측이 우선 적용한다(§2.6).
    /// </summary>
    public static CycleClass ClassifyCycle(
        int? mt, int? ct, int? wt,
        double mtFaultMs, double ctFaultMs, double wtNonProdMs, double ctNonProdMs, int sampleCount)
    {
        if (ct is not int c || c <= 0) return CycleClass.Ignore;
        if (sampleCount < MinBaselineSamples) return CycleClass.Normal;
        if (mt is int m)
        {
            if (mtFaultMs > 0 && m > mtFaultMs) return CycleClass.Fault;                       // ①-a
            var w = wt is int w0 && w0 >= 0 ? w0 : Math.Max(0, c - m);
            if (wtNonProdMs > 0 && w >= wtNonProdMs) return CycleClass.NonProduction;         // ②-a
            return CycleClass.Normal;
        }
        if (ctFaultMs > 0 && c > ctFaultMs) return CycleClass.Fault;                           // ①-b
        if (ctNonProdMs > 0 && c >= ctNonProdMs) return CycleClass.NonProduction;             // ②-b
        return CycleClass.Normal;
    }

    /// <summary>
    /// 사이클기반 가용성 A = Σ실측CT / (Σ실측CT + Σ비가동CT) (doc/22 §4). 분모 0 이면 null + 사유.
    /// (TEEP 매트릭스 셀 전용 — 요약 KPI 는 <see cref="ComputeWallClockAvailability"/> 벽시계 모델.)
    /// </summary>
    /// <remarks>인자는 반드시 <b>구간 합집합(union)의 총량</b>을 넘길 것 — CT 를 단순 합산하면 오염된 이력에서
    /// 사이클끼리 겹쳐 달력을 초과한다(실측: Σct 7.63h / 창 3.35h). union 은 창을 넘을 수 없다.</remarks>
    public static (double? Availability, string? Note) ComputeCycleAvailability(double normalCtMs, double idleCtMs)
    {
        var denom = normalCtMs + idleCtMs;
        if (denom <= 0)
            return (null, "기간 내 사이클 CT 합 0 — 가용성 산출 불가(수집된 정상 가동이 없습니다).");
        return (Math.Clamp(normalCtMs / denom, 0, 1),
            "Σ실측CT ÷ (Σ실측CT + Σ고장CT). 고장 = 판정 기준을 넘긴 사이클 행 전체.");
    }

    /// <summary>
    /// 벽시계 가용성 A = Σ가동 ÷ Σ생산가능시간 (2026-07-06 단일 모델, doc/28 §2.7). 세 뷰(추이·정산·도넛) 공통 SSOT.
    ///   생산가능 = 캘린더 − 비생산 − 미계측 − 진행 중 (− 분기 형제가동)
    ///   가동     = 정상 행이 실제 돈 구간(runIntervals ∩ 생산가능)
    ///   비가동   = 생산가능 − 가동 (잔여 = 유지보수 + 고장. 행이 연속이라 그 밖의 잔여 '미귀속'은 0 이어야 정상)
    /// 라인(다-Flow)은 호출측이 flow별 합산으로 넘긴다. 분모 0 이면 null + 사유.
    /// </summary>
    public static (double? Availability, string? Note) ComputeWallClockAvailability(double runWallMs, double availableWallMs)
    {
        if (availableWallMs <= 0)
            return (null, "생산가능시간 0(전 기간 비생산/미계측/진행 중) — 가용성 산출 불가.");
        return (Math.Clamp(runWallMs / availableWallMs, 0, 1),
            "가동(벽시계) ÷ 생산가능시간(캘린더 − 비생산 − 미계측). 비가동 = 생산가능 − 가동 = 고장 + 유지보수.");
    }

    /// <summary>
    /// 사이클기반 성능 P = (N × 표준 CT) / Σ실측CT, min 1.0 (doc/22 §4). <b>표준 CT = 14일 중앙 CT</b>(doc/28 §2.2 —
    /// 종전 평균 CT 는 정지를 머금은 행에 끌려 P 가 100% 에 고정됐다. 실측 현장 3000: kit 평균 1,000s vs 중앙 6.5s).
    /// 정상상태에서 P≈100% 로 수렴 — "최속 대비 손실"이 아니라 "14일 추세 대비 당기 저하" 지표(§6 ①).
    /// 표준 CT 미산출(표본 부족) 또는 정상 사이클 0 이면 null + 사유.
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
            return (null, "표준 CT(14일 중앙) 미산출 — 성능 산출 불가(클린샘플 부족).");
        return (Math.Min(1.0, normalCycleCount * thr / normalCtMs),
            "(정상 사이클수 × 표준 CT[14일 중앙]) ÷ Σ실측CT. 14일 추세 대비 당기 속도저하 지표.");
    }

    /// <summary>
    /// MTBF (doc/22 §5 / doc/28 §2.1) = 연속 고장 onset 간격 평균. onset = 고장 행의 <b>시작</b>(오름차순 ms).
    /// 0건이면 고장없음 배지, 1건이면 간격 없음(산출 불가).
    /// </summary>
    public static (double? Mtbf, string? Note, bool NoFault) ComputeMtbf2(IReadOnlyList<double> onsetsAscMs)
    {
        if (onsetsAscMs is null || onsetsAscMs.Count == 0)
            return (null, "고장 0건 — 평균 고장 간격 산출 불가(고장없음).", true);
        if (onsetsAscMs.Count < 2)
            return (null, "고장 1건 — 연속 onset 간격 없음(평균 고장 간격 산출 불가).", false);

        double sum = 0; int gaps = 0;
        for (int i = 1; i < onsetsAscMs.Count; i++)
        {
            var g = onsetsAscMs[i] - onsetsAscMs[i - 1];
            if (g > 0) { sum += g; gaps++; }
        }
        if (gaps == 0) return (null, "유효 onset 간격 없음 — 평균 고장 간격 산출 불가.", false);
        return (sum / gaps, "연속 고장 onset(행 시작) 간격 평균.", false);
    }

    /// <summary>
    /// 고장 사이클 평균 시간 (doc/28 §2.1 — 종전 MTTR 자리, API 필드명 <c>mttr</c> 유지). 입력은 고장 행 각각의
    /// <b>사이클 전체 길이(ct)</b> — 초과분이 아니다(행 안에서 고장이 언제 시작됐는지 모르므로 행이 단위).
    /// 정상 사이클 1개 분량이 포함되므로 "수리 시간"으로 읽지 말 것(화면 ⓘ). 음수 방어, 빈 입력이면 산출 불가.
    /// </summary>
    public static (double? Mttr, string? Note) ComputeMttr(IReadOnlyList<double> faultRowCtMsList)
    {
        var valid = (faultRowCtMsList ?? new List<double>()).Where(x => x >= 0).ToList();
        if (valid.Count == 0)
            return (null, "고장 사이클 없음 — 고장 사이클 평균 시간 산출 불가.");
        return (valid.Average(), "고장으로 판정된 사이클의 전체 길이 평균(사이클 정상분 포함 — 초과량은 정지 로그 행 노트).");
    }

    /// <summary>
    /// 생산효율 TEEP = 가동(Σ실측CT) ÷ 캘린더시간 (전체, 비생산 포함) — 표준 TEEP(24×365 관점, P6 생산효율 탭).
    /// 단순 가동형: 분자=가동시간만(P·Q 미반영 — 설비효율 탭이 A·P·Q 담당). 캘린더 ≤ 0 이면 null.
    /// 라인은 호출측이 캘린더=기간×flow수 로 넘긴다(가동이 flow별 합산이므로 분모도 배수 — 병렬 flow 과다계상 방지).
    /// 비생산으로 전환해도 TEEP 는 변하지 않는다(달력 손실 그대로) — 화면 ⓘ 한 줄(doc/28 §2.7).
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
