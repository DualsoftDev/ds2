using System.Text.Json;
using System.Text.Json.Nodes;
using DSPilot.Models;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] ManagedSections =
        ["Database", "FlowCycle", "DspTables", "Hub", "Logging", "Ui", "HistoryView", "Cctv", "OeeSignals", "Shift", "CycleExclusion"];

    private readonly string _filePath;
    private readonly string _productionFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IWebHostEnvironment env,
        ILogger<AppSettingsService> logger)
    {
        _filePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _productionFilePath = Path.Combine(env.ContentRootPath, "appsettings.Production.json");
        _logger = logger;
    }

    /// <summary>
    /// appsettings.json이 없으면 AppSettingsModel 기본값으로 자동 생성.
    /// WebApplication.CreateBuilder 전에 정적으로 호출.
    /// </summary>
    public static void EnsureSettingsFiles(string contentRootPath)
    {
        var appSettingsPath = Path.Combine(contentRootPath, "appsettings.json");

        if (File.Exists(appSettingsPath))
            return;

        var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
        File.WriteAllText(appSettingsPath, defaultJson);
        Console.WriteLine("[AppSettings] appsettings.json 생성됨 (기본값)");
    }

    public AppSettingsModel LoadSettings()
    {
        var root = LoadRaw(_filePath);

        // Production.json이 있으면 각 섹션을 오버라이드 (ASP.NET Core 설정 병합과 동일 방식)
        if (File.Exists(_productionFilePath))
        {
            var prod = LoadRaw(_productionFilePath);
            foreach (var key in ManagedSections)
            {
                if (prod[key] is not null)
                    root[key] = prod[key]!.DeepClone();
            }
        }

        return new AppSettingsModel
        {
            Database = Deserialize<DatabaseSettings>(root["Database"]),
            FlowCycle = Deserialize<FlowCycleSettings>(root["FlowCycle"]),
            DspTables = Deserialize<DspTablesSettings>(root["DspTables"]),
            Hub = Deserialize<HubSettings>(root["Hub"]),
            Logging = Deserialize<LoggingSettings>(root["Logging"]),
            Ui = Deserialize<UiSettings>(root["Ui"]),
            HistoryView = Deserialize<HistoryViewSettings>(root["HistoryView"]),
            Cctv = Deserialize<CctvSettings>(root["Cctv"]),
            OeeSignals = Deserialize<OeeSignalSettings>(root["OeeSignals"]),
            Shift = Deserialize<ShiftSettings>(root["Shift"]),
            CycleExclusion = Deserialize<CycleExclusionSettings>(root["CycleExclusion"]),
        };
    }

    public void SaveSettings(AppSettingsModel model)
    {
        var root = LoadRaw(_filePath);
        WriteSections(root, model);
        SaveRaw(_filePath, root);

        // Production.json에 사용자 설정 전체 동기화 (재설치 시 appsettings.json이 덮어씌워져도 유지)
        var prod = File.Exists(_productionFilePath) ? LoadRaw(_productionFilePath) : new JsonObject();
        WriteSections(prod, model);
        SaveRaw(_productionFilePath, prod);
        _logger.LogInformation("appsettings.Production.json 전체 설정 동기화 완료");
    }

    private static void WriteSections(JsonObject target, AppSettingsModel model)
    {
        target["Database"] = JsonSerializer.SerializeToNode(model.Database, JsonOptions);
        target["FlowCycle"] = JsonSerializer.SerializeToNode(model.FlowCycle, JsonOptions);
        target["DspTables"] = JsonSerializer.SerializeToNode(model.DspTables, JsonOptions);
        target["Hub"] = JsonSerializer.SerializeToNode(model.Hub, JsonOptions);
        target["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);
        target["Ui"] = JsonSerializer.SerializeToNode(model.Ui, JsonOptions);
        target["HistoryView"] = JsonSerializer.SerializeToNode(model.HistoryView, JsonOptions);
        target["Cctv"] = JsonSerializer.SerializeToNode(model.Cctv, JsonOptions);
        target["OeeSignals"] = JsonSerializer.SerializeToNode(model.OeeSignals, JsonOptions);
        target["Shift"] = JsonSerializer.SerializeToNode(model.Shift, JsonOptions);
        target["CycleExclusion"] = JsonSerializer.SerializeToNode(model.CycleExclusion, JsonOptions);
    }

    public FlowCycleOverride? GetFlowCycleOverride(string flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            return null;
        }

        var settings = LoadSettings();
        return settings.FlowCycle.Overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveFlowCycleOverride(string flowName, string? startCallName, string? endCallName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var settings = LoadSettings();
        var overrides = settings.FlowCycle.Overrides;
        var existing = overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        var normalizedStart = NormalizeOptional(startCallName);
        var normalizedEnd = NormalizeOptional(endCallName);

        if (string.IsNullOrWhiteSpace(normalizedStart) && string.IsNullOrWhiteSpace(normalizedEnd))
        {
            if (existing is not null)
            {
                overrides.Remove(existing);
            }
        }
        else if (existing is null)
        {
            overrides.Add(new FlowCycleOverride
            {
                FlowName = flowName,
                StartCallName = normalizedStart,
                EndCallName = normalizedEnd
            });
        }
        else
        {
            existing.StartCallName = normalizedStart;
            existing.EndCallName = normalizedEnd;
        }

        settings.FlowCycle.Overrides = overrides
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    public void ClearFlowCycleOverrides()
    {
        var settings = LoadSettings();
        settings.FlowCycle.Overrides.Clear();
        SaveSettings(settings);
    }

    /// <summary>
    /// Flow 의 표준(ideal) 사이클 시간(ms)만 갱신. P5 OEE Performance 의 단일 소스 (doc/21 §2.4).
    /// 기존 StartCallName/EndCallName override 는 보존 — backward compatible.
    /// idealCycleTimeMs == null 이면 IdealCycleTimeMs 만 비우고, 다른 override 도 모두 비어 있으면 항목 제거.
    /// </summary>
    public void SaveFlowIdealCycleTime(string flowName, int? idealCycleTimeMs)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var normalized = idealCycleTimeMs is > 0 ? idealCycleTimeMs : null;

        var settings = LoadSettings();
        var overrides = settings.FlowCycle.Overrides;
        var existing = overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (normalized is null)
            {
                return; // 설정할 것도, 제거할 것도 없음
            }
            overrides.Add(new FlowCycleOverride
            {
                FlowName = flowName,
                IdealCycleTimeMs = normalized,
            });
        }
        else
        {
            existing.IdealCycleTimeMs = normalized;

            // 모든 override 필드가 비면 항목 자체 제거 (SaveFlowCycleOverride 의 빈 항목 정리와 동일 원칙)
            if (normalized is null
                && string.IsNullOrWhiteSpace(existing.StartCallName)
                && string.IsNullOrWhiteSpace(existing.EndCallName))
            {
                overrides.Remove(existing);
            }
        }

        settings.FlowCycle.Overrides = overrides
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    /// <summary>
    /// 대시보드 히스토리 이상치 제외 범위를 Flow별로 upsert/삭제 (단위 = 초). minSec/maxSec 둘 다 null 이면
    /// 해당 Flow 의 제외 규칙을 제거(전체 복원). 음수는 무시(null), min &gt; max 로 뒤집힌 입력은 자동 교정한다.
    /// (SaveFlowCycleOverride 와 같은 per-flow 리스트 정리 원칙 — 빈 항목은 남기지 않음.)
    /// </summary>
    public void SaveCycleExclusion(string flowName, double? minSec, double? maxSec)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var min = minSec is >= 0 ? minSec : null;
        var max = maxSec is >= 0 ? maxSec : null;
        if (min is not null && max is not null && min > max)
        {
            (min, max) = (max, min);   // 뒤집힌 입력 자동 교정
        }

        var settings = LoadSettings();
        var ranges = settings.CycleExclusion.Ranges;
        var existing = ranges
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        if (min is null && max is null)
        {
            if (existing is not null) ranges.Remove(existing);   // 미설정 = 제외 해제
        }
        else if (existing is null)
        {
            ranges.Add(new FlowCycleExclusion { FlowName = flowName, MinSec = min, MaxSec = max });
        }
        else
        {
            existing.MinSec = min;
            existing.MaxSec = max;
        }

        settings.CycleExclusion.Ranges = ranges
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    /// <summary>
    /// DSP 데이터베이스 삭제 (plc.db 및 관련 파일)
    /// </summary>
    public void DeleteDatabase(string dbPath)
    {
        _logger.LogInformation("데이터베이스 삭제 시작: {DbPath}", dbPath);

        try
        {
            // 모든 SQLite 연결 풀 해제 (파일 잠금 방지)
            SqliteConnection.ClearAllPools();
            _logger.LogInformation("SQLite 연결 풀 해제 완료");

            // plc.db 삭제
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
                _logger.LogInformation("데이터베이스 파일 삭제: {DbPath}", dbPath);
            }

            // WAL 파일 삭제
            var walPath = dbPath + "-wal";
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
                _logger.LogInformation("WAL 파일 삭제: {WalPath}", walPath);
            }

            // SHM 파일 삭제
            var shmPath = dbPath + "-shm";
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
                _logger.LogInformation("SHM 파일 삭제: {ShmPath}", shmPath);
            }

            _logger.LogInformation("데이터베이스 삭제 완료");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "데이터베이스 삭제 실패: {DbPath}", dbPath);
            throw;
        }
    }

    private JsonObject LoadRaw(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("{File} 파일 없음, 기본값으로 생성", Path.GetFileName(path));
                var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
                File.WriteAllText(path, defaultJson);
            }

            var json = File.ReadAllText(path);
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{File} JSON 파싱 실패, 백업 후 기본값으로 복구", Path.GetFileName(path));
            var backupPath = path + $".bak.{DateTime.Now:yyyyMMdd_HHmmss}";
            try { File.Copy(path, backupPath, overwrite: true); } catch { /* best effort */ }

            var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
            File.WriteAllText(path, defaultJson);
            return JsonNode.Parse(defaultJson)?.AsObject() ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{File} 읽기 실패", Path.GetFileName(path));
            return new JsonObject();
        }
    }

    private void SaveRaw(string path, JsonObject root)
    {
        try
        {
            File.WriteAllText(path, root.ToJsonString(JsonOptions));
            _logger.LogInformation("{File} 저장 완료", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{File} 저장 실패", Path.GetFileName(path));
        }
    }

    private static T Deserialize<T>(JsonNode? node) where T : new()
    {
        if (node is null) return new T();
        return node.Deserialize<T>(JsonOptions) ?? new T();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
