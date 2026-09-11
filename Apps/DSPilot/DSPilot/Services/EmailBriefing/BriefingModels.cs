// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Oee;

namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// 하루치 브리핑에 담기는 집계 데이터(렌더링 입력). BriefingComposer 가 채우고 BriefingHtmlRenderer 가 소비한다.
/// 모든 시각은 "어제"(로컬 자정~자정) 기준.
/// </summary>
public sealed record BriefingData(
    DateOnly Day,                 // 대상 날짜(로컬, 어제)
    OeeSummaryDto Line,           // 라인 전체(flow=null) 생산 요약
    double? LineTeep,             // 라인 전체 TEEP(생산효율). null=산출 불가
    IReadOnlyList<FlowBrief> Flows,   // Flow별 요약(생산량 내림차순)
    int AbnormalTotal,            // 이상 총 건수(경로이탈+UserTag)
    int AbnormalCount,            // 경로이탈 자동감지 건수(ABNORMAL)
    int UserTagCount,             // 사용자정의 알람 건수(USERTAG)
    IReadOnlyList<BriefTopRow> TopAbnormal,   // 최다 발생 상위 항목
    // ── doc/28 §2.8 (2026-09-11) — 하루치(00:00~24:00 클립)에서 사람이 봐야 할 정지 ──
    //   ReviewStops = '확인 필요' 고장(길이가 비생산 기준 이상, 수동 라벨 없음 = 끄고 간 정지 후보) + 하루 경계를 넘는 고장 행(전일부터
    //   이어짐 / 다음 날로 이어짐 / 진행 중). 메일은 전환 전 숫자로 나가므로 상단에 건수·시간을 올려 월요일 아침 전환의 트리거가 된다.
    IReadOnlyList<BriefStopRow> ReviewStops,
    int ReviewPendingCount = 0,               // 요약 KPI 의 확인 필요 건수(라인)
    double ReviewPendingMs = 0);              // 그 행들의 계측 길이 합(하루 클립)

/// <summary>
/// 브리핑 특이사항 정지 한 줄 — 하루 경계 클립 전 원값(StartAt/EndAt)과 이날 몫(InDayMs)을 함께 담는다.
/// CrossesStart = 전일부터 이어짐, CrossesEnd = 다음 날로 이어짐, Open = 아직 진행 중(열린 사이클).
/// </summary>
public sealed record BriefStopRow(
    string Flow, DateTime StartAt, DateTime? EndAt, double DurationMs, double InDayMs,
    bool NeedsReview, bool CrossesStart, bool CrossesEnd, bool Open, string? Note);

/// <summary>Flow 1개의 생산 요약 한 줄.</summary>
public sealed record FlowBrief(string Name, double? Oee, int? Count, double DowntimeMs);

/// <summary>이상 최다 발생 상위 항목 한 줄.</summary>
public sealed record BriefTopRow(string Name, string Category, int Count);

/// <summary>발송/미리보기 결과(컨트롤러 응답용).</summary>
public sealed record BriefingSendResult(bool Sent, int RecipientCount, string Message);

/// <summary>미리보기(발송 없이 렌더만).</summary>
public sealed record BriefingPreview(string Subject, string Html);
