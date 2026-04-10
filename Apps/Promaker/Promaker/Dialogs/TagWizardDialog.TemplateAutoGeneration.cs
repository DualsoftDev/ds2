using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 템플릿 자동 생성 (주소 설정 + 시스템 타입 템플릿 + ApiDef 생성)
/// </summary>
public partial class TagWizardDialog
{
    /// <summary>
    /// 주소 설정 파일이 없으면 자동 생성
    /// </summary>
    private void EnsureAddressConfigFiles()
    {
        try
        {
            // system_base.txt 확인 및 생성
            var systemAddressPath = TemplateManager.SystemBasePath;
            if (!System.IO.File.Exists(systemAddressPath))
            {
                // Legacy 파일이 있으면 복사
                var legacySystemPath = TemplateManager.SystemBasePath;
                if (System.IO.File.Exists(legacySystemPath))
                {
                    var content = System.IO.File.ReadAllText(legacySystemPath);
                    System.IO.File.WriteAllText(systemAddressPath, content);
                    GenerationStatusText.Text = "✓ system_base.txt 파일이 생성되었습니다";
                }
                else
                {
                    // 기본 템플릿으로 생성
                    TemplateManager.EnsureTemplatesExist();
                    GenerationStatusText.Text = "✓ system_base.txt 파일이 생성되었습니다 (기본값)";
                }
            }

            // flow_base.txt 확인 및 생성
            var flowAddressPath = TemplateManager.FlowBasePath;
            if (!System.IO.File.Exists(flowAddressPath))
            {
                // Legacy 파일이 있으면 복사
                var legacyFlowPath = TemplateManager.FlowBasePath;
                if (System.IO.File.Exists(legacyFlowPath))
                {
                    var content = System.IO.File.ReadAllText(legacyFlowPath);
                    System.IO.File.WriteAllText(flowAddressPath, content);
                    GenerationStatusText.Text = "✓ flow_base.txt 파일이 생성되었습니다";
                }
                else
                {
                    // 기본 템플릿으로 생성
                    TemplateManager.EnsureTemplatesExist();
                    GenerationStatusText.Text = "✓ flow_base.txt 파일이 생성되었습니다 (기본값)";
                }
            }
        }
        catch (Exception ex)
        {
            GenerationStatusText.Text = $"⚠ 주소 설정 파일 생성 중 오류: {ex.Message}";
        }
    }

    /// <summary>
    /// 프로젝트에서 사용된 시스템 타입의 템플릿이 없으면 자동 생성
    /// </summary>
    private void EnsureTemplatesForUsedSystemTypes()
    {
        try
        {
            // 프로젝트에서 사용된 모든 시스템 타입 수집
            var usedSystemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ApiDef의 부모 System에서 SystemType 수집
            foreach (var apiDef in _store.ApiDefs.Values)
            {
                if (_store.Systems.TryGetValue(apiDef.ParentId, out var system))
                {
                    var systemTypeOpt = system.SystemType;
                    if (systemTypeOpt != null && FSharpOption<string>.get_IsSome(systemTypeOpt))
                    {
                        var systemType = systemTypeOpt.Value;
                        if (!string.IsNullOrWhiteSpace(systemType))
                        {
                            usedSystemTypes.Add(systemType);
                        }
                    }
                }
            }

            if (usedSystemTypes.Count == 0)
                return;

            // 기존 템플릿 파일 확인
            var existingTemplates = TemplateManager.GetDeviceTemplateFiles()
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 템플릿이 없는 시스템 타입 찾기
            var missingSystemTypes = usedSystemTypes
                .Where(st => !existingTemplates.Contains(st))
                .ToList();

            // 빈 템플릿 파일 자동 생성
            foreach (var systemType in missingSystemTypes)
            {
                CreateEmptyTemplate(systemType);
            }

            if (missingSystemTypes.Count > 0)
            {
                GenerationStatusText.Text = $"✓ {missingSystemTypes.Count}개 시스템 타입의 템플릿을 자동 생성했습니다: {string.Join(", ", missingSystemTypes)}";
            }
        }
        catch (Exception ex)
        {
            // 템플릿 자동 생성 실패는 경고만 표시하고 계속 진행
            GenerationStatusText.Text = $"⚠ 템플릿 자동 생성 중 오류: {ex.Message}";
        }
    }

