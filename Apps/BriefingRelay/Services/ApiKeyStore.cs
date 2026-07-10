// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using BriefingRelay.Models;

namespace BriefingRelay.Services;

/// <summary>
/// API 키 검증 + 키별 일일 쿼터 카운터. 키 저장소는 설정(주입)에서 로드 — v1 은 인메모리 카운터(재시작 시 리셋).
/// 상수시간 비교로 타이밍 공격을 줄이고, 비활성 키·쿼터 초과를 여기서 판정한다.
/// 프로세스 단일 인스턴스 기준(스케일아웃 시 카운터는 외부 저장소로 이전 필요 — 보안 강화 항목).
/// </summary>
public sealed class ApiKeyStore
{
    private readonly Dictionary<string, ApiKeyEntry> _byKey;
    private readonly object _lock = new();
    // key -> (로컬 날짜, 오늘 사용량)
    private readonly Dictionary<string, (DateOnly Day, int Count)> _usage = new(StringComparer.Ordinal);

    public ApiKeyStore(RelayConfig cfg)
    {
        _byKey = new Dictionary<string, ApiKeyEntry>(StringComparer.Ordinal);
        foreach (var e in cfg.ApiKeys ?? [])
            if (!string.IsNullOrWhiteSpace(e.Key))
                _byKey[e.Key] = e;
    }

    /// <summary>설정된 키가 하나라도 있는지(없으면 서버는 fail-closed 로 전부 거부).</summary>
    public bool HasKeys => _byKey.Count > 0;

    /// <summary>제시된 키가 유효(존재+활성)하면 엔트리 반환. 상수시간 대조.</summary>
    public ApiKeyEntry? Validate(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey)) return null;
        // 존재 여부와 무관하게 모든 등록 키를 훑어 상수시간 비교(열거 자체는 키 수만큼 — 소규모라 무해).
        ApiKeyEntry? match = null;
        foreach (var kv in _byKey)
            if (FixedTimeEquals(kv.Key, presentedKey)) match = kv.Value;
        return match is { Disabled: false } ? match : null;
    }

    /// <summary>오늘 쿼터 내에서 count 건을 소비하면 true. 초과면 false(소비 안 함).</summary>
    public bool TryConsume(ApiKeyEntry entry, int count)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        lock (_lock)
        {
            var cur = _usage.TryGetValue(entry.Key, out var u) && u.Day == today ? u.Count : 0;
            if (cur + count > entry.DailyQuota) return false;
            _usage[entry.Key] = (today, cur + count);
            return true;
        }
    }

    // System.Security.Cryptography.CryptographicOperations.FixedTimeEquals 의 문자열판(길이 노출 최소화).
    private static bool FixedTimeEquals(string a, string b)
    {
        var diff = a.Length ^ b.Length;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ (i < b.Length ? b[i] : (char)0);
        return diff == 0;
    }
}
