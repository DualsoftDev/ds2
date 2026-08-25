using System;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// AppData 의 Promaker 설정 파일 경로 단일 출처.
/// 이전엔 Dualsoft\Promaker 직속이었으나 Settings 하위 폴더로 이동.
/// 첫 호출 시 옛 위치에 파일이 있고 새 위치에는 없으면 자동 이동(1회) — 사용자 설정 보존.
/// </summary>
public static class SettingsPaths
{
    private static readonly string AppDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Dualsoft", "Promaker");

    private static readonly string SettingsRoot = Path.Combine(AppDataRoot, "Settings");

    private static readonly string[] MovedFileNames =
    {
        "PlcConfig.txt",
        "splitDeviceAasx.txt",
        "iriPrefix.txt",
        "createDefaultEntitiesOnEmptyAasx.txt",
    };

    private static bool _migrated;

    public static string Of(string fileName)
    {
        EnsureMigrated();
        return Path.Combine(SettingsRoot, fileName);
    }

    public static string PlcConfig                       => Of("PlcConfig.txt");
    /// <summary>실 PLC 연결 다이얼로그가 마지막으로 입력한 벤더/IP/포트/Timeout/Scan 등을 저장.
    /// Promaker 재실행 시 같은 값으로 다시 채워져 사용자 입력 부담 감소.</summary>
    public static string PlcConnection                   => Of("PlcConnection.json");
    /// <summary>Promaker 가 호스팅하는 OPC UA 서버 설정 (Enabled · Endpoint · MaxSessions · Timeouts 등) 저장 파일.</summary>
    public static string OpcUaServer                     => Of("OpcUaServer.json");
    public static string SplitDeviceAasx                 => Of("splitDeviceAasx.txt");
    public static string IriPrefix                       => Of("iriPrefix.txt");
    public static string CreateDefaultEntitiesOnEmptyAasx => Of("createDefaultEntitiesOnEmptyAasx.txt");

    /// <summary>사용자 정의 AASX 템플릿 폴더 경로. 폴더 안의 *.aasx 의 모든 SM 이 export 시 자동 첨부.</summary>
    public static string AasxUserTemplatesFolder         => Of("aasxUserTemplatesFolder.txt");

    /// <summary>Log tab ComboBox 의 선택 LogLevelChoice (Debug/Info/Warn). AppLogState 가 세션 간 영속화.</summary>
    public static string LogFilterLevel                  => Of("logFilterLevel.txt");

    /// <summary>간트 표시 윈도우(분) — 간트 차트 헤더 드롭다운(5~300분). 순수 뷰 설정이라 PLC 설정
    /// (PlcConnection.json — Agent 업로드에 포함)이 아닌 앱 설정으로 영속화.</summary>
    public static string GanttWindowMinutes              => Of("ganttWindowMinutes.txt");

    /// <summary>`.yaml` 저장 시 lossy 안내 dialog 의 "다시 보지 않기" persistence. true 면 다음 호출부터 dialog skip.</summary>
    public static string YamlSaveNoticeShown             => Of("yamlSaveNoticeShown.txt");

    /// <summary>Monitoring + 실 PLC PLAY 시 "Agent 가 모니터링을 (재)시작했습니다" 안내 다이얼로그의
    /// "다시 보지 않기" persistence. 파일 존재 = 다음부터 다이얼로그 생략 (SimLog 한 줄만).</summary>
    public static string AgentDelegationNoticeSuppress   => Of("agentDelegationNoticeSuppress.txt");

    /// <summary>Agent 보내기/가져오기 대상 — "true" 면 네트워크(특정 IP 공유폴더), 아니면 로컬 공유폴더.</summary>
    public static string AgentTransferUseNetwork         => Of("agentTransferUseNetwork.txt");

    /// <summary>Agent 네트워크 대상 IP 주소 (네트워크 모드일 때만 사용).</summary>
    public static string AgentTransferIp                 => Of("agentTransferIp.txt");

    /// <summary>Agent 보내기/가져오기 대상 모드 — "Local" | "Network" | "Cloud".</summary>
    public static string AgentTransferMode               => Of("agentTransferMode.txt");

    /// <summary>Monitoring + 실 PLC PLAY 시 DSPilot 이 미설치라 브라우저 실행을 건너뛸 때 보여주는 안내
    /// 다이얼로그의 "다시 보지 않기" persistence. 파일 존재 = 다음부터 다이얼로그 생략.</summary>
    public static string DspilotMissingNoticeSuppress    => Of("dspilotMissingNoticeSuppress.txt");

    /// <summary>AASX 사용자 템플릿 폴더 — 디폴트 위치 (AppData\Dualsoft\Promaker\AasxUserTemplates).</summary>
    public static string DefaultAasxUserTemplatesDir => Path.Combine(AppDataRoot, "AasxUserTemplates");

    /// <summary>PLC 템플릿 사용자 복사본 폴더 — AppData\Dualsoft\Promaker\PlcTemplate</summary>
    public static string PlcTemplateDir => Path.Combine(AppDataRoot, "PlcTemplate");

    /// <summary>사용자 추가 LLM system prompt 폴더 — AppData\Dualsoft\Promaker\Prompts. *.md 자동 흡수 (PromptLoader user-tier).</summary>
    public static string UserPromptsDir => Path.Combine(AppDataRoot, "Prompts");

    /// <summary>사용자 정의 LLM 작업 지침 폴더 — AppData\Dualsoft\Promaker\Instructions. 명시 승인된 항목만 instruction-tier 로 주입.</summary>
    public static string CustomInstructionsDir => Path.Combine(AppDataRoot, "Instructions");

    /// <summary>v0.x 사용자 prompts 폴더 — Dualsoft 누락된 옛 경로. 부트 시 존재 감지용 (마이그레이션 안내).</summary>
    public static string LegacyUserPromptsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Promaker", "Prompts");

    /// <summary>PlcConfig 미존재 시 동봉 XGI_Template.xml 가 복사될 기본 위치.</summary>
    public static string DefaultXgiTemplate => Path.Combine(PlcTemplateDir, "XGI_Template.xml");

    /// <summary>옛 경로(AppData\Dualsoft\Promaker\xxx) → 새 경로(Settings\xxx) 1회 이동.</summary>
    private static void EnsureMigrated()
    {
        if (_migrated) return;
        _migrated = true;

        try
        {
            Directory.CreateDirectory(SettingsRoot);
            foreach (var name in MovedFileNames)
            {
                var oldPath = Path.Combine(AppDataRoot, name);
                var newPath = Path.Combine(SettingsRoot, name);
                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
            }
        }
        catch { /* 마이그레이션 실패해도 신규 경로는 그대로 사용 */ }
    }
}
