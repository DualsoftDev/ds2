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
    IReadOnlyList<FlowBrief> Flows,   // Flow별 요약(생산량 내림차순)
    int AbnormalTotal,            // 이상 총 건수(경로이탈+UserTag)
    int AbnormalCount,            // 경로이탈 자동감지 건수(ABNORMAL)
    int UserTagCount,             // 사용자정의 알람 건수(USERTAG)
    IReadOnlyList<BriefTopRow> TopAbnormal);  // 최다 발생 상위 항목

/// <summary>Flow 1개의 생산 요약 한 줄.</summary>
public sealed record FlowBrief(string Name, double? Oee, int? Count, double DowntimeMs);

/// <summary>이상 최다 발생 상위 항목 한 줄.</summary>
public sealed record BriefTopRow(string Name, string Category, int Count);

/// <summary>발송/미리보기 결과(컨트롤러 응답용).</summary>
public sealed record BriefingSendResult(bool Sent, int RecipientCount, string Message);

/// <summary>미리보기(발송 없이 렌더만).</summary>
public sealed record BriefingPreview(string Subject, string Html);
