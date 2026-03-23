using System.Text.Json;
using System.Text.Json.Nodes;
using DSPilot.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace DSPilot.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _projectDir;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IWebHostEnvironment env,
        ILogger<AppSettingsService> logger)
    {
        _filePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _projectDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "project");
        _logger = logger;
    }

    public AppSettingsModel LoadSettings()
    {
        var root = LoadRaw();
        return new AppSettingsModel
        {
            DsPilot = Deserialize<DsPilotSettings>(root["DsPilot"]),
            Database = Deserialize<DatabaseSettings>(root["Database"]),
            PlcDatabase = Deserialize<PlcDatabaseSettings>(root["PlcDatabase"]),
            PlcConnection = Deserialize<PlcConnectionSettings>(root["PlcConnection"]),
            Logging = Deserialize<LoggingSettings>(root["Logging"]),
            Ui = Deserialize<UiSettings>(root["Ui"]),
        };
    }

    public void SaveSettings(AppSettingsModel model)
    {
        var root = LoadRaw();

        root["DsPilot"] = JsonSerializer.SerializeToNode(model.DsPilot, JsonOptions);
        root["Database"] = JsonSerializer.SerializeToNode(model.Database, JsonOptions);
        root["PlcDatabase"] = JsonSerializer.SerializeToNode(model.PlcDatabase, JsonOptions);
        root["PlcConnection"] = JsonSerializer.SerializeToNode(model.PlcConnection, JsonOptions);
        root["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);
        root["Ui"] = JsonSerializer.SerializeToNode(model.Ui, JsonOptions);

        SaveRaw(root);
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

    private JsonObject LoadRaw()
    {
        var json = File.ReadAllText(_filePath);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private void SaveRaw(JsonObject root)
    {
        File.WriteAllText(_filePath, root.ToJsonString(JsonOptions));
        _logger.LogInformation("appsettings.json 저장 완료");
    }

    private static T Deserialize<T>(JsonNode? node) where T : new()
    {
        if (node is null) return new T();
        return node.Deserialize<T>(JsonOptions) ?? new T();
    }
}
