using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// "Cylinder_5" → "Cylinder_#" 로 정규화 (F# 위임).
    /// 사용자 표시는 systemTypePreset 의 템플릿명을 따른다.
    /// </summary>
    private static string NormalizeSystemTypeForDisplay(string name) =>
        SystemTypePreset.normalizeSystemTypeForDisplay(name);

    /// <summary>"Cylinder_#" → ["Cylinder_1".."Cylinder_10"] (F# 위임). 그 외 원본 1개.</summary>
    private static IEnumerable<string> ExpandSystemTypeTemplate(string name) =>
        SystemTypePreset.expandSystemTypeTemplate(name);

    /// <summary>
    /// 시스템 주소 로드 — Cylinder_N 은 Cylinder_# 단일 행으로 묶어 표시.
    /// </summary>
    private void LoadSystemBase()
    {
        try
        {
            _systemBaseRows.Clear();

            // SystemType 목록 — systemTypePreset (Cylinder_# 포함) 우선, AASX Preset 키 보완.
            var availableSet = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                availableSet.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                availableSet.Add(NormalizeSystemTypeForDisplay(kv.Key));
            var systemTypes = availableSet.ToList();

            // 각 Preset 의 BaseAddresses 를 그리드에 표시 — Cylinder_N 은 Cylinder_# 로 그룹.
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
                    IsEnabled  = true,
                    IW_Base    = first.IW_Base?.ToString() ?? "",
                    QW_Base    = first.QW_Base?.ToString() ?? "",
                    MW_Base    = first.MW_Base?.ToString() ?? "",
                });
            }

            RefreshAvailableSystemTypes(systemTypes);
            Step1Section.SystemBaseStatusText.Text = $"{_systemBaseRows.Count}개 추가됨 (가용 타입 {systemTypes.Count}개)";
        }
        catch (Exception ex)
        {
            Step1Section.SystemBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// AvailableSystemTypes 콤보 데이터 갱신 — systemTypePreset 의 템플릿명 그대로 (Cylinder_# 포함).
    /// AASX preset 키는 NormalizeSystemTypeForDisplay 로 정규화 후 합집합. 이미 추가된 타입은 제외.
    /// </summary>
    private void RefreshAvailableSystemTypes(System.Collections.Generic.List<string>? allTypes = null)
    {
        if (allTypes == null)
        {
            var set = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                set.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                set.Add(NormalizeSystemTypeForDisplay(kv.Key));
            allTypes = set.ToList();
        }

        var usedTypes = _systemBaseRows.Select(r => r.SystemType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AvailableSystemTypes.Clear();
        foreach (var t in allTypes)
            if (!usedTypes.Contains(t))
                AvailableSystemTypes.Add(t);
    }

    internal void AddSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        var sysType = Step1Section.NewSystemTypeCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(sysType))
        {
            Step1Section.SystemBaseStatusText.Text = "⚠ 시스템 타입을 선택하거나 입력하세요.";
            return;
        }
        if (_systemBaseRows.Any(r => r.SystemType.Equals(sysType, StringComparison.OrdinalIgnoreCase)))
        {
            Step1Section.SystemBaseStatusText.Text = $"⚠ '{sysType}' 은(는) 이미 추가되어 있습니다.";
            return;
        }

        _systemBaseRows.Add(new SystemBaseRow
        {
            SystemType = sysType,
            IsEnabled  = true,
            IW_Base    = Step1Section.NewSystemIwBox.Text?.Trim() ?? "",
            QW_Base    = Step1Section.NewSystemQwBox.Text?.Trim() ?? "",
            MW_Base    = Step1Section.NewSystemMwBox.Text?.Trim() ?? "",
        });

        // 입력 필드 리셋
        Step1Section.NewSystemTypeCombo.Text = "";
        Step1Section.NewSystemIwBox.Text = "";
        Step1Section.NewSystemQwBox.Text = "";
        Step1Section.NewSystemMwBox.Text = "";

        RefreshAvailableSystemTypes();
        Step1Section.SystemBaseStatusText.Text = $"✓ '{sysType}' 추가됨 ({_systemBaseRows.Count}개)";
    }

    internal void RemoveSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        if (Step1Section.SystemBaseGrid.SelectedItem is SystemBaseRow row)
        {
            var name = row.SystemType;
            _systemBaseRows.Remove(row);
            RefreshAvailableSystemTypes();
            Step1Section.SystemBaseStatusText.Text = $"✓ '{name}' 삭제됨 ({_systemBaseRows.Count}개)";
        }
        else
        {
            Step1Section.SystemBaseStatusText.Text = "⚠ 삭제할 행을 먼저 선택하세요.";
        }
    }

    /// <summary>
    /// SystemType 별 BaseAddress 를 FBTagMapPresets 에서 직접 읽어온다 (외부 파일 불필요).
    /// Preset 이 없는 SystemType 은 결과에 포함되지 않는다.
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseSystemBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        var presets = FBTagMapStore.LoadAll(_store);
        foreach (var kv in presets)
        {
            var ba = kv.Value.BaseAddresses;
            if (ba == null) continue;
            var iw = TryParseNum(ba.InputBase);
            var qw = TryParseNum(ba.OutputBase);
            var mw = TryParseNum(ba.MemoryBase);
            if (iw == null && qw == null && mw == null) continue;
            result[kv.Key] = (iw, qw, mw);
        }
        return result;
    }

    /// <summary>"%IW1234.0.0" / "4000" 등에서 첫 정수를 추출. 실패 시 null.</summary>
    private static int? TryParseNum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    /// <summary>SystemBase 행을 FBTagMapPresets 에 직접 반영 — 텍스트 round-trip 불필요.</summary>
    internal void SaveSystemBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = _systemBaseRows.Where(r => r.IsEnabled).ToList();
            var cp = GetOrCreateControlProps();
            if (cp != null)
            {
                foreach (var row in enabled)
                    foreach (var concrete in ExpandSystemTypeTemplate(row.SystemType))
                    {
                        if (!cp.FBTagMapPresets.TryGetValue(concrete, out var preset))
                        {
                            preset = new FBTagMapPreset();
                            cp.FBTagMapPresets[concrete] = preset;
                        }
                        var ba = preset.BaseAddresses ?? new FBBaseAddressSet();
                        if (int.TryParse(row.IW_Base, out _)) ba.InputBase  = row.IW_Base;
                        if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                        if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                        preset.BaseAddresses = ba;
                    }
            }
            Step1Section.SystemBaseStatusText.Text = $"✓ 저장 완료 ({enabled.Count}개 시스템) | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox(
                $"시스템 주소 설정이 저장되었습니다.\n\n사용 중인 시스템: {enabled.Count}개",
                "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
            Step1Section.SystemBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }
}
