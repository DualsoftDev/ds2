// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using DSPilot.Models.Dsp;

namespace DSPilot.Repositories;

/// <summary>
/// DSP 실시간 데이터베이스 저장소 인터페이스
/// </summary>
public interface IDspRepository
{
    /// <summary>
    /// 스키마 생성 (Call, Flow, CallFlowView)
    /// </summary>
    Task<bool> CreateSchemaAsync();

    /// <summary>
    /// Flow 대량 삽입 (AASX 초기 로드용)
    /// </summary>
    Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows);

    /// <summary>
    /// Call 대량 삽입 (AASX 초기 로드용)
    /// </summary>
    Task<int> BulkInsertCallsAsync(List<DspCallEntity> calls);

    /// <summary>
    /// Call 상태 조회 (CallId 기반)
    /// </summary>
    Task<string> GetCallStateAsync(Guid callId);

    /// <summary>
    /// Call 정보 조회 (WorkName, FlowName 포함)
    /// </summary>
    Task<(string WorkName, string FlowName)?> GetCallInfoAsync(Guid callId);

    /// <summary>
    /// Call 전체 데이터 조회 (GoingCount 등 포함, CallId 기반)
    /// </summary>
    Task<DspCallEntity?> GetCallByIdAsync(Guid callId);

    /// <summary>
    /// Call 상태 업데이트 (CallId 기반)
    /// </summary>
    Task<bool> UpdateCallStateAsync(Guid callId, string state);

    /// <summary>
    /// Call 상태 및 통계 업데이트 (Going → Finish 시, CallId 기반)
    /// </summary>
    Task<bool> UpdateCallWithStatisticsAsync(
        Guid callId,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime);

    /// <summary>
    /// 여러 Call 의 동작편차 누적 통계(실행수/평균/표준편차)를 <b>절대값</b>으로 일괄 덮어쓴다(상태는 미변경).
    /// 라이브 누산기가 캡 적용 전 누적한 이상치 오염을 원시 엣지 재도출값으로 청소(self-heal)하는 배치 경로.
    /// (단건 증분 <see cref="UpdateCallWithStatisticsAsync"/> 와 달리 GoingCount 를 +1 하지 않고 통째로 SET 한다.)
    /// </summary>
    Task<int> SetCallGoingStatisticsAsync(
        IReadOnlyList<(Guid CallId, int Count, double Avg, double StdDev)> stats);

    /// <summary>
    /// Flow 상태 업데이트
    /// </summary>
    Task<bool> UpdateFlowStateAsync(string flowName, string state);

    /// <summary>
    /// Flow 내 Going 상태 Call 존재 여부 확인
    /// </summary>
    Task<bool> HasGoingCallsInFlowAsync(string flowName);

    /// <summary>
    /// Flow 메트릭 업데이트 (MT, WT, CT, MovingStartName, MovingEndName)
    /// </summary>
    Task<bool> UpdateFlowMetricsAsync(
        string flowName,
        int? mt,
        int? wt,
        int? ct,
        string? movingStartName,
        string? movingEndName);

    /// <summary>
    /// 전체 데이터 삭제 (재초기화용)
    /// </summary>
    Task<bool> ClearAllDataAsync();

    /// <summary>
    /// 데이터베이스 정리 (중복 제거, 무결성 검사)
    /// </summary>
    Task CleanupDatabaseAsync();

    /// <summary>
    /// Heatmap용 Call 통계 데이터 조회 (GoingCount > 0인 항목만)
    /// </summary>
    Task<List<CallStatisticsDto>> GetCallStatisticsAsync();

}

/// <summary>
/// Call 통계 DTO
/// </summary>
public class CallStatisticsDto
{
    public Guid CallId { get; set; }
    public string CallName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public double AverageGoingTime { get; set; }
    public double StdDevGoingTime { get; set; }
    public int GoingCount { get; set; }
}