    /// <summary>
    /// 템플릿 파일 생성 (실제 SystemType의 ApiDef 기반)
    /// </summary>
    private void CreateEmptyTemplate(string systemType)
    {
        var fileName = $"{systemType}.txt";

        // 해당 SystemType을 가진 System에서 실제 ApiDef 수집
        var apiNames = GetApiNamesForSystemType(systemType);

        // ApiDef가 없으면 기본값 사용
        if (apiNames.Count == 0)
        {
            apiNames = new List<string> { "ADV", "RET" };
        }

        // 템플릿 내용 생성
        var sb = new StringBuilder();
        sb.AppendLine($"# {systemType} 신호 템플릿");
        sb.AppendLine($"# 파일명({fileName})이 SystemType으로 사용됩니다.");
        sb.AppendLine($"# $(F) = Flow명, $(D) = Device명, $(A) = Api명");
        sb.AppendLine();

        // [IW] 섹션
        sb.AppendLine("[IW]");
        foreach (var apiName in apiNames)
        {
            sb.AppendLine($"{apiName}: W_$(F)_I_$(D)_$(A)_LS");
        }
        sb.AppendLine();

        // [QW] 섹션
        sb.AppendLine("[QW]");
        foreach (var apiName in apiNames)
        {
            sb.AppendLine($"{apiName}: W_$(F)_Q_$(D)_$(A)_CMD");
        }
        sb.AppendLine();

        // [MW] 섹션
        sb.AppendLine("[MW]");
        foreach (var apiName in apiNames)
        {
            sb.AppendLine($"{apiName}: W_$(F)_M_$(D)_$(A)_BUSY");
        }

        TemplateManager.WriteTemplateFile(fileName, sb.ToString());

        // ApiDef가 없었으면 기본 ApiDef 생성
        var existingApiCount = GetApiNamesForSystemType(systemType).Count;
        if (existingApiCount == 0)
        {
            CreateDefaultApiDefsForSystemType(systemType, apiNames.ToArray());
        }
    }

    /// <summary>
    /// SystemType에 연결된 실제 ApiDef 이름 목록 가져오기
    /// </summary>
    private List<string> GetApiNamesForSystemType(string systemType)
    {
        var apiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 해당 SystemType을 가진 모든 System 찾기
            foreach (var system in _store.Systems.Values)
            {
                var systemTypeOpt = system.SystemType;
                if (systemTypeOpt != null && FSharpOption<string>.get_IsSome(systemTypeOpt))
                {
                    var sysType = systemTypeOpt.Value;
                    if (string.Equals(sysType, systemType, StringComparison.OrdinalIgnoreCase))
                    {
                        // 해당 System의 모든 ApiDef 수집
                        var systemApiDefs = _store.ApiDefs.Values
                            .Where(api => api.ParentId == system.Id)
                            .Select(api => api.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name));

                        foreach (var apiName in systemApiDefs)
                        {
                            apiNames.Add(apiName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GenerationStatusText.Text += $"\n  ⚠ ApiDef 조회 중 오류: {ex.Message}";
        }

        return apiNames.OrderBy(n => n).ToList();
    }

    /// <summary>
    /// SystemType에 대한 기본 ApiDef 생성 및 주소 설정
    /// </summary>
    private void CreateDefaultApiDefsForSystemType(string systemType, string[] apiNames)
    {
        try
        {
            // system_base.txt에 주소 설정이 없으면 기본값 추가
            EnsureSystemBaseAddress(systemType);

            // 해당 SystemType을 가진 System 찾기
            foreach (var system in _store.Systems.Values)
            {
                var systemTypeOpt = system.SystemType;
                if (systemTypeOpt != null && FSharpOption<string>.get_IsSome(systemTypeOpt))
                {
                    var sysType = systemTypeOpt.Value;
                    if (string.Equals(sysType, systemType, StringComparison.OrdinalIgnoreCase))
                    {
                        // 기존 ApiDef 이름 수집
                        var existingApiNames = _store.ApiDefs.Values
                            .Where(api => api.ParentId == system.Id)
                            .Select(api => api.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        // 없는 ApiDef만 생성
                        foreach (var apiName in apiNames)
                        {
                            if (!existingApiNames.Contains(apiName))
                            {
                                _store.AddApiDefWithProperties(apiName, system.Id);
                                GenerationStatusText.Text += $"\n  → {system.Name}.{apiName} ApiDef 생성됨";
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GenerationStatusText.Text += $"\n  ⚠ ApiDef 자동 생성 중 오류: {ex.Message}";
        }
    }

    /// <summary>
    /// system_base.txt에 SystemType의 주소가 없으면 기본값 추가
    /// </summary>
    private void EnsureSystemBaseAddress(string systemType)
    {
        try
        {
            var systemAddressPath = TemplateManager.SystemBasePath;
            var content = System.IO.File.Exists(systemAddressPath)
                ? System.IO.File.ReadAllText(systemAddressPath)
                : "";

            // 이미 해당 SystemType이 설정되어 있는지 확인
            if (content.Contains($"@SYSTEM {systemType}", StringComparison.OrdinalIgnoreCase))
                return;

            // 기본 주소값 할당 (기존 최대값 + 100)
            int maxAddress = 3000;
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("@IW_BASE", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("@QW_BASE", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("@MW_BASE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var addr))
                    {
                        maxAddress = Math.Max(maxAddress, addr);
                    }
                }
            }

            // 새 SystemType 추가
            var newBaseAddress = maxAddress + 100;
            var newEntry = $@"
@SYSTEM {systemType}
@IW_BASE {newBaseAddress}
@QW_BASE {newBaseAddress}
@MW_BASE {newBaseAddress + 6000}
";

            content += newEntry;
            System.IO.File.WriteAllText(systemAddressPath, content);

            GenerationStatusText.Text += $"\n  → system_base.txt에 {systemType} 주소 추가 (IW/QW: {newBaseAddress}, MW: {newBaseAddress + 6000})";
        }
        catch (Exception ex)
        {
            GenerationStatusText.Text += $"\n  ⚠ 주소 설정 중 오류: {ex.Message}";
        }
    }
}
