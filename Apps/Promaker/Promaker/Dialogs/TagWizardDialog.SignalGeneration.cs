using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Ds2.IOList;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// 신호 생성
    /// </summary>
    private bool GenerateSignals()
    {
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            // 주소 설정 파일이 없으면 자동 생성
            EnsureAddressConfigFiles();

            // 프로젝트에서 사용된 시스템 타입 수집 및 템플릿 자동 생성
            EnsureTemplatesForUsedSystemTypes();

            var templateDir = TemplateManager.TemplatesFolderPath;
            var result = _generator.Generate(_store, templateDir);

            if (!_generator.IsSuccess(result))
            {
                // 오류를 다이얼로그 내 탭에 표시
                DisplayErrors(result);
                return true; // Step 3로 이동하여 오류 탭 표시
            }

            // IO 및 Dummy 신호 변환
            _ioRows.Clear();
            _dummyRows.Clear();

            foreach (var row in ConvertSignalsToRows(result))
                _ioRows.Add(row);

            foreach (var row in ConvertDummySignalsToRows(result))
                _dummyRows.Add(row);

            // 매칭 검증 및 분류
            ValidateAndClassifySignals();

            // 상태 메시지
            var unmatchedCount = _unmatchedRows.Count;
            GenerationStatusText.Text = unmatchedCount > 0
                ? $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개 생성 | ⚠ 매칭 실패 {unmatchedCount}개"
                : $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개가 생성되었습니다. 모든 신호가 매칭되었습니다.";

            return true;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"신호 생성 중 예외가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "적용 ▶";
        }
    }

    /// <summary>
    /// 매칭 검증 및 분류
    /// </summary>
    private void ValidateAndClassifySignals()
    {
        _unmatchedRows.Clear();

        foreach (var row in _ioRows)
        {
            if (row.CallId == Guid.Empty || row.ApiCallId == Guid.Empty)
            {
                string reason = (row.CallId == Guid.Empty, row.ApiCallId == Guid.Empty) switch
                {
                    (true, true) => "Call 및 ApiCall을 찾을 수 없음",
                    (true, false) => "Call을 찾을 수 없음",
                    _ => "ApiCall(Device)을 찾을 수 없음",
                };

                _unmatchedRows.Add(new UnmatchedSignalRow(
                    Flow: row.Flow,
                    Device: row.Device,
                    Api: row.Api,
                    OutSymbol: row.OutSymbol,
                    OutAddress: row.OutAddress,
                    InSymbol: row.InSymbol,
                    InAddress: row.InAddress,
                    FailureReason: reason
                ));
            }
        }

        // 탭 표시 업데이트
        if (_unmatchedRows.Count > 0)
        {
            UnmatchedTabItem.Visibility = Visibility.Visible;
            UnmatchedCountText.Text = _unmatchedRows.Count.ToString();
        }
        else
        {
            UnmatchedTabItem.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 신호 적용
    /// </summary>
    private bool ApplySignals()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 IO 신호가 없습니다.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        var validRows = _ioRows.Where(r => r.ApiCallId != Guid.Empty && r.CallId != Guid.Empty).ToList();
        var unmatchedCount = _unmatchedRows.Count;

        if (unmatchedCount > 0)
        {
            var result = DialogHelpers.ShowThemedMessageBox(
                $"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않았습니다.\n\n" +
                $"'매칭 실패' 탭에서 상세 내역을 확인할 수 있습니다.\n\n" +
                $"✓ 매칭된 {validRows.Count}개 항목만 적용됩니다.\n\n" +
                $"계속하시겠습니까?",
                "TAG Wizard - 확인",
                MessageBoxButton.YesNo,
                "?");

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";

            _successCount = 0;
            var failedItems = new List<string>();

            foreach (var row in validRows)
            {
                try
                {
                    _store.UpdateApiCallIoTags(
                        row.CallId,
                        row.ApiCallId,
                        row.OutSymbol,
                        row.OutAddress,
                        row.InSymbol,
                        row.InAddress);

                    _successCount++;
                }
                catch (Exception ex)
                {
                    failedItems.Add($"{row.Flow}/{row.Device}/{row.Api}: {ex.Message}");
                }
            }

            // 완료 메시지 구성
            var summary = new StringBuilder();
            summary.AppendLine($"✅ {_successCount}개 ApiCall에 IO 태그가 성공적으로 적용되었습니다.");
            summary.AppendLine($"📊 IO 신호: {_ioRows.Count}개");
            summary.AppendLine($"📊 Dummy 신호: {_dummyRows.Count}개");

            if (failedItems.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"⚠️ {failedItems.Count}개 항목 적용 실패:");
                foreach (var item in failedItems.Take(3))
                {
                    summary.AppendLine($"  • {item}");
                }
                if (failedItems.Count > 3)
                {
                    summary.AppendLine($"  ... 외 {failedItems.Count - 3}개");
                }
            }

            CompletionSummaryText.Text = summary.ToString();

            return _successCount > 0;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// SignalRecord → IoBatchRow 변환
    /// </summary>
    private List<IoBatchRow> ConvertSignalsToRows(GenerationResult result)
    {
        var rows = new List<IoBatchRow>();

        // Group signals by Flow/Work/Call/Device
        var grouped = result.IoSignals
            .GroupBy(s => new { s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;

            // Find input and output signals (IoType starts with I/Q)
            var inputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase));
            var outputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase));

            // ApiCall 매칭
            var matchedCall = FindCallByName(_store, key.FlowName, key.WorkName, key.CallName);
            var callId = matchedCall?.Id ?? Guid.Empty;

            var apiCallId = Guid.Empty;
            string device = "UNKNOWN";
            string api = key.DeviceName;  // Initial value (DeviceName represents ApiDef.Name)

            if (matchedCall != null)
            {
                // DeviceName은 ApiDef.Name이므로 ApiDefId를 통해 매칭
                var matchedApiCall = matchedCall.ApiCalls
                    .FirstOrDefault(ac =>
                    {
                        // ApiDefId 확인
                        if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId))
                            return false;

                        var apiDefId = ac.ApiDefId.Value;

                        // ApiDef 조회
                        var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                        if (!FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                            return false;

                        var apiDef = apiDefOption.Value;

                        // ApiDef.Name과 DeviceName 비교
                        return apiDef.Name.Equals(key.DeviceName, StringComparison.OrdinalIgnoreCase);
                    });
                apiCallId = matchedApiCall?.Id ?? Guid.Empty;

                // Extract Device (System.Name) and Api (ApiDef.Name) from matched ApiCall
                if (matchedApiCall != null && FSharpOption<Guid>.get_IsSome(matchedApiCall.ApiDefId))
                {
                    var apiDefId = matchedApiCall.ApiDefId.Value;
                    var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                    if (FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                    {
                        var apiDef = apiDefOption.Value;
                        api = apiDef.Name;

                        // Get parent System name
                        if (_store.Systems.TryGetValue(apiDef.ParentId, out var system))
                        {
                            device = system.Name;
                        }
                    }
                }
            }

            rows.Add(new IoBatchRow(
                callId: callId,
                apiCallId: apiCallId,
                flow: key.FlowName,
                device: device,
                api: api,
                inAddress: inputSignal?.Address ?? "",
                inSymbol: inputSignal?.VarName ?? "",
                outAddress: outputSignal?.Address ?? "",
                outSymbol: outputSignal?.VarName ?? ""
            ));
        }

        return rows;
    }

    /// <summary>
    /// DummySignal → DummySignalRow 변환
    /// </summary>
    private List<DummySignalRow> ConvertDummySignalsToRows(GenerationResult result)
    {
        return result.DummySignals
            .Select(signal => new DummySignalRow(
                Flow: signal.FlowName,
                Work: signal.WorkName,
                Call: signal.CallName,
                Symbol: signal.VarName,
                Address: signal.Address,
                Type: signal.IoType
            ))
            .ToList();
    }

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
                    if (FSharpOption<string>.get_IsSome(system.Properties.SystemType))
                    {
                        var systemType = system.Properties.SystemType.Value;
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
                if (FSharpOption<string>.get_IsSome(system.Properties.SystemType))
                {
                    var sysType = system.Properties.SystemType.Value;
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
                if (FSharpOption<string>.get_IsSome(system.Properties.SystemType))
                {
                    var sysType = system.Properties.SystemType.Value;
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
                                _store.AddApiDefWithProperties(apiName, system.Id, false, null, null, 0, "");
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

    /// <summary>
    /// Call 이름으로 검색
    /// </summary>
    private static Call? FindCallByName(DsStore store, string flowName, string workName, string callName)
    {
        var flows = DsQuery.allFlows(store);

        foreach (var flow in flows)
        {
            if (!flow.Name.Equals(flowName, StringComparison.OrdinalIgnoreCase))
                continue;

            var works = DsQuery.worksOf(flow.Id, store);
            foreach (var work in works)
            {
                if (!work.Name.Equals(workName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var calls = DsQuery.callsOf(work.Id, store);
                foreach (var call in calls)
                {
                    if (call.Name.Equals(callName, StringComparison.OrdinalIgnoreCase))
                        return call;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 오류를 다이얼로그 탭에 표시
    /// </summary>
    private void DisplayErrors(Ds2.IOList.GenerationResult result)
    {
        _errorItems.Clear();

        // 오류를 그룹화하고 표시
        var errorGroups = result.Errors
            .GroupBy(e => e.ErrorType)
            .OrderBy(g => g.Key);

        foreach (var group in errorGroups)
        {
            var errorType = FormatErrorType(group.Key);
            var messages = string.Join("\n", group.Select(e => e.Message).Distinct());
            var count = group.Count();

            _errorItems.Add(new ErrorDisplayItem
            {
                ErrorType = $"{errorType} ({count}개)",
                Message = messages
            });
        }

        // 오류 탭 표시
        if (_errorItems.Count > 0)
        {
            ErrorsTabItem.Visibility = Visibility.Visible;
            ErrorCountText.Text = result.Errors.Count().ToString();
        }
        else
        {
            ErrorsTabItem.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 오류 타입을 사용자 친화적 문자열로 변환
    /// </summary>
    private string FormatErrorType(Ds2.IOList.ErrorType errorType)
    {
        return errorType switch
        {
            Ds2.IOList.ErrorType.TemplateNotFound => "템플릿 파일 없음",
            Ds2.IOList.ErrorType.ApiDefNotInTemplate => "API가 템플릿에 정의되지 않음",
            _ => errorType.ToString()
        };
    }
}
