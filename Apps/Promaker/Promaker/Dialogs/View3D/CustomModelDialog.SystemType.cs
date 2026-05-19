using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Windows;

public partial class CustomModelDialog
{
    private string GetSystemType()
        => (SystemTypeInput.SelectedItem as string)?.Trim() ?? "";

    private bool TrySelectSystemType(string name)
    {
        foreach (var item in SystemTypeInput.Items)
        {
            if (item is string s && string.Equals(s, name, StringComparison.Ordinal))
            {
                SystemTypeInput.SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 프로젝트 ApiDef ∪ 전역 device.json apiDefs ∪ 보류 중인 신규 항목의 합집합.
    /// 동일 이름이면 프로젝트가 우선(이름·순서). 대소문자 무시 중복 제거.
    /// </summary>
    private List<string> GetEffectiveApiDefs(string systemType)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_systemTypeApiDefs.TryGetValue(systemType, out var fromProject))
        {
            foreach (var name in fromProject)
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
        }

        foreach (var name in _registry.GetApiDefs(systemType))
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);

        if (_pendingNewApiDefs.TryGetValue(systemType, out var pending))
        {
            foreach (var name in pending)
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
        }

        return result;
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void PopulateSystemTypeDropdown()
    {
        SystemTypeInput.Items.Clear();

        var registered = _registry.Models.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtinNames = Ds2.View3D.DevicePresets.KnownNames;

        // 미등록 SystemType (커스텀 모델 필요한 항목) — 선택 가능
        var unregistered = _projectSystemTypes
            .Where(st => !registered.Contains(st) && !builtinNames.Contains(st))
            .ToList();

        if (unregistered.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 프로젝트 SystemType (미등록) ──", IsEnabled = false, Foreground = Brush("#f59e0b") });
            foreach (var st in unregistered)
                SystemTypeInput.Items.Add(st);
        }

        // 이미 커스텀 등록된 SystemType — 선택 가능 (편집용)
        var customRegistered = _projectSystemTypes.Where(st => registered.Contains(st)).ToList();
        // 프로젝트에 없지만 레지스트리에는 있는 커스텀 모델도 표시
        var extraCustom = _registry.ModelNames.Where(n => !_projectSystemTypes.Contains(n)).ToList();

        if (customRegistered.Count > 0 || extraCustom.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 커스텀 모델 등록됨 ──", IsEnabled = false, Foreground = Brush("#10b981") });
            foreach (var st in customRegistered.Concat(extraCustom))
                SystemTypeInput.Items.Add(st);
        }

        // 내장 모델 (참고용, 선택 불가)
        var builtinUsed = _projectSystemTypes.Where(st => builtinNames.Contains(st)).ToList();
        if (builtinUsed.Count > 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "── 내장 모델 (변경 불가) ──", IsEnabled = false, Foreground = Brush("#64748b") });
            foreach (var st in builtinUsed)
                SystemTypeInput.Items.Add(new ComboBoxItem
                    { Content = $"{st} (내장)", IsEnabled = false, Foreground = Brush("#64748b") });
        }

        // 아무것도 없을 때 안내
        if (SystemTypeInput.Items.Count == 0)
        {
            SystemTypeInput.Items.Add(new ComboBoxItem
                { Content = "프로젝트에 등록 가능한 SystemType이 없습니다", IsEnabled = false, Foreground = Brush("#64748b") });
        }
    }

    private void SystemTypeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var typeName = GetSystemType();
        if (!string.IsNullOrEmpty(typeName))
        {
            // JSON이 비어있거나 이전 자동 생성 템플릿이면 교체
            var currentJson = JsonEditor.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(currentJson) || currentJson == _lastGeneratedTemplate.Trim())
            {
                GenerateJsonTemplate(typeName);
            }
        }

        UpdateJsonEditorEnabled();
        ValidateForm();
    }

    private void CreateNew_Click(object sender, RoutedEventArgs e)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SystemTypeInput.Items)
            if (item is string s) existing.Add(s);
        foreach (var name in _registry.ModelNames) existing.Add(name);
        foreach (var name in Ds2.View3D.DevicePresets.KnownNames) existing.Add(name);

        var dlg = new NewCustomModelDialog(this, existing);
        if (dlg.ShowDialog() != true) return;

        var newName = dlg.ResultSystemType;
        if (string.IsNullOrWhiteSpace(newName)) return;

        // 보류 메타에 저장 → GenerateJsonTemplate이 union으로 사용
        _pendingNewApiDefs[newName] = dlg.ResultApiDefs;

        // 드롭다운에 신규 항목 추가 후 선택
        SystemTypeInput.Items.Insert(0, new ComboBoxItem
            { Content = "── 빠른 등록 (미저장) ──", IsEnabled = false, Foreground = Brush("#3b82f6") });
        SystemTypeInput.Items.Insert(1, newName);
        SystemTypeInput.SelectedIndex = 1;
    }
}
