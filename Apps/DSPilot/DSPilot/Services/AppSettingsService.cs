using System.Diagnostics;
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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IWebHostEnvironment env,
        IHostApplicationLifetime lifetime,
        ILogger<AppSettingsService> logger)
    {
        _filePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _projectDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "project");
        _lifetime = lifetime;
        _logger = logger;
    }

    public AppSettingsModel LoadSettings()
    {
        var root = LoadRaw();
        return new AppSettingsModel
        {
            DsPilot = Deserialize<DsPilotSettings>(root["DsPilot"]),
            PlcDatabase = Deserialize<PlcDatabaseSettings>(root["PlcDatabase"]),
            DspDatabase = Deserialize<DspDatabaseSettings>(root["DspDatabase"]),
            PlcConnection = Deserialize<PlcConnectionSettings>(root["PlcConnection"]),
            Logging = Deserialize<LoggingSettings>(root["Logging"]),
        };
    }

    public void SaveSettings(AppSettingsModel model)
    {
        var root = LoadRaw();

        root["DsPilot"] = JsonSerializer.SerializeToNode(model.DsPilot, JsonOptions);
        root["PlcDatabase"] = JsonSerializer.SerializeToNode(model.PlcDatabase, JsonOptions);
        root["DspDatabase"] = JsonSerializer.SerializeToNode(model.DspDatabase, JsonOptions);
        root["PlcConnection"] = JsonSerializer.SerializeToNode(model.PlcConnection, JsonOptions);
        root["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);

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

    /// <returns>true: 자동 재시작됨, false: 수동 재시작 필요 (VS 디버그 모드)</returns>
    public bool RestartApplication()
    {
        _logger.LogInformation("애플리케이션 재시작 요청됨");

        if (Debugger.IsAttached)
        {
            _logger.LogInformation("디버거 연결됨 - 앱 종료만 수행 (수동 재시작 필요)");
            _lifetime.StopApplication();
            return false;
        }

        if (!Environment.UserInteractive)
        {
            _logger.LogInformation("서비스 모드 - SCM에 의해 재시작됩니다");
            _lifetime.StopApplication();
            return true;
        }

        // 콘솔 모드: 새 프로세스를 딜레이와 함께 시작 후 현재 앱 종료
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            var existingArgs = Environment.GetCommandLineArgs().Skip(1)
                .Where(a => !a.StartsWith("--restart-delay"));
            var allArgs = existingArgs.Append("--restart-delay").Append("2000");

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", allArgs),
                UseShellExecute = false
            });
            _logger.LogInformation("새 프로세스 시작 (2초 딜레이): {Path}", exePath);
        }

        _lifetime.StopApplication();
        return true;
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
