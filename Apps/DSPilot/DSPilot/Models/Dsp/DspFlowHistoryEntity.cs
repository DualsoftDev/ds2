// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Dsp;

/// <summary>
/// Flow 히스토리 엔티티 - dsp.db의 dspFlowHistory 테이블
/// 각 사이클 완료 시 MT, WT, CT 값을 기록
/// </summary>
public class DspFlowHistoryEntity
{
    /// <summary>
    /// History ID (자동 증가)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Flow 이름
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// Machine Time (ms) - 실제 작업 시간
    /// </summary>
    public int? MT { get; set; }

    /// <summary>
    /// Wait Time (ms) - 대기 시간
    /// </summary>
    public int? WT { get; set; }

    /// <summary>
    /// Cycle Time (ms) - MT + WT
    /// </summary>
    public int? CT { get; set; }

    /// <summary>
    /// 사이클 번호
    /// </summary>
    public int? CycleNo { get; set; }

    /// <summary>
    /// 기록 시간
    /// </summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>
    /// 비가동 사이클 여부 (CT > MaxCycleTimeMs 또는 CT < MinCycleTimeMs)
    /// </summary>
    public bool IsIdle { get; set; } = false;

    /// <summary>
    /// 이 사이클이 측정된 시점의 head Call 이름 (boundary 박제 — 사후 분석/필터링용).
    /// head/tail 이 바뀐 후의 데이터와 섞이지 않도록 row 별로 정의를 보존.
    /// </summary>
    public string? HeadCallName { get; set; }

    /// <summary>
    /// 이 사이클이 측정된 시점의 tail Call 이름 (boundary 박제).
    /// </summary>
    public string? TailCallName { get; set; }
}
