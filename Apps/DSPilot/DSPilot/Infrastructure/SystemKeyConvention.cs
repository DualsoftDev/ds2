// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Infrastructure;

/// <summary>
/// 멀티 PLC 복합키 (SystemId, 주소) 의 SystemId 표기 SSOT.
///
/// `plc.systemId` 컬럼에는 System Guid 가 <b>소문자 "D" 포맷</b>(하이픈 포함, 중괄호 없음)으로
/// 저장된다. 조회 스코프·캐시 키·정의 인덱스가 모두 이 표기를 문자열 비교로 맞추므로, 한 곳이라도
/// 대문자/"N" 포맷으로 어긋나면 예외 없이 조회 결과가 조용히 0건이 된다. 그래서 각 소비자가
/// 직접 <c>ToString("D").ToLowerInvariant()</c> 하지 않고 이 헬퍼만 쓴다.
/// </summary>
public static class SystemKeyConvention
{
    /// <summary>
    /// 인메모리 키용 표기. 귀속 미상(null/Empty/파싱 실패)은 빈 문자열 — 키 조합에서 "System 부분 없음"
    /// 을 뜻하고, 딕셔너리 키로 그대로 쓸 수 있다.
    /// </summary>
    public static string Key(Guid? systemId) =>
        systemId.HasValue && systemId.Value != Guid.Empty
            ? systemId.Value.ToString("D").ToLowerInvariant()
            : string.Empty;

    /// <inheritdoc cref="Key(Guid?)"/>
    public static string Key(string? systemId) =>
        string.IsNullOrWhiteSpace(systemId) ? string.Empty
        : Guid.TryParse(systemId, out var g) ? Key(g) : string.Empty;

    /// <summary>
    /// SQL 파라미터용 표기. 귀속 미상은 <c>null</c> — <c>(@SystemId IS NULL OR p.systemId = @SystemId)</c>
    /// 패턴에서 "스코프 없음 = 전체" 로 동작한다(빈 문자열을 넘기면 systemId='' 인 행만 매칭되어
    /// 결과가 0건이 되므로 <see cref="Key(Guid?)"/> 를 그대로 쓰면 안 된다).
    /// </summary>
    public static string? Scope(Guid? systemId)
    {
        var key = Key(systemId);
        return key.Length == 0 ? null : key;
    }
}
