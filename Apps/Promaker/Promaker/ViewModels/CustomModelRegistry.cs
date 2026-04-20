using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Promaker.ViewModels;

/// <summary>
/// 커스텀 JSON 디바이스 모델 레지스트리.
/// %AppData%\Dualsoft\Promaker\custom-models\ 에 *.device.json 파일을 관리한다.
/// 모든 프로젝트에서 공유되는 공통 저장소.
/// </summary>
public class CustomModelRegistry
{
    private const string CompanyFolder = "Dualsoft";
    private const string AppFolder = "Promaker";
    private const string ModelFolder = "custom-models";
    private const string FilePattern = "*.device.json";

    private readonly string _modelsDir;

    /// <summary>현재 등록된 모델 목록 (이름 → JSON 텍스트)</summary>
    public Dictionary<string, string> Models { get; private set; } = new();

    /// <summary>현재 등록된 모델 이름 목록</summary>
    public IReadOnlyList<string> ModelNames => Models.Keys.OrderBy(k => k).ToList();

    /// <summary>모델 저장 폴더 경로</summary>
    public string ModelsDirectory => _modelsDir;

    public CustomModelRegistry()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _modelsDir = Path.Combine(appData, CompanyFolder, AppFolder, ModelFolder);
    }

    /// <summary>
    /// 모델 폴더에서 모든 *.device.json 파일 로드
    /// </summary>
    public void LoadAll()
    {
        Models.Clear();

        if (!Directory.Exists(_modelsDir))
            return;

        foreach (var file in Directory.GetFiles(_modelsDir, FilePattern))
        {
            try
            {
                var json = File.ReadAllText(file);
                var name = ExtractNameFromFileName(file);
                if (!string.IsNullOrEmpty(name))
                    Models[name] = json;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomModel] Failed to load {file}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[CustomModel] Loaded {Models.Count} custom models from {_modelsDir}");
    }

    /// <summary>
    /// 모델 저장 (파일명 = systemType.device.json)
    /// </summary>
    public void Save(string systemType, string jsonText)
    {
        EnsureDirectory();
        var filePath = GetFilePath(systemType);
        File.WriteAllText(filePath, jsonText);
        Models[systemType] = jsonText;
    }

    /// <summary>
    /// 모델 삭제
    /// </summary>
    public bool Delete(string systemType)
    {
        var filePath = GetFilePath(systemType);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            Models.Remove(systemType);
            return true;
        }
        Models.Remove(systemType);
        return false;
    }

    /// <summary>
    /// JSON 유효성 검증 (파싱 가능 + name/height/parts|chain 존재)
    /// </summary>
    public static (bool isValid, string error) Validate(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("name", out _))
                return (false, "\"name\" 필드가 없습니다.");

            if (!root.TryGetProperty("height", out _))
                return (false, "\"height\" 필드가 없습니다.");

            var hasParts = root.TryGetProperty("parts", out _);
            var hasChain = root.TryGetProperty("chain", out _);
            if (!hasParts && !hasChain)
                return (false, "\"parts\" 또는 \"chain\" 필드가 필요합니다.");

            return (true, string.Empty);
        }
        catch (JsonException ex)
        {
            return (false, $"JSON 구문 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// 모든 모델을 System.Text.Json 직렬화 가능한 딕셔너리로 반환
    /// (ThreeDViewState.SendInitMessage에서 WebView2로 전달용)
    /// </summary>
    public Dictionary<string, object> ToSerializableDictionary()
    {
        var result = new Dictionary<string, object>();
        foreach (var (name, json) in Models)
        {
            try
            {
                var obj = JsonSerializer.Deserialize<JsonElement>(json);
                result[name] = obj;
            }
            catch
            {
                // 파싱 실패한 모델은 건너뜀
            }
        }
        return result;
    }

    /// <summary>
    /// 등록된 모든 커스텀 모델 이름 (F# DevicePresets.registerCustomNames용)
    /// </summary>
    public IEnumerable<string> GetRegisteredNames() => Models.Keys;

    // ── Private Helpers ──────────────────────────────────────────

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_modelsDir))
            Directory.CreateDirectory(_modelsDir);
    }

    private string GetFilePath(string systemType)
        => Path.Combine(_modelsDir, $"{systemType}.device.json");

    private static string ExtractNameFromFileName(string filePath)
        => Path.GetFileNameWithoutExtension(filePath).Replace(".device", "");
}
