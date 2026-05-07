using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using AAStoPLC.TagWizard;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    private void LoadDeviceTemplateList()
    {
        try
        {
            DeviceTemplateListBox.Items.Clear();

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            static bool IsTemplate(string n) => !string.IsNullOrEmpty(n) && n.Contains('#');

            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
            {
                if (!IsTemplate(t))
                    names.Add(t);
            }

            foreach (var kv in FBTagMapStore.LoadAll(_store))
            {
                if (!IsTemplate(kv.Key))
                    names.Add(kv.Key);
            }

            if (names.Count == 0)
            {
                DeviceTemplateStatusText.Text = "등록된 SystemType 프리셋이 없습니다. 프로젝트 속성에서 프리셋을 추가하세요.";
                return;
            }

            _ = FBTagMapStore.LoadAll(_store);
            foreach (var n in names)
                DeviceTemplateListBox.Items.Add(n);

            DeviceTemplateListBox.SelectedItem = names.First();
            DeviceTemplateStatusText.Text = $"{names.Count}개의 SystemType 이 발견되었습니다.";
        }
        catch (Exception ex)
        {
            DeviceTemplateStatusText.Text = $"목록 로드 실패: {ex.Message}";
        }
    }

    private void DeviceTemplateListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeviceTemplateListBox.SelectedItem is string systemType)
            LoadDeviceTemplate(systemType);
    }

    private void LoadDeviceTemplate(string systemType)
    {
        _isLoadingTemplate = true;
        try
        {
            if (systemType.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                systemType = Path.GetFileNameWithoutExtension(systemType);

            _currentDeviceTemplateFile = systemType;
            CurrentDeviceTemplateText.Text = systemType;

            ClearSignalGrids();
            ReloadWizApiNames(systemType);

            var presets = FBTagMapStore.LoadAll(_store);
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new FBTagMapPresetDto();

            var currentFb = presetDto.FBTagMapName ?? "";
            if (GlobalFBTypeCombo != null)
            {
                GlobalFBTypeCombo.SelectionChanged -= GlobalFBType_Changed;
                GlobalFBTypeCombo.SelectedItem =
                    string.IsNullOrEmpty(currentFb) ? null : WizFBTypes.FirstOrDefault(x => x == currentFb);
                GlobalFBTypeCombo.SelectionChanged += GlobalFBType_Changed;
            }

            foreach (var sec in AllSections())
                LoadSectionRows(sec, presetDto, currentFb);

            RefreshChunkedViewsIfActive();

            var totalCount = presetDto.IwPatterns.Count + presetDto.QwPatterns.Count + presetDto.MwPatterns.Count;
            int iwSpare = presetDto.IwPatterns.Count(p => p.IsSpare);
            int qwSpare = presetDto.QwPatterns.Count(p => p.IsSpare);
            int mwSpare = presetDto.MwPatterns.Count(p => p.IsSpare);
            string SparePart(int n) => n > 0 ? $" ({n} 예비)" : "";
            DeviceTemplateStatusText.Text =
                $"✓ 로드 완료 | IW: {presetDto.IwPatterns.Count}{SparePart(iwSpare)}, " +
                $"QW: {presetDto.QwPatterns.Count}{SparePart(qwSpare)}, " +
                $"MW: {presetDto.MwPatterns.Count}{SparePart(mwSpare)} | 총 {totalCount}개 신호";

            var apis = presetDto.IwPatterns.Select(p => p.ApiName)
                .Concat(presetDto.QwPatterns.Select(p => p.ApiName))
                .Concat(presetDto.MwPatterns.Select(p => p.ApiName))
                .Where(n => !string.IsNullOrWhiteSpace(n)
                         && !string.Equals(n, IoConstants.ApiNoneSentinel, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(n, "-", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            LoadAuxPortRows(systemType, apis);
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 로드 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            DeviceTemplateStatusText.Text = $"로드 실패: {ex.Message}";
            ClearSignalGrids();
        }
        finally
        {
            _isLoadingTemplate = false;
        }

        PersistCurrentPreset();
    }

    private void ClearSignalGrids()
    {
        _iwSignalRows.Clear();
        _qwSignalRows.Clear();
        _mwSignalRows.Clear();
        _auxPortRows.Clear();
    }

    private void LoadAuxPortRows(string systemType, IEnumerable<string> apis)
    {
        _auxPortRows.Clear();

        var presets = FBTagMapStore.LoadAll(_store);
        var fbType = "";
        var existingAutoMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingComMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (presets.TryGetValue(systemType, out var preset))
        {
            fbType = preset.FBTagMapName ?? "";
            if (preset.AutoAuxPortMap != null)
            {
                foreach (var kv in preset.AutoAuxPortMap)
                    existingAutoMap[kv.Key] = kv.Value;
            }

            if (preset.ComAuxPortMap != null)
            {
                foreach (var kv in preset.ComAuxPortMap)
                    existingComMap[kv.Key] = kv.Value;
            }
        }

        if (GlobalFBTypeCombo != null)
        {
            GlobalFBTypeCombo.SelectionChanged -= GlobalFBType_Changed;
            GlobalFBTypeCombo.SelectedItem =
                string.IsNullOrEmpty(fbType) ? null : WizFBTypes.FirstOrDefault(x => x == fbType);
            GlobalFBTypeCombo.SelectionChanged += GlobalFBType_Changed;
        }

        foreach (var api in apis)
        {
            var row = new AuxPortRow
            {
                ApiName = api,
                TargetFBType = fbType,
                AutoAuxPort = existingAutoMap.TryGetValue(api, out var a) ? a : "",
                ComAuxPort = existingComMap.TryGetValue(api, out var c) ? c : "",
            };
            _auxPortRows.Add(HookAutoSave(row));
        }
    }
}
