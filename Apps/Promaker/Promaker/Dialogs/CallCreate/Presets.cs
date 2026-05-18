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
    private static string NormalizeCylinder(string modelType)
    {
        const string prefix = "Cylinder_";
        if (modelType.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(modelType.AsSpan(prefix.Length), out _))
        {
            return prefix + "#";
        }
        return modelType;
    }

    private static void CreateDefaultPresetFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(PresetFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Cylinder_N (N=1,2,3,...) 은 모두 ADV;RET — 단일 템플릿 "Cylinder_#" 로 합친다.
            // ('#' 마커는 AddCall 다이얼로그에서 ApiCall 복제 카운트로 치환됨)
            // ApiList 가 비어도 entry 자체는 보존 (예: ModeStn — Operation Mode FB).
            var defaults = Ds2.Core.Store.DevicePresets.Entries()
                .Select(t => (modelType: NormalizeCylinder(t.Item1), apiList: t.Item2 ?? ""))
                .GroupBy(x => x.modelType, StringComparer.Ordinal)
                .Select(g => $"{g.First().apiList}:{g.Key}")
                .ToArray();

            var json = JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PresetFilePath, json);
        }
        catch { /* 디렉토리 생성/쓰기 실패는 무시 — fallback 으로 진행 */ }
    }

    /// <summary>
    /// '#' 을 포함한 SystemType 은 ApiCall 개수로 치환되는 템플릿 — Add 시점에 동적 결정.
    /// 예: "Cylinder_#", "abcd#" → ApiCall 개수 N 일 때 "Cylinder_N", "abcdN".
    /// </summary>
    private static bool IsTemplateModel(string? modelType) =>
        !string.IsNullOrEmpty(modelType) && modelType.Contains('#');

    private void LoadPresets()
    {
        PresetComboBox.Items.Clear();

        // 프리셋 소스 (병합 모드 — 자동 동기화):
        //  (1) systemTypePreset.json — 사용자 편집/추가분 보존
        //  (2) DevicePresets.Entries() — 새로 등록된 SystemType 자동 합류 (Robot*/ModeStn 등)
        // 같은 modelType 이 양쪽에 있으면 파일 (사용자 편집) 우선.
        var byModelType = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedTypes = new List<string>();

        foreach (var preset in LoadPresetsFromFile())
        {
            var parts = preset.Split(':');
            if (parts.Length == 2)
            {
                if (!byModelType.ContainsKey(parts[1]))
                    orderedTypes.Add(parts[1]);
                byModelType[parts[1]] = parts[0];
            }
        }

        // DevicePresets.Entries() 의 미등록 항목 자동 추가 (ApiList 비어도 표시 — ModeStn 등).
        foreach (var t in Ds2.Core.Store.DevicePresets.Entries())
        {
            var modelType = NormalizeCylinder(t.Item1);
            if (!byModelType.ContainsKey(modelType))
            {
                orderedTypes.Add(modelType);
                byModelType[modelType] = t.Item2 ?? "";
            }
        }

        var rawEntries = orderedTypes
            .Select(mt => (modelType: mt, apiList: byModelType[mt]))
            .ToList();

        foreach (var (modelType, apiList) in rawEntries)
        {
            PresetComboBox.Items.Add(new ComboBoxItem
            {
                Content = modelType,
                Tag = $"{apiList}|{modelType}",
            });
        }

        // 직접 입력 항목 추가 (Tag = null → Dummy SystemType)
        PresetComboBox.Items.Add(new ComboBoxItem { Content = "직접 입력", Tag = null });

        // 마지막 선택 복원 — 일치 항목 없으면 첫 번째.
        var last = _lastSelectedPreset;
        var matchedIndex = -1;
        if (!string.IsNullOrEmpty(last))
        {
            for (int i = 0; i < PresetComboBox.Items.Count; i++)
            {
                if (PresetComboBox.Items[i] is ComboBoxItem cbi
                    && string.Equals(cbi.Content as string, last, StringComparison.Ordinal))
                {
                    matchedIndex = i;
                    break;
                }
            }
        }
        PresetComboBox.SelectedIndex = matchedIndex >= 0 ? matchedIndex : 0;
    }
}
