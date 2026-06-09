// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// 단일 head/tail Flow 의 배지 상태를 head-start→tail-complete 엣지 래치로 도출하는 <b>순수 함수</b> 모음.
/// going-any(OR-스캔)와 달리, head 가 시작하면 사이클이 열리고 tail 이 완료될 때까지 닫히지 않으므로
/// Body 구간(중간 Call 이 자기 센서로 Going 을 빠져나가 어느 Call 도 Going 이 아닌 순간)에도 "가동중"이 유지된다.
/// <para>FlowMetricsService / SimulationEngineService 가 래치 상태를 읽어 호출한다. 부수효과·시간 의존이 없어
/// 단위 테스트(DSPilot.Tests)가 직접 검증할 수 있도록 모든 입력을 인자로 받는다.</para>
/// </summary>
public static class FlowLatchBadge
{
    public const string Going = "Going";
    public const string Finish = "Finish";
    public const string Ready = "Ready";

    /// <summary>
    /// tail 완료(래치 close) 직후 "완료(Finish)"를 보장 표시하는 기본 hold(ms).
    /// DspDbService.SetFlowStateWithHold 의 기존 Tail-Finish hold(250ms)와 동일 — 별도 설정 키 불요.
    /// </summary>
    public const int FinishHoldMs = 250;

    /// <summary>
    /// 래치 스냅샷에서 배지 상태를 도출(eligible Flow 전용·순수).
    /// <list type="bullet">
    /// <item>IsCycleActive==true → "Going"</item>
    /// <item>!IsCycleActive && (now-PreviousCycleFinish) &lt; finishHoldMs → "Finish"</item>
    /// <item>그 외 → "Ready"</item>
    /// </list>
    /// </summary>
    public static string Compute(bool isCycleActive, DateTime? previousCycleFinish, DateTime now, int finishHoldMs = FinishHoldMs)
    {
        if (isCycleActive) return Going;
        if (previousCycleFinish.HasValue
            && (now - previousCycleFinish.Value).TotalMilliseconds >= 0
            && (now - previousCycleFinish.Value).TotalMilliseconds < finishHoldMs)
            return Finish;
        return Ready;
    }

    /// <summary>
    /// 래치 적격(LatchEligible) 판정(순수).
    /// <para>적격 = (명시 override 있음 || (Head 라벨 &amp;&amp; Tail 라벨 둘 다 있음)) || (토폴로지 HeadCount==1 &amp;&amp; TailCount==1).
    /// 단 head/tail 과 동명인 Call 이 여러 Work 에 있어 경계가 모호하면(headAmbiguous/tailAmbiguous) 무조건 강등.</para>
    /// 강등된 Flow 는 호출 측이 기존 going-any 폴백을 쓴다(회귀 0).
    /// </summary>
    public static bool IsEligible(
        bool hasExplicitOverride,
        bool hasHeadLabel,
        bool hasTailLabel,
        int topologyHeadCount,
        int topologyTailCount,
        bool headAmbiguous,
        bool tailAmbiguous)
    {
        if (headAmbiguous || tailAmbiguous) return false;
        bool labelled = hasExplicitOverride || (hasHeadLabel && hasTailLabel);
        bool singleTopology = topologyHeadCount == 1 && topologyTailCount == 1;
        return labelled || singleTopology;
    }

    /// <summary>
    /// Phase 2 워치독 abandon 판정(순수). 래치가 열린 채 경과가 유효 이상치 Max 를 넘으면 — 지금 완료돼도
    /// 어차피 비가동으로 분류될 사이클이므로 — 'Going 고정'으로 보고 해제 대상. maxMs&lt;=0(제한 없음)이면 해제 안 함.
    /// </summary>
    public static bool ShouldAbandon(bool isCycleActive, DateTime? cycleStart, int maxMs, DateTime now)
        => isCycleActive
           && maxMs > 0
           && cycleStart.HasValue
           && (now - cycleStart.Value).TotalMilliseconds > maxMs;
}
