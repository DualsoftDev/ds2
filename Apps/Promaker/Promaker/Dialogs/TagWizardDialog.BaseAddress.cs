using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    private void LoadTemplateFileList()
    {
        LoadSystemBase();
        LoadFlowBase();
        LoadDeviceTemplateList();
    }

    private static string NormalizeSystemTypeForDisplay(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        const string prefix = "Cylinder_";
        if (name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), out _))
        {
            return prefix + "#";
        }

        return name;
    }

    private static IEnumerable<string> ExpandSystemTypeTemplate(string name)
    {
        if (string.Equals(name, "Cylinder_#", StringComparison.Ordinal))
        {
            return DevicePresets.Entries3
                .Select(t => t.Item1)
                .Where(s => s.StartsWith("Cylinder_") && int.TryParse(s.AsSpan("Cylinder_".Length), out _));
        }

        return [name];
    }

    private void LoadSystemBase()
    {
        try
        {
            _systemBaseRows.Clear();

            var availableSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                availableSet.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                availableSet.Add(NormalizeSystemTypeForDisplay(kv.Key));

            var systemTypes = availableSet.ToList();
            var existingConfig = ParseSystemBaseFile();
            var grouped = existingConfig
                .GroupBy(kv => NormalizeSystemTypeForDisplay(kv.Key), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
            {
                var first = g.First().Value;
                _systemBaseRows.Add(new SystemBaseRow
                {
                    SystemType = g.Key,
                    IsEnabled = true,
                    IW_Base = first.IW_Base?.ToString() ?? "",
                    QW_Base = first.QW_Base?.ToString() ?? "",
                    MW_Base = first.MW_Base?.ToString() ?? "",
                });
            }

            RefreshAvailableSystemTypes(systemTypes);
            SystemBaseStatusText.Text = $"{_systemBaseRows.Count}개 추가됨 (가용 타입 {systemTypes.Count}개)";
        }
        catch (Exception ex)
        {
            SystemBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    private void RefreshAvailableSystemTypes(List<string>? allTypes = null)
    {
        if (allTypes == null)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                set.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                set.Add(NormalizeSystemTypeForDisplay(kv.Key));
            allTypes = set.ToList();
        }

        var usedTypes = _systemBaseRows
            .Select(r => r.SystemType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AvailableSystemTypes.Clear();
        foreach (var t in allTypes)
        {
            if (!usedTypes.Contains(t))
                AvailableSystemTypes.Add(t);
        }
    }

    private void AddSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        var sysType = NewSystemTypeCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(sysType))
        {
            SystemBaseStatusText.Text = "⚠ 시스템 타입을 선택하거나 입력하세요.";
            return;
        }

        if (_systemBaseRows.Any(r => r.SystemType.Equals(sysType, StringComparison.OrdinalIgnoreCase)))
        {
            SystemBaseStatusText.Text = $"⚠ '{sysType}' 은(는) 이미 추가되어 있습니다.";
            return;
        }

        _systemBaseRows.Add(new SystemBaseRow
        {
            SystemType = sysType,
            IsEnabled = true,
            IW_Base = NewSystemIwBox.Text?.Trim() ?? "",
            QW_Base = NewSystemQwBox.Text?.Trim() ?? "",
            MW_Base = NewSystemMwBox.Text?.Trim() ?? "",
        });

        NewSystemTypeCombo.Text = "";
        NewSystemIwBox.Text = "";
        NewSystemQwBox.Text = "";
        NewSystemMwBox.Text = "";

        RefreshAvailableSystemTypes();
        SystemBaseStatusText.Text = $"✓ '{sysType}' 추가됨 ({_systemBaseRows.Count}개)";
    }

    private void RemoveSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        if (SystemBaseGrid.SelectedItem is not SystemBaseRow row)
        {
            SystemBaseStatusText.Text = "⚠ 삭제할 행을 먼저 선택하세요.";
            return;
        }

        var name = row.SystemType;
        _systemBaseRows.Remove(row);
        RefreshAvailableSystemTypes();
        SystemBaseStatusText.Text = $"✓ '{name}' 삭제됨 ({_systemBaseRows.Count}개)";
    }

    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseSystemBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        var presets = FBTagMapStore.LoadAll(_store);
        foreach (var kv in presets)
        {
            var ba = kv.Value.BaseAddresses;
            if (ba == null)
                continue;

            result[kv.Key] = (TryParseNum(ba.InputBase), TryParseNum(ba.OutputBase), TryParseNum(ba.MemoryBase));
        }

        return result;
    }

    private static int? TryParseNum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    private void SaveSystemBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = _systemBaseRows.Where(r => r.IsEnabled).ToList();
            var cp = GetOrCreateControlProps();
            if (cp != null)
            {
                foreach (var row in enabled)
                {
                    foreach (var concrete in ExpandSystemTypeTemplate(row.SystemType))
                    {
                        if (!cp.FBTagMapPresets.TryGetValue(concrete, out var preset))
                        {
                            preset = new FBTagMapPreset();
                            cp.FBTagMapPresets[concrete] = preset;
                        }

                        var ba = preset.BaseAddresses ?? new FBBaseAddressSet();
                        if (int.TryParse(row.IW_Base, out _)) ba.InputBase = row.IW_Base;
                        if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                        if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                        preset.BaseAddresses = ba;
                    }
                }
            }

            SystemBaseStatusText.Text = $"✓ 저장 완료 ({enabled.Count}개 시스템) | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox(
                $"시스템 주소 설정이 저장되었습니다.\n\n사용 중인 시스템: {enabled.Count}개",
                "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
            SystemBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }

    private void LoadFlowBase()
    {
        try
        {
            _flowBaseRows.Clear();

            var projects = Queries.allProjects(_store);
            var flows = projects.IsEmpty
                ? new List<Flow>()
                : Queries.activeSystemsOf(projects.Head.Id, _store)
                    .SelectMany(sys => Queries.flowsOf(sys.Id, _store))
                    .ToList();
            var flowNames = flows
                .Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            var existingConfig = ParseFlowBaseFile();
            for (int i = 0; i < flowNames.Count; i++)
            {
                var flowName = flowNames[i];
                var row = new FlowBaseRow { FlowName = flowName };
                if (existingConfig.TryGetValue(flowName, out var config))
                {
                    row.IW_Base = config.IW_Base?.ToString() ?? "";
                    row.QW_Base = config.QW_Base?.ToString() ?? "";
                    row.MW_Base = config.MW_Base?.ToString() ?? "";
                }
                else
                {
                    int baseAddress = i * 1000;
                    row.IW_Base = baseAddress.ToString();
                    row.QW_Base = baseAddress.ToString();
                    row.MW_Base = baseAddress.ToString();
                }

                _flowBaseRows.Add(row);
            }

            FlowBaseStatusText.Text = flowNames.Count > 0
                ? $"{flowNames.Count}개의 Flow를 찾았습니다."
                : "프로젝트에 Flow가 없습니다.";
        }
        catch (Exception ex)
        {
            FlowBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseFlowBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in _store.Flows.Values)
        {
            var cfpOpt = flow.GetControlProperties();
            if (!FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt))
                continue;
            var ov = cfpOpt.Value.BaseAddressOverride;
            if (!FSharpOption<FBBaseAddressSet>.get_IsSome(ov))
                continue;

            var ba = ov.Value;
            result[flow.Name] = (TryParseNum(ba.InputBase), TryParseNum(ba.OutputBase), TryParseNum(ba.MemoryBase));
        }

        return result;
    }

    private void SaveFlowBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = _flowBaseRows.ToDictionary(r => r.FlowName, StringComparer.OrdinalIgnoreCase);
            foreach (var flow in _store.Flows.Values)
            {
                if (!rows.TryGetValue(flow.Name, out var row))
                    continue;
                if (string.IsNullOrWhiteSpace(row.IW_Base)
                    && string.IsNullOrWhiteSpace(row.QW_Base)
                    && string.IsNullOrWhiteSpace(row.MW_Base))
                {
                    continue;
                }

                var cfpOpt = flow.GetControlProperties();
                ControlFlowProperties cfp;
                if (FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt))
                {
                    cfp = cfpOpt.Value;
                }
                else
                {
                    cfp = new ControlFlowProperties();
                    flow.SetControlProperties(cfp);
                }

                var ba = new FBBaseAddressSet();
                if (int.TryParse(row.IW_Base, out _)) ba.InputBase = row.IW_Base;
                if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                cfp.BaseAddressOverride = FSharpOption<FBBaseAddressSet>.Some(ba);
            }

            FlowBaseStatusText.Text = $"✓ 저장 완료 | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox("Flow 주소 설정이 저장되었습니다.", "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
            FlowBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }
}
