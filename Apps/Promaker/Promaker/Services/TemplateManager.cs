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
    /// address_config.txt 경로
    /// </summary>
    public static string AddressConfigPath => Path.Combine(TemplatesPath, "address_config.txt");

    /// <summary>
    /// 기본 템플릿 파일 목록
    /// </summary>
    private static readonly Dictionary<string, string> DefaultTemplates = new()
    {
        ["address_config.txt"] = @"# Address Configuration
# All SystemTypes use GLOBAL mode (continuous addressing)

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
        ["RBT.txt"] = @"@META RBT
@CATEGORY RBT

[RBT.IW]
ADV: W_$(F)_I_$(D)_ADV_LS
RET: W_$(F)_I_$(D)_RET_LS

[RBT.QW]
ADV: W_$(F)_Q_$(D)_ADV_CMD
RET: W_$(F)_Q_$(D)_RET_CMD

[RBT.MW]
ADV: W_$(F)_M_$(D)_ADV_BUSY
RET: W_$(F)_M_$(D)_RET_BUSY
",
        ["PIN.txt"] = @"@META PIN
@CATEGORY PIN

[PIN.IW]
UP: W_$(F)_I_$(D)_UP_LS
DOWN: W_$(F)_I_$(D)_DOWN_LS

[PIN.QW]
UP: W_$(F)_Q_$(D)_UP_CMD
DOWN: W_$(F)_Q_$(D)_DOWN_CMD

[PIN.MW]
UP: W_$(F)_M_$(D)_UP_BUSY
DOWN: W_$(F)_M_$(D)_DOWN_BUSY
",
        ["CLAMP.txt"] = @"@META CLAMP
@CATEGORY CLAMP

[CLAMP.IW]
CLOSE: W_$(F)_I_$(D)_CLOSE_LS
OPEN: W_$(F)_I_$(D)_OPEN_LS

[CLAMP.QW]
CLOSE: W_$(F)_Q_$(D)_CLOSE_CMD
OPEN: W_$(F)_Q_$(D)_OPEN_CMD

[CLAMP.MW]
CLOSE: W_$(F)_M_$(D)_CLOSE_BUSY
OPEN: W_$(F)_M_$(D)_OPEN_BUSY
",
        ["LATCH.txt"] = @"@META LATCH
@CATEGORY LATCH

[LATCH.IW]
LOCK: W_$(F)_I_$(D)_LOCK_LS
UNLOCK: W_$(F)_I_$(D)_UNLOCK_LS

[LATCH.QW]
LOCK: W_$(F)_Q_$(D)_LOCK_CMD
UNLOCK: W_$(F)_Q_$(D)_UNLOCK_CMD

[LATCH.MW]
LOCK: W_$(F)_M_$(D)_LOCK_BUSY
UNLOCK: W_$(F)_M_$(D)_UNLOCK_BUSY
",
        ["Unit.txt"] = @"@META Unit
@CATEGORY Unit

[Unit.IW]
ADV: W_$(F)_I_$(D)_UP_LS
RET: W_$(F)_I_$(D)_DOWN_LS

[Unit.QW]
ADV: W_$(F)_Q_$(D)_UP_CMD
RET: W_$(F)_Q_$(D)_DOWN_CMD

[Unit.MW]
ADV: W_$(F)_M_$(D)_UP_BUSY
RET: W_$(F)_M_$(D)_DOWN_BUSY
",
        ["UpDn.txt"] = @"@META UpDn
@CATEGORY UpDn

[UpDn.IW]
UP: W_$(F)_I_$(D)_UP_LS
DOWN: W_$(F)_I_$(D)_DOWN_LS

[UpDn.QW]
UP: W_$(F)_Q_$(D)_UP_CMD
DOWN: W_$(F)_Q_$(D)_DOWN_CMD

[UpDn.MW]
UP: W_$(F)_M_$(D)_UP_BUSY
DOWN: W_$(F)_M_$(D)_DOWN_BUSY
",
        ["Motor.txt"] = @"@META Motor
@CATEGORY Motor

[Motor.IW]
FWD: W_$(F)_I_$(D)_FWD_LS
BWD: W_$(F)_I_$(D)_BWD_LS

[Motor.QW]
FWD: W_$(F)_Q_$(D)_FWD_CMD
BWD: W_$(F)_Q_$(D)_BWD_CMD

[Motor.MW]
FWD: W_$(F)_M_$(D)_FWD_BUSY
BWD: W_$(F)_M_$(D)_BWD_BUSY
",
        ["Multi.txt"] = @"@META Multi
@CATEGORY Multi

[Multi.IW]
ADV: W_$(F)_I_$(D)_ADV_LS
RET: W_$(F)_I_$(D)_RET_LS
UP: W_$(F)_I_$(D)_UP_LS
DOWN: W_$(F)_I_$(D)_DOWN_LS
FWD: W_$(F)_I_$(D)_FWD_LS
BWD: W_$(F)_I_$(D)_BWD_LS

[Multi.QW]
ADV: W_$(F)_Q_$(D)_ADV_CMD
RET: W_$(F)_Q_$(D)_RET_CMD
UP: W_$(F)_Q_$(D)_UP_CMD
DOWN: W_$(F)_Q_$(D)_DOWN_CMD
FWD: W_$(F)_Q_$(D)_FWD_CMD
BWD: W_$(F)_Q_$(D)_BWD_CMD

[Multi.MW]
ADV: W_$(F)_M_$(D)_ADV_BUSY
RET: W_$(F)_M_$(D)_RET_BUSY
UP: W_$(F)_M_$(D)_UP_BUSY
DOWN: W_$(F)_M_$(D)_DOWN_BUSY
FWD: W_$(F)_M_$(D)_FWD_BUSY
BWD: W_$(F)_M_$(D)_BWD_BUSY
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
    /// 템플릿 파일 목록 조회
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
                .OrderBy(name => name == "address_config.txt" ? 0 : 1)
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
