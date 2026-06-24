// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text.Json;

namespace DSPilot.Infrastructure;

/// <summary>
/// 실측 duration 확정 상태 사이드카 (calibration-state.json). AASX 모델과 분리된 런타임 확정 메타.
/// Promaker.Shared.CalibrationState 의 DSPilot 복제(B안) — 동일 스키마/파일/camelCase 직렬화여야
/// Promaker·Agent 와 같은 사이드카를 읽고 쓴다(필드/포맷 한 글자라도 어긋나면 stale).
///
/// 용도: ActionUnder(Min 보다 빨리 끝남)·ActionOver(Max 초과) 판정 게이트의 SSOT.
///   - 사용자가 실측으로 확정한 Work 만 MinMeasured/MaxMeasured=true → 해당 이상 판정 활성.
///   - AasxSha256 이 현재 AASX 해시와 다르면(모델 변경) 전체 stale → 재확정 전까지 비활성.
///
/// 모델(AASX)을 건드리지 않고, 동시 저장 충돌은 <see cref="SharedWriteLock"/> 으로 직렬화한다.
/// </summary>
public sealed class CalibrationState
{
    /// <summary>이 상태가 확정된 시점의 AASX SHA-256. 현재 AASX 와 다르면 전체 stale.</summary>
    public string AasxSha256 { get; set; } = "";

    /// <summary>Work GUID("D" 포맷 문자열) → 확정 메타.</summary>
    public Dictionary<string, WorkCalib> Works { get; set; } = new();

    public sealed class WorkCalib
    {
        public bool MinMeasured { get; set; }
        public int MinMs { get; set; }
        public bool MaxMeasured { get; set; }
        public int MaxMs { get; set; }
        public string MeasuredAtUtc { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string Key(Guid workGuid) => workGuid.ToString("D");

    /// <summary>사이드카 로드 — 없거나 손상이면 빈 상태.</summary>
    public static CalibrationState Load()
    {
        try
        {
            var path = SharedPaths.CalibrationStateJsonPath;
            if (!File.Exists(path)) return new CalibrationState();
            return JsonSerializer.Deserialize<CalibrationState>(File.ReadAllText(path), JsonOpts)
                   ?? new CalibrationState();
        }
        catch { return new CalibrationState(); }
    }

    /// <summary>사이드카 저장. 호출자가 <see cref="SharedWriteLock"/> 안에서 read-modify-write 하는 것을 전제.</summary>
    public bool TrySave()
    {
        try
        {
            Directory.CreateDirectory(SharedPaths.AgentDirectory);
            File.WriteAllText(SharedPaths.CalibrationStateJsonPath, JsonSerializer.Serialize(this, JsonOpts));
            return true;
        }
        catch { return false; }
    }

    /// <summary>해당 Work 의 Min 실측 확정 여부 — 현재 AASX 해시와 일치할 때만 유효(모델 변경 시 stale 제외).</summary>
    public bool IsMinMeasured(Guid workGuid, string currentAasxSha256)
    {
        if (string.IsNullOrEmpty(AasxSha256) || AasxSha256 != currentAasxSha256) return false;   // stale
        return Works.TryGetValue(Key(workGuid), out var w) && w.MinMeasured;
    }

    /// <summary>해당 Work 의 Max 실측 확정 여부 — 현재 AASX 해시와 일치할 때만 유효. ActionOver 게이트가 사용.</summary>
    public bool IsMaxMeasured(Guid workGuid, string currentAasxSha256)
    {
        if (string.IsNullOrEmpty(AasxSha256) || AasxSha256 != currentAasxSha256) return false;   // stale
        return Works.TryGetValue(Key(workGuid), out var w) && w.MaxMeasured;
    }

    /// <summary>한 Work 의 Min 실측 확정 기록(in-memory, 누적). 호출자가 락 안에서 호출 후 <see cref="TrySave"/>.
    /// 같은 Work 의 Max 확정은 보존한다(같은 실측 적용이 Min/Max 를 함께 박을 수 있음).</summary>
    public void SetMinMeasured(Guid workGuid, int minMs, string aasxSha256)
    {
        var w = EnsureWork(workGuid, aasxSha256);
        w.MinMeasured = true;
        w.MinMs = minMs;
        w.MeasuredAtUtc = DateTime.UtcNow.ToString("o");
    }

    /// <summary>한 Work 의 Max 실측 확정 기록(in-memory, 누적). Min 확정은 보존.</summary>
    public void SetMaxMeasured(Guid workGuid, int maxMs, string aasxSha256)
    {
        var w = EnsureWork(workGuid, aasxSha256);
        w.MaxMeasured = true;
        w.MaxMs = maxMs;
        w.MeasuredAtUtc = DateTime.UtcNow.ToString("o");
    }

    /// <summary>모델 해시가 바뀌었으면(다른 모델) 기존 확정 전체를 비우고 해시 갱신 후, 해당 Work 의 WorkCalib 를 확보.</summary>
    private WorkCalib EnsureWork(Guid workGuid, string aasxSha256)
    {
        if (AasxSha256 != aasxSha256)
        {
            AasxSha256 = aasxSha256;
            Works.Clear();   // 모델이 바뀌면 이전 확정은 무효 — 새 기준으로 재시작
        }
        var key = Key(workGuid);
        if (!Works.TryGetValue(key, out var w)) { w = new WorkCalib(); Works[key] = w; }
        return w;
    }

    /// <summary>한 Work 의 Min/Max 실측 확정을 모두 해제(Min/Max 초기화 시). 락 안에서 호출 후 <see cref="TrySave"/>.</summary>
    public void ClearWork(Guid workGuid) => Works.Remove(Key(workGuid));
}
