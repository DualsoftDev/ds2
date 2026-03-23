using System.Text.Json;
using System.Text.Json.Nodes;
using DSPilot.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace DSPilot.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _productionFilePath;
    private readonly string _projectDir;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IWebHostEnvironment env,
        ILogger<AppSettingsService> logger)
    {
        _filePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _productionFilePath = Path.Combine(env.ContentRootPath, "appsettings.Production.json");
        _projectDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "project");
        _logger = logger;
    }

    public AppSettingsModel LoadSettings()
    {
        var root = LoadRaw(_filePath);

        // Production.json이 있으면 각 섹션을 오버라이드 (ASP.NET Core 설정 병합과 동일 방식)
        if (File.Exists(_productionFilePath))
        {
            var prod = LoadRaw(_productionFilePath);
            foreach (var key in new[] { "DsPilot", "Database", "PlcDatabase", "PlcCapture", "Logging", "Ui" })
            {
                if (prod[key] is not null)
                    root[key] = prod[key]!.DeepClone();
            }
        }

        return new AppSettingsModel
        {
            DsPilot = Deserialize<DsPilotSettings>(root["DsPilot"]),
            Database = Deserialize<DatabaseSettings>(root["Database"]),
            PlcDatabase = Deserialize<PlcDatabaseSettings>(root["PlcDatabase"]),
            PlcCapture = Deserialize<PlcCaptureSettings>(root["PlcCapture"]),
            Logging = Deserialize<LoggingSettings>(root["Logging"]),
            Ui = Deserialize<UiSettings>(root["Ui"]),
        };
    }

    public void SaveSettings(AppSettingsModel model)
    {
        var root = LoadRaw(_filePath);

        root["DsPilot"] = JsonSerializer.SerializeToNode(model.DsPilot, JsonOptions);
        root["Database"] = JsonSerializer.SerializeToNode(model.Database, JsonOptions);
        root["PlcDatabase"] = JsonSerializer.SerializeToNode(model.PlcDatabase, JsonOptions);
        root["PlcCapture"] = JsonSerializer.SerializeToNode(model.PlcCapture, JsonOptions);
        root["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);
        root["Ui"] = JsonSerializer.SerializeToNode(model.Ui, JsonOptions);

        SaveRaw(_filePath, root);

        // Production.json에 사용자 설정 전체 동기화 (재설치 시 appsettings.json이 덮어씌워져도 유지)
        var prod = File.Exists(_productionFilePath) ? LoadRaw(_productionFilePath) : new JsonObject();
        prod["DsPilot"] = JsonSerializer.SerializeToNode(model.DsPilot, JsonOptions);
        prod["Database"] = JsonSerializer.SerializeToNode(model.Database, JsonOptions);
        prod["PlcDatabase"] = JsonSerializer.SerializeToNode(model.PlcDatabase, JsonOptions);
        prod["PlcCapture"] = JsonSerializer.SerializeToNode(model.PlcCapture, JsonOptions);
        prod["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);
        prod["Ui"] = JsonSerializer.SerializeToNode(model.Ui, JsonOptions);
        SaveRaw(_productionFilePath, prod);
        _logger.LogInformation("appsettings.Production.json 전체 설정 동기화 완료");
    }

    public async Task<string> UploadAasxFileAsync(IBrowserFile file)
    {
        Directory.CreateDirectory(_projectDir);
        var destPath = Path.Combine(_projectDir, file.Name);

        await using var stream = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
        await using var fs = new FileStream(destPath, FileMode.Create);
        await stream.CopyToAsync(fs);

        _logger.LogInformation("AASX 파일 업로드: {Path}", destPath);
        return destPath;
    }

    /// <summary>
    /// DSP 데이터베이스 삭제 (plc.db 및 관련 파일)
    /// </summary>
    public void DeleteDatabase(string dbPath)
    {
        _logger.LogInformation("데이터베이스 삭제 시작: {DbPath}", dbPath);

        try
        {
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

    private static JsonObject LoadRaw(string path)
    {
        var json = File.ReadAllText(path);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private void SaveRaw(string path, JsonObject root)
    {
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
        _logger.LogInformation("{File} 저장 완료", Path.GetFileName(path));
    }

    private static T Deserialize<T>(JsonNode? node) where T : new()
    {
        if (node is null) return new T();
        return node.Deserialize<T>(JsonOptions) ?? new T();
    }
}
