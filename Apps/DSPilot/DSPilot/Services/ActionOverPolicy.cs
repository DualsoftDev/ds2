// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// ActionOver(동작 지연) 판정의 순수 규칙 — DSPilot 소유 판정(2026-08-24)의 SSOT.
/// IO/엔진/설정 접근 없이 숫자만 다룬다(<see cref="SimulationEngineService"/> 가 값을 주입).
/// </summary>
public static class ActionOverPolicy
{
    /// <summary>
    /// 모델 Max(ms) → ActionOver 임계(ms).
    ///
    /// <para>임계는 AASX 에 굽지 않고 판정 시점에 여유값을 더한다 — 그래야 Promaker 학습 반영 등으로
    /// 모델 밴드가 바뀌어도 임계가 자동으로 따라가고, "재측정 버튼을 눌러야 되살아나는" 상태가 안 생긴다.</para>
    ///
    /// <para><paramref name="marginAlreadyInModel"/> 는 이중 가산 방지 스위치다. DSPilot 의 실측 보정
    /// (<see cref="AutoCalibrationService"/>)은 Max 를 <c>max(중앙값×(1+여유율), 클린최대) + 여유값</c> 으로
    /// 산출해 <b>여유값이 포함된 값</b>을 AASX 에 쓰므로, 그 경우엔 더하지 않고 그대로 쓴다.
    /// 호출측은 calibration-state 사이드카 값이 현재 모델 Max 와 일치하는지로 이 플래그를 만든다.</para>
    /// </summary>
    /// <returns>임계(ms). 모델 Max 가 0 이하(미설정)면 0 — 호출측은 0 을 "판정 제외"로 다룬다.</returns>
    public static int ResolveThresholdMs(int modelMaxMs, int marginMs, bool marginAlreadyInModel)
    {
        if (modelMaxMs <= 0) return 0;
        if (marginAlreadyInModel) return modelMaxMs;
        return modelMaxMs + Math.Max(0, marginMs);
    }

    /// <summary>
    /// 완료대기 시계 판정 — OUT 상승(<paramref name="startMs"/>) 이후 IN 미도달 상태로 임계를 넘었는가.
    ///
    /// <para>OUT 하강은 시계를 지우지 않는다. 이 현장은 OUT 을 IN 도달까지 유지하다 IN 이 오면 내리는데,
    /// 동작이 실패하면 PLC 가 1~2초 만에 OUT 을 회수해 버린다(실측: Conveyor2.MOVE OUT 1.49초 뒤 회수,
    /// IN 은 4분 5초 뒤). 그래서 "OUT 유지 중 초과"(엔진 device-watchdog)도 "OUT 하강 시점 경과 &gt; Max"
    /// (어댑터 경로)도 둘 다 못 잡는다 — 명령 회수와 무관하게 IN 도달까지 재야 잡힌다.</para>
    /// </summary>
    /// <param name="alreadyEmitted">이 시계로 이미 발행했으면 true — 사이클당 1건 보장.</param>
    public static bool ShouldEmit(long startMs, long nowMs, int thresholdMs, bool alreadyEmitted)
    {
        if (alreadyEmitted || thresholdMs <= 0) return false;
        return nowMs - startMs > thresholdMs;
    }
}
