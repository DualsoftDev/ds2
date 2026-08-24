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
///   - 게이트 유효성은 Work 별 실측 duration 값(MinMs/MaxMs)이 현재 모델 duration 과 일치하는지로 판정한다.
///     usertag·이름·좌표 등 duration 무관 편집엔 게이트 유지, duration 이 실제로 바뀐 Work 만 재확정 요구
///     (AasxSha256 은 마지막 확정 시점 기록용 정보 필드 — 게이트 판정에는 쓰지 않는다).
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

    public static bool TryLoadExact(string path, out CalibrationState? state, out string error)
    {
        state = null;
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = $"Calibration state not found at '{path}'.";
                return false;
            }
            state = JsonSerializer.Deserialize<CalibrationState>(File.ReadAllText(path), JsonOpts);
            if (state is null)
            {
                error = "Calibration state JSON was empty.";
                return false;
            }
            state.Works ??= new Dictionary<string, WorkCalib>();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Calibration state JSON is invalid: {ex.Message}";
            return false;
        }
    }

    /// <summary>사이드카 저장. 호출자가 <see cref="SharedWriteLock"/> 안에서 read-modify-write 하는 것을 전제.</summary>
    public bool TrySave()
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(SharedPaths.AgentDirectory);
            temp = SharedPaths.CalibrationStateJsonPath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(temp, SharedPaths.CalibrationStateJsonPath, overwrite: true);
            return true;
        }
        catch
        {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    /// <summary>해당 Work 의 Min 실측 확정 여부 — 저장된 실측 MinMs 가 현재 모델의 Min duration(currentMinMs)과
    /// 같을 때만 유효. raw AASX 해시(usertag·이름 등 duration 무관 편집에도 바뀜) 대신 Work 별 duration 값으로
    /// stale 판정한다: duration 을 안 건드린 편집은 게이트 유지(GUID 승계), duration 이 바뀐 Work 만 재확정 요구.
    /// ActionUnder 어댑터 게이트가 이 결과로 발행을 거른다.</summary>
    public bool IsMinMeasured(Guid workGuid, int currentMinMs)
        => Works.TryGetValue(Key(workGuid), out var w) && w.MinMeasured && w.MinMs == currentMinMs;

    /// <summary>해당 Work 의 Max 실측 확정 여부 — 저장된 실측 MaxMs 가 현재 모델의 Max duration(currentMaxMs)과
    /// 같을 때만 유효. 판정 근거는 IsMinMeasured 와 동일(duration 값 대조, GUID 승계). ActionOver 게이트가 사용.</summary>
    public bool IsMaxMeasured(Guid workGuid, int currentMaxMs)
        => Works.TryGetValue(Key(workGuid), out var w) && w.MaxMeasured && w.MaxMs == currentMaxMs;

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

    /// <summary>해당 Work 의 WorkCalib 확보. 다른 Work 의 확정은 보존한다(부분 재확정이 나머지를 날리지 않음 = GUID 승계).
    /// 게이트 유효성은 Work 별 duration 값 대조로 판정하므로 모델 변경 시 전체 무효화(Clear)가 필요 없다.</summary>
    private WorkCalib EnsureWork(Guid workGuid, string aasxSha256)
    {
        AasxSha256 = aasxSha256;   // 정보성 — 게이트 판정엔 미사용(Work 별 duration 값이 SSOT)
        var key = Key(workGuid);
        if (!Works.TryGetValue(key, out var w)) { w = new WorkCalib(); Works[key] = w; }
        return w;
    }

    /// <summary>한 Work 의 Min/Max 실측 확정을 모두 해제(Min/Max 초기화 시). 락 안에서 호출 후 <see cref="TrySave"/>.</summary>
    public void ClearWork(Guid workGuid) => Works.Remove(Key(workGuid));

    /// <summary>모델 duration 이 바뀐 뒤 사이드카 정합 — 새 모델값과 더 이상 일치하지 않는 확정만 해제.
    /// 값이 유지된 필드의 확정(게이트)은 보존하고, 도장을 새로 찍지는 않는다 — 학습/수동 값은 여유 없는
    /// 밴드값이라 실측 임계 확정이 아니다(ActionOver 문서 §1-2: 빡빡한 값에 도장을 찍으면 오탐).
    /// 게이트는 어차피 값 대조로 자기 무효화되므로 판정 동작은 동일하고, calibration-status 진단이
    /// '조용한 stale' 대신 '미확정(재확정 필요)' 로 정직해진다. 반환 = 상태 변경 여부(저장 필요).</summary>
    public bool ReconcileWork(Guid workGuid, int? currentMinMs, int? currentMaxMs)
    {
        if (!Works.TryGetValue(Key(workGuid), out var w)) return false;
        var changed = false;
        if (w.MinMeasured && (currentMinMs is null || w.MinMs != currentMinMs.Value))
        {
            w.MinMeasured = false;
            changed = true;
        }
        if (w.MaxMeasured && (currentMaxMs is null || w.MaxMs != currentMaxMs.Value))
        {
            w.MaxMeasured = false;
            changed = true;
        }
        if (changed && !w.MinMeasured && !w.MaxMeasured)
            Works.Remove(Key(workGuid));
        return changed;
    }
}
