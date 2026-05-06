using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using log4net;

namespace Promaker.LlmAgent;

/// <summary>
/// LLM 데이터 송출 사용자 동의 (data egress consent) 관리.
///
/// 결정/주의 16 — read tool 결과는 사용자 모델 전체가 외부 LLM 으로 송출되므로 첫 진입 opt-in 다이얼로그 +
/// `%APPDATA%/Promaker/llm-config.json` consent flag + UTC timestamp 저장. consent 없으면 LLM Chat 진입 차단.
///
/// 1d-4 E. 진입점 (`MainViewModel.ToggleLlmChat`) 1차 차단 + `LlmChatViewModel.InitializeAsync` 2차 defense-in-depth.
/// </summary>
public static class LlmConsent
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmConsent));

    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Promaker");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "llm-config.json");

    public sealed class Config
    {
        public bool DataEgressConsent { get; set; }
        public string? ConsentTimestampUtc { get; set; }
    }

    private static readonly object _saveLock = new();

    /// <summary>
    /// M2 — corrupt JSON 시 LLM Chat 영구 차단 회피: `.bak` 백업 후 default 반환.
    /// 다음 Grant 시 새 정상 파일이 작성되어 사용자가 동의 다이얼로그를 다시 거치게 됨.
    /// </summary>
    public static Config Load()
    {
        if (!File.Exists(ConfigPath)) return new Config();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new Config();
        }
        catch (JsonException ex)
        {
            var bak = ConfigPath + ".bak";
            try { File.Move(ConfigPath, bak, overwrite: true); } catch { /* best-effort */ }
            Log.Warn($"LlmConsent JSON corrupt — {bak} 로 백업 후 default 사용 ({ex.Message})");
            return new Config();
        }
    }

    /// <summary>
    /// M2 — Promaker 다중 인스턴스 동시 grant race 방지: lock + atomic write (temp + File.Move).
    /// </summary>
    private static void Save(Config config)
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ConfigPath + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    public static bool IsGranted() => Load().DataEgressConsent;

    public static void Grant()
    {
        var config = Load();
        config.DataEgressConsent = true;
        config.ConsentTimestampUtc = DateTime.UtcNow.ToString("o");
        Save(config);
        Log.Info($"LlmConsent granted — {ConfigPath}");
    }

    /// <summary>
    /// consent 가 없으면 사용자에게 opt-in 다이얼로그를 표시. true=granted (이미 또는 신규 동의).
    /// 거부 시 false. 메인 UI 스레드에서 호출.
    /// </summary>
    public static bool EnsureGranted()
    {
        if (IsGranted()) return true;

        const string msg =
            "Promaker LLM Chat 사용 시 다음 정보가 외부 LLM 서비스 (Claude) 로 전송됩니다:\n" +
            "  • 대화에 입력하는 사용자 메시지\n" +
            "  • LLM 이 read tool 로 조회한 모델 정보 (system / flow / work 이름, 구조)\n" +
            "  • Promaker 가 정의한 system prompt\n\n" +
            "전송 채널: Claude CLI (사용자 PC 에 설치된 Anthropic 공식 CLI) → Anthropic API\n" +
            "API 키 / 비밀번호 / 파일 시스템 경로 등은 전송되지 않습니다.\n\n" +
            "동의하시겠습니까?\n\n" +
            "(거부 시 LLM Chat 기능이 차단됩니다. 추후 동의는 LLM Chat 메뉴 재진입 시 다시 묻습니다.)";

        var owner = Application.Current?.MainWindow;
        var result = owner != null
            ? MessageBox.Show(owner, msg, "LLM 데이터 전송 동의", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(msg, "LLM 데이터 전송 동의", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Grant();
            return true;
        }
        Log.Info("LlmConsent declined");
        return false;
    }
}
