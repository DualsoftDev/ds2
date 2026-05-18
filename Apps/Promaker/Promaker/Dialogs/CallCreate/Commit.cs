using AAStoPLC.TagWizard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.FSharp.Collections;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

public partial class CallCreateDialog
{
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // 다이얼로그 오픈 직후 외부에서 눌린 Enter 가 IsDefault 추가 버튼을 트리거하는 경우 무시.
        if (System.DateTime.UtcNow - _readyAt < _commitGrace) return;

        if (ModeTabControl.SelectedIndex == 0)
            CommitBasicTab();
        else
            CommitAdvancedTab();
    }

    private (string alias, List<string> apiNames)? ValidateAliasAndApiNames(TextBox aliasBox, TextBox apiNameBox)
    {
        var alias = aliasBox.Text.Trim();
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { return null; }
        if (aliasResult.IsAliasDotForbidden) { DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return null; }

        var apiResult = InputValidation.validateApiNames(apiNameBox.Text);
        if (apiResult.IsEmptyInput || apiResult.IsEmptyAfterParse)
        {
            CreateDeviceSystem = false;
            return (alias, new List<string> { "Api_None" });
        }
        if (apiResult.IsApiNameDotForbidden) { DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return null; }

        var apiNames = ((InputValidation.ApiNameValidationResult.Valid)apiResult).Item.ToList();
        return (alias, apiNames);
    }

    private void CommitBasicTab()
    {
        var result = ValidateAliasAndApiNames(BasicAliasTextBox, BasicApiNameTextBox);
        if (result is null) return;

        var (alias, apiNames) = result.Value;

        // SystemType 가져오기 - 프리셋에 따라 고급 탭에서 설정한 값 사용
        SelectedSystemType = GetSystemTypeForCurrentPreset();

        // 마지막 선택 프리셋 저장 (메모리) — 다음 다이얼로그 오픈 시 자동 선택용.
        if (PresetComboBox.SelectedItem is ComboBoxItem { Content: string label })
            _lastSelectedPreset = label;

        // 추가 기능이 펼쳐진 경우 복수 생성
        if (AdvancedExpander.IsExpanded)
        {
            if (RadioCallReplication.IsChecked == true)
                CommitCallReplication(alias, apiNames);
            else
                CommitApiCallReplication(alias, apiNames);
            return;
        }

        // 단일 생성
        var names = apiNames.Select(n => $"{alias}.{n}").ToList();
        Mode = CallCreateMode.CallReplication;
        CallNames = names;
        DialogResult = true;
    }

    // Tag 형식: "ADV;RET|Unit"  (sysType|modelType)
    private static string? ParseSysType(string? tag) =>
        tag?.Split('|') is [var s, ..] ? s : tag;

    private static string? ParseModelType(string? tag) =>
        tag?.Split('|') is [_, var m] ? m : null;

    private string? GetSystemTypeForCurrentPreset()
    {
        if (PresetComboBox.SelectedItem is not ComboBoxItem item)
            return null;
        // 직접 입력 선택 시 → Dummy (Tag=null)
        if (IsCustomInputSelected()) return "Dummy";

        var modelType = ParseModelType(item.Tag?.ToString());

        // ApiCall 개수 N 추출 (Advanced + ApiCallReplication 모드일 때만).
        int GetApiCallCount()
        {
            if (AdvancedExpander?.IsExpanded == true
                && RadioApiCallReplication?.IsChecked == true
                && int.TryParse(ApiCallCountTextBox?.Text?.Trim(), out var parsed)
                && parsed >= 1)
                return parsed;
            return 1;
        }

        // '#' 템플릿 → ApiCall 개수로 치환.
        if (IsTemplateModel(modelType))
            return modelType!.Replace("#", GetApiCallCount().ToString());

        return modelType;
    }

    private void CommitCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(CallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = Device.generateDeviceAliases(alias, count);
        var names = Device.generateCallNames(
            ListModule.OfSeq(deviceAliases),
            ListModule.OfSeq(apiNames));

        Mode = CallCreateMode.CallReplication;
        CallNames = names.ToList();
        DialogResult = true;
    }

    private void CommitApiCallReplication(string alias, List<string> apiNames)
    {
        if (!int.TryParse(ApiCallCountTextBox.Text.Trim(), out int count) || count < 1 || count > 100)
        {
            DialogHelpers.Warn("개수는 1~100 사이의 숫자를 입력해주세요."); return;
        }

        var deviceAliases = Device.generateDeviceAliases(alias, count);

        // 편의상 첫 번째 apiName 기준. 여러 apiName → 여러 Call.
        // 각 Call별로 DeviceAliases를 동일하게 사용.
        if (apiNames.Count == 1)
        {
            Mode = CallCreateMode.ApiCallReplication;
            CallDevicesAlias = alias;
            CallApiName = apiNames[0];
            DeviceAliases = deviceAliases;
            DialogResult = true;
        }
        else
        {
            // 여러 ApiName + ApiCall 복제: apiName별로 별도 Call
            // CallNames로 모든 조합을 전달하되, DeviceAliases도 함께 전달
            Mode = CallCreateMode.ApiCallReplication;
            CallDevicesAlias = alias;
            CallApiName = apiNames[0]; // 첫 번째 (multi는 별도 처리)
            DeviceAliases = deviceAliases;

            // multi-apiName은 CallNames에도 기록 (NodeCommands에서 분기)
            var names = apiNames.Select(n => $"{alias}.{n}").ToList();
            CallNames = names;
            DialogResult = true;
        }
    }

    private void CommitAdvancedTab()
    {
        var alias = AdvAliasFilterBox.Text.Trim();
        var aliasResult = InputValidation.validateDevicesAlias(alias);
        if (aliasResult.IsEmptyAlias) { return; }
        if (aliasResult.IsAliasDotForbidden) { DialogHelpers.Warn("DevicesAlias에는 '.'을 사용할 수 없습니다."); return; }

        var apiName = AdvApiNameFilterBox.Text.Trim();
        var apiResult = InputValidation.validateApiNames(apiName);
        if (apiResult.IsEmptyInput || apiResult.IsEmptyAfterParse) { DialogHelpers.Warn("ApiName을 입력해주세요."); return; }
        if (apiResult.IsApiNameDotForbidden) { DialogHelpers.Warn("ApiName에는 '.'을 사용할 수 없습니다."); return; }

        var selected = ApiDefListBox.SelectedItems.OfType<ApiDefMatch>().ToList();
        if (selected.Count == 0)
        {
            DialogHelpers.Warn("ApiDef를 선택해주세요.\n\nDevice System이 없으면 '기본' 탭에서 먼저 생성해주세요."); return;
        }

        Mode = CallCreateMode.ApiDefPicker;
        SelectedApiDefs = selected;
        DevicesAlias = alias;
        ApiName = apiName;
        CallNames = [$"{alias}.{apiName}"];
        DialogResult = true;
    }
}
