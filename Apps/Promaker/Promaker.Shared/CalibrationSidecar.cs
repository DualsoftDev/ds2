using System;
using System.Collections.Generic;

namespace Promaker.Shared;

/// <summary>
/// Promaker 가 모델 Work duration(Min/Max)을 고쳐 쓴 뒤 calibration-state 사이드카를 정합시키는 진입점.
/// 학습 반영·일괄 편집·속성창 인라인 편집 등 모든 duration 쓰기 경로가 저장 성공 직후 호출한다.
///
/// 하는 일 = 어긋난 확정 해제뿐, 도장 신규 발행은 하지 않는다 — 이유는 <see cref="CalibrationState.ReconcileWork"/> 참조.
/// 이 훅이 없으면 duration 쓰기마다 사이드카가 stale 로 남아 calibration-status 가 미스터리 수치를 보이고
/// (2026-08-24 우진 stale 26건), Agent 게이트가 "확정됐다가 어긋난 것"과 "애초에 미확정"을 구분 못 한다.
/// </summary>
public static class CalibrationSidecar
{
    /// <summary>duration 쓰기 결과를 사이드카에 정합. 해제된 Work 수를 반환(0 = 변경 없음 또는 락 미획득).
    /// 락 미획득 시 조용히 건너뛴다 — 게이트는 값 대조로 자기 무효화되므로 판정은 어긋나지 않고,
    /// 다음 duration 쓰기 또는 DSPilot 실측 보정이 다시 정합시킨다.</summary>
    public static int ReconcileAfterDurationWrite(
        IReadOnlyCollection<(Guid WorkId, int? MinMs, int? MaxMs)> applied)
    {
        if (applied.Count == 0) return 0;
        if (!SharedWriteLock.TryAcquire("Promaker", out _)) return 0;
        try
        {
            var state = CalibrationState.Load();
            if (state.Works.Count == 0) return 0;
            var cleared = 0;
            foreach (var (workId, minMs, maxMs) in applied)
                if (state.ReconcileWork(workId, minMs, maxMs))
                    cleared++;
            if (cleared > 0)
                state.TrySave();
            return cleared;
        }
        finally
        {
            SharedWriteLock.Release("Promaker");
        }
    }
}
