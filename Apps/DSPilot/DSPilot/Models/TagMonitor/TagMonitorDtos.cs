// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.TagMonitor;

// 태그 모니터링 API DTO (/api/tag-monitor/*). 전역 camelCase 로 직렬화된다.
//
// 시각은 <b>정수 epoch ms</b> 로 내려보낸다. 다른 화면들은 서버에서 로컬 문자열로 바꿔 주지만 여기는
// 차트 축이 주 소비자라 숫자가 그대로 필요하다(Chart.js time 축이 로컬로 렌더한다). doc/30 §9.1-1 과도 같은 규약.

/// <summary>
/// 모니터링 대상 1건. 정의는 AASX, 값은 signal 표에서 온다.
/// <para>TagId=null = 정의는 있으나 tag 행이 아직 없다(= 한 번도 수집되지 않았다).
/// InModel=false = 값은 쌓여 있는데 지금 모델엔 없는 주소다(지운 태그의 과거 이력).
/// Level = "Info"(모니터링TAG) | "Error"(이상알람TAG) — 목록은 둘 다 담고 화면이 기본 선택만 가른다.</para>
/// </summary>
public record TagMonitorTagDto(
    long? TagId,
    string SystemName,
    string SystemGuid,
    string Name,
    string Address,
    string ValueType,
    string Level,
    string? Unit,
    string? LastValue,
    long? LastAtMs,
    bool InModel);

/// <summary>ProjectLoaded=false 면 AASX 가 안 올라온 것이라 목록이 비어 있는 게 정상이다.</summary>
public record TagMonitorListDto(bool ProjectLoaded, List<TagMonitorTagDto> Tags);

/// <summary>
/// 값 하나. 계단 그래프라 "이 시각에 이 값으로 바뀌었다"는 뜻이다.
/// <para>Num = 수치 축에 그릴 값(문자열 태그는 null), Text = 원문(수치 태그는 대개 null).
/// 신호 표의 값 칸은 타입을 선언하지 않아 정수·실수·문자열이 한 칸에 담기므로 둘로 나눠 준다.</para>
/// </summary>
public record TagMonitorPointDto(long AtMs, double? Num, string? Text);

/// <summary>
/// 구간 통계. 값 타입에 따라 채워지는 칸이 다르다 — 수치가 아니면 Min/Max/Avg 는 null,
/// Bit 가 아니면 OnMs/OnRatio 는 null 이다. Changes 는 구간 안의 기록 건수(=값이 바뀐 횟수).
/// </summary>
public record TagMonitorStatsDto(
    int Changes,
    double? Min,
    double? Max,
    double? Avg,
    string? Last,
    long? OnMs,
    double? OnRatio);

/// <summary>
/// 태그 하나의 구간 시계열.
/// <para>StartValue = 구간 <b>직전</b> 마지막 값(계단 시작점). null 이면 구간 시작 전에 기록이 없었다.
/// Truncated=true 는 포인트를 솎았거나 스캔 상한에 걸렸다는 뜻 — 화면이 "기간을 좁히세요" 를 띄운다.</para>
/// </summary>
public record TagMonitorSeriesDto(
    long TagId,
    string Name,
    string SystemName,
    string Address,
    string ValueType,
    string? Unit,
    TagMonitorPointDto? StartValue,
    List<TagMonitorPointDto> Points,
    int TotalCount,
    bool Truncated,
    TagMonitorStatsDto Stats);

/// <summary>추이 조회 응답 — 구간(요청이 해석한 실제 범위)과 태그별 시계열.</summary>
public record TagMonitorSeriesResponseDto(long FromMs, long ToMs, List<TagMonitorSeriesDto> Series);
