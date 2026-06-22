using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>
/// 실측 duration 확정 상태 사이드카 (calibration-state.json). AASX 모델과 분리된 런타임 확정 메타.
/// Work 별 "Min 실측 확정(MinMeasured)" 여부 + 확정 시점 AASX 해시(AasxSha256)를 담는다.
///
/// 용도: ActionUnder(동작이 Min 보다 빨리 끝남 = 시간 미만) 판정 게이트의 SSOT.
///   - 사용자가 'Min 실측 기록(FillMin)' 으로 실측값을 확정한 Work 만 MinMeasured=true → ActionUnder 활성.
///   - AasxSha256 이 현재 AASX 해시와 다르면(모델 변경) 전체 stale → 재확정 전까지 ActionUnder 비활성.
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

    /// <summary>해당 Work 의 Min 실측 확정 여부 — 현재 AASX 해시와 일치할 때만 유효(모델 변경 시 stale 제외).
    /// ActionUnder 어댑터 게이트가 이 결과로 발행을 거른다.</summary>
    public bool IsMinMeasured(Guid workGuid, string currentAasxSha256)
    {
        if (string.IsNullOrEmpty(AasxSha256) || AasxSha256 != currentAasxSha256) return false;   // stale
        return Works.TryGetValue(Key(workGuid), out var w) && w.MinMeasured;
    }

    /// <summary>한 Work 의 Min 실측 확정 기록(in-memory). 호출자가 락 안에서 호출 후 <see cref="TrySave"/>.
    /// 모델 해시가 바뀌었으면(다른 모델) 기존 확정을 비우고 해시를 갱신해 새 모델 기준으로 누적 시작.</summary>
    public void SetMinMeasured(Guid workGuid, int minMs, string aasxSha256)
    {
        if (AasxSha256 != aasxSha256)
        {
            AasxSha256 = aasxSha256;
            Works.Clear();   // 모델이 바뀌면 이전 확정은 무효 — 새 기준으로 재시작
        }
        Works[Key(workGuid)] = new WorkCalib
        {
            MinMeasured = true,
            MinMs = minMs,
            MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>한 Work 의 Min 실측 확정을 해제(Min/Max 초기화 시). 호출자가 락 안에서 호출 후 <see cref="TrySave"/>.</summary>
    public void ClearMinMeasured(Guid workGuid) => Works.Remove(Key(workGuid));
}
