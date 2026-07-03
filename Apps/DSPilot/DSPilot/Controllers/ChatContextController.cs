// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using DSPilot.Models.Dashboard;
using DSPilot.Models.Dsp;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 설비박사 챗봇(PlantDoctorAI.ChatBot)용 경량 컨텍스트 API.
/// GET /api/chat/context — 현재 시점의 핵심 지표를 하나의 JSON 으로 반환 (LLM 시스템 프롬프트 주입용).
/// 개별 9개 API 호출 대신 1회 호출로 컨텍스트 수집 시간을 단축한다.
/// </summary>
[ApiController]
[Route("api/chat")]
public class ChatContextController : ControllerBase
{
    private readonly DspDbService _db;
    private readonly AbnormalEventService _abnormal;
    private readonly UserTagAlertService _userTags;
    private readonly AppSettingsService _settings;
    private readonly ILogger<ChatContextController> _logger;

    public ChatContextController(
        DspDbService db,
        AbnormalEventService abnormal,
        UserTagAlertService userTags,
        AppSettingsService settings,
        ILogger<ChatContextController> logger)
    {
        _db = db;
        _abnormal = abnormal;
        _userTags = userTags;
        _settings = settings;
        _logger = logger;
    }

    // ── GET /api/chat/context ─────────────────────────────────────────────
    /// <summary>
    /// LLM 시스템 프롬프트에 주입할 설비 현황 스냅샷.
    /// flows: 각 Flow 의 현재 상태 + CT.
    /// alarms: 활성 알람 요약 (최대 20건).
    /// capturedAt: UTC ISO8601.
    /// </summary>
    [HttpGet("context")]
    public ActionResult<ChatContextDto> GetContext([FromQuery] int alarmLimit = 20)
    {
        _logger.LogDebug("[ChatContext] 컨텍스트 요청");

        var snap = _db.Snapshot;

        // Flow 상태 요약
        var flows = snap.Flows.Select(f => new ChatFlowDto(
            f.FlowName,
            f.State,
            f.CT, f.AvgCT,
            f.MT, f.WT,
            f.MovingStartName, f.MovingEndName)).ToList();

        // 활성 알람 — abnormal 이벤트만 (usertag 는 별도 엔드포인트)
        var n = Math.Clamp(alarmLimit, 1, 100);
        var alarms = _abnormal.GetActive(n).Select(a => new ChatAlarmDto(
            a.Level, a.KindName, a.FlowName, a.WorkName, a.ElapsedMs)).ToList();

        return Ok(new ChatContextDto(
            flows,
            alarms,
            TotalFlows:   flows.Count,
            RunningFlows: flows.Count(f => f.State == "Running"),
            AlarmCount:   alarms.Count,
            CapturedAt:   DateTimeOffset.UtcNow));
    }
}

// ── DTOs (이 파일 전용) ──────────────────────────────────────────────────────

public record ChatContextDto(
    List<ChatFlowDto> Flows,
    List<ChatAlarmDto> Alarms,
    int TotalFlows,
    int RunningFlows,
    int AlarmCount,
    DateTimeOffset CapturedAt);

public record ChatFlowDto(
    string FlowName,
    string? State,
    int? CT, double? AvgCT,
    int? MT, int? WT,
    string? MovingStartName, string? MovingEndName);

public record ChatAlarmDto(
    string Level,
    string KindName,
    string FlowName,
    string? WorkName,
    long? ElapsedMs);
