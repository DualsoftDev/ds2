using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Promaker.Services;

/// <summary>
/// TAG Wizard 템플릿 파일 관리
/// </summary>
public static class TemplateManager
{
    private static readonly string TemplatesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "Templates");

    /// <summary>
    /// 템플릿 폴더 경로
    /// </summary>
    public static string TemplatesFolderPath => TemplatesPath;

    /// <summary>
    /// systemAddress.txt 경로 (시스템 타입별 글로벌 주소)
    /// </summary>
    public static string SystemAddressPath => Path.Combine(TemplatesPath, "systemAddress.txt");

    /// <summary>
    /// flowAddress.txt 경로 (Flow별 로컬 주소)
    /// </summary>
    public static string FlowAddressPath => Path.Combine(TemplatesPath, "flowAddress.txt");

    /// <summary>
    /// Legacy system_base.txt 경로 (하위 호환성)
    /// </summary>
    public static string SystemBasePath => Path.Combine(TemplatesPath, "system_base.txt");

    /// <summary>
    /// Legacy flow_base.txt 경로 (하위 호환성)
    /// </summary>
    public static string FlowBasePath => Path.Combine(TemplatesPath, "flow_base.txt");

    /// <summary>
    /// Legacy address_config.txt 경로 (하위 호환성)
    /// </summary>
    public static string AddressConfigPath => Path.Combine(TemplatesPath, "address_config.txt");

    /// <summary>
    /// 기본 템플릿 파일 목록
    /// </summary>
    private static readonly Dictionary<string, string> DefaultTemplates = new()
    {
        ["systemAddress.txt"] = @"# System Address Configuration
# 시스템 타입별 글로벌 주소 설정
# 형식: @SYSTEM [타입명] 다음에 @IW_BASE, @QW_BASE, @MW_BASE 지정

@SYSTEM RBT
@IW_BASE 3070
@QW_BASE 3070
@MW_BASE 9110

@SYSTEM PIN
@IW_BASE 3200
@QW_BASE 3200
@MW_BASE 9300

@SYSTEM CLAMP
@IW_BASE 3300
@QW_BASE 3300
@MW_BASE 9500

@SYSTEM LATCH
@IW_BASE 3250
@QW_BASE 3250
@MW_BASE 9400

@SYSTEM Unit
@IW_BASE 3400
@QW_BASE 3400
@MW_BASE 9600

@SYSTEM UpDn
@IW_BASE 3500
@QW_BASE 3500
@MW_BASE 9700

@SYSTEM Motor
@IW_BASE 3600
@QW_BASE 3600
@MW_BASE 9800

@SYSTEM Multi
@IW_BASE 3700
@QW_BASE 3700
@MW_BASE 9900
",
        ["flowAddress.txt"] = @"# Flow Address Configuration
# Flow별 로컬 주소 설정
# 형식: @FLOW [Flow명] 다음에 @IW_BASE, @QW_BASE, @MW_BASE 지정

# 예시:
# @FLOW Flow1
# @IW_BASE 4000
# @QW_BASE 4000
# @MW_BASE 10000
#
# @FLOW Flow2
# @IW_BASE 4100
# @QW_BASE 4100
# @MW_BASE 10100
",
        ["system_base.txt"] = @"# System Base Address Configuration (Legacy)
# 시스템 타입별 글로벌 주소 설정
# 형식: @SYSTEM [타입명] 다음에 @IW_BASE, @QW_BASE, @MW_BASE 지정

@SYSTEM RBT
@IW_BASE 3070
@QW_BASE 3070
@MW_BASE 9110

@SYSTEM PIN
@IW_BASE 3200
@QW_BASE 3200
@MW_BASE 9300

@SYSTEM CLAMP
@IW_BASE 3300
@QW_BASE 3300
@MW_BASE 9500

@SYSTEM LATCH
@IW_BASE 3250
@QW_BASE 3250
@MW_BASE 9400

@SYSTEM Unit
@IW_BASE 3400
@QW_BASE 3400
@MW_BASE 9600

@SYSTEM UpDn
@IW_BASE 3500
@QW_BASE 3500
@MW_BASE 9700

@SYSTEM Motor
@IW_BASE 3600
@QW_BASE 3600
@MW_BASE 9800

@SYSTEM Multi
@IW_BASE 3700
@QW_BASE 3700
@MW_BASE 9900
",
        ["flow_base.txt"] = @"# Flow Base Address Configuration
# Flow별 로컬 주소 설정
# 형식: @FLOW [Flow명] 다음에 @IW_BASE, @QW_BASE, @MW_BASE 지정

# 예시:
# @FLOW Flow1
# @IW_BASE 4000
# @QW_BASE 4000
# @MW_BASE 10000
#
# @FLOW Flow2
# @IW_BASE 4100
# @QW_BASE 4100
# @MW_BASE 10100
",
        ["RBT.txt"] = @"# RBT (Robot) 신호 템플릿
# 파일명(RBT.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_I_$(D)_$(A)_LS
RET: W_$(F)_I_$(D)_$(A)_LS

[QW]
ADV: W_$(F)_Q_$(D)_$(A)_CMD
RET: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
ADV: W_$(F)_M_$(D)_$(A)_BUSY
RET: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["PIN.txt"] = @"# PIN 신호 템플릿
# 파일명(PIN.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
UP: W_$(F)_I_$(D)_$(A)_LS
DOWN: W_$(F)_I_$(D)_$(A)_LS

[QW]
UP: W_$(F)_Q_$(D)_$(A)_CMD
DOWN: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
UP: W_$(F)_M_$(D)_$(A)_BUSY
DOWN: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["CLAMP.txt"] = @"# CLAMP 신호 템플릿
# 파일명(CLAMP.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
CLOSE: W_$(F)_I_$(D)_$(A)_LS
OPEN: W_$(F)_I_$(D)_$(A)_LS

[QW]
CLOSE: W_$(F)_Q_$(D)_$(A)_CMD
OPEN: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
CLOSE: W_$(F)_M_$(D)_$(A)_BUSY
OPEN: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["LATCH.txt"] = @"# LATCH 신호 템플릿
# 파일명(LATCH.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
LOCK: W_$(F)_I_$(D)_$(A)_LS
UNLOCK: W_$(F)_I_$(D)_$(A)_LS

[QW]
LOCK: W_$(F)_Q_$(D)_$(A)_CMD
UNLOCK: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
LOCK: W_$(F)_M_$(D)_$(A)_BUSY
UNLOCK: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["Unit.txt"] = @"# Unit 신호 템플릿
# 파일명(Unit.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_I_$(D)_$(A)_LS
RET: W_$(F)_I_$(D)_$(A)_LS

[QW]
ADV: W_$(F)_Q_$(D)_$(A)_CMD
RET: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
ADV: W_$(F)_M_$(D)_$(A)_BUSY
RET: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["UpDn.txt"] = @"# UpDn 신호 템플릿
# 파일명(UpDn.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
UP: W_$(F)_I_$(D)_$(A)_LS
DOWN: W_$(F)_I_$(D)_$(A)_LS

[QW]
UP: W_$(F)_Q_$(D)_$(A)_CMD
DOWN: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
UP: W_$(F)_M_$(D)_$(A)_BUSY
DOWN: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["Motor.txt"] = @"# Motor 신호 템플릿
# 파일명(Motor.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
FWD: W_$(F)_I_$(D)_$(A)_LS
BWD: W_$(F)_I_$(D)_$(A)_LS

[QW]
FWD: W_$(F)_Q_$(D)_$(A)_CMD
BWD: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
FWD: W_$(F)_M_$(D)_$(A)_BUSY
BWD: W_$(F)_M_$(D)_$(A)_BUSY
",
        ["Multi.txt"] = @"# Multi 신호 템플릿
# 파일명(Multi.txt)이 SystemType으로 사용됩니다.
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_I_$(D)_$(A)_LS
RET: W_$(F)_I_$(D)_$(A)_LS
UP: W_$(F)_I_$(D)_$(A)_LS
DOWN: W_$(F)_I_$(D)_$(A)_LS
FWD: W_$(F)_I_$(D)_$(A)_LS
BWD: W_$(F)_I_$(D)_$(A)_LS

[QW]
ADV: W_$(F)_Q_$(D)_$(A)_CMD
RET: W_$(F)_Q_$(D)_$(A)_CMD
UP: W_$(F)_Q_$(D)_$(A)_CMD
DOWN: W_$(F)_Q_$(D)_$(A)_CMD
FWD: W_$(F)_Q_$(D)_$(A)_CMD
BWD: W_$(F)_Q_$(D)_$(A)_CMD

[MW]
ADV: W_$(F)_M_$(D)_$(A)_BUSY
RET: W_$(F)_M_$(D)_$(A)_BUSY
UP: W_$(F)_M_$(D)_$(A)_BUSY
DOWN: W_$(F)_M_$(D)_$(A)_BUSY
FWD: W_$(F)_M_$(D)_$(A)_BUSY
BWD: W_$(F)_M_$(D)_$(A)_BUSY
"
    };

    /// <summary>
    /// 템플릿 폴더 초기화 (없으면 생성, 기본 템플릿 복사)
    /// </summary>
    public static void EnsureTemplatesExist()
    {
        try
        {
            // 템플릿 폴더 생성
            if (!Directory.Exists(TemplatesPath))
            {
                Directory.CreateDirectory(TemplatesPath);
            }

            // 기본 템플릿 파일들 생성 (없으면)
            foreach (var template in DefaultTemplates)
            {
                var filePath = Path.Combine(TemplatesPath, template.Key);
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, template.Value);
                }
            }
        }
        catch
        {
            // Ignore failures in template initialization
        }
    }

    /// <summary>
    /// 설정 파일 목록 조회 (system_base.txt, flow_base.txt)
    /// </summary>
    public static List<string> GetConfigFiles()
    {
        EnsureTemplatesExist();

        try
        {
            var files = new List<string>();
            var systemBase = Path.Combine(TemplatesPath, "system_base.txt");
            var flowBase = Path.Combine(TemplatesPath, "flow_base.txt");

            if (File.Exists(systemBase))
                files.Add("system_base.txt");
            if (File.Exists(flowBase))
                files.Add("flow_base.txt");

            return files;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 장치 템플릿 파일 목록 조회 (RBT.txt, PIN.txt 등)
    /// </summary>
    public static List<string> GetDeviceTemplateFiles()
    {
        EnsureTemplatesExist();

        try
        {
            return Directory.GetFiles(TemplatesPath, "*.txt")
                .Select(Path.GetFileName)
                .Where(name => name != null)
                .Select(name => name!)
                .Where(name => name != "systemAddress.txt" &&
                              name != "flowAddress.txt" &&
                              name != "system_base.txt" &&
                              name != "flow_base.txt" &&
                              name != "address_config.txt")
                .OrderBy(name => name)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 템플릿 파일 목록 조회 (모든 .txt 파일)
    /// </summary>
    public static List<string> GetTemplateFiles()
    {
        EnsureTemplatesExist();

        try
        {
            return Directory.GetFiles(TemplatesPath, "*.txt")
                .Select(Path.GetFileName)
                .Where(name => name != null)
                .Select(name => name!)
                .OrderBy(name => name == "system_base.txt" ? 0 :
                               name == "flow_base.txt" ? 1 :
                               name == "address_config.txt" ? 2 : 3)
                .ThenBy(name => name)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 템플릿 파일 내용 읽기
    /// </summary>
    public static string ReadTemplateFile(string fileName)
    {
        var filePath = Path.Combine(TemplatesPath, fileName);
        return File.Exists(filePath) ? File.ReadAllText(filePath) : "";
    }

    /// <summary>
    /// 템플릿 파일 내용 저장
    /// </summary>
    public static void WriteTemplateFile(string fileName, string content)
    {
        EnsureTemplatesExist();
        var filePath = Path.Combine(TemplatesPath, fileName);
        File.WriteAllText(filePath, content);
    }

    /// <summary>
    /// 템플릿을 기본값으로 초기화
    /// </summary>
    public static void ResetToDefaults()
    {
        EnsureTemplatesExist();

        foreach (var template in DefaultTemplates)
        {
            var filePath = Path.Combine(TemplatesPath, template.Key);
            File.WriteAllText(filePath, template.Value);
        }
    }

    /// <summary>
    /// 템플릿 폴더를 탐색기에서 열기
    /// </summary>
    public static void OpenTemplatesFolder()
    {
        EnsureTemplatesExist();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = TemplatesPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore failures
        }
    }
}
