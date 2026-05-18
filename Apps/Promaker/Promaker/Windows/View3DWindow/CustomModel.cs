using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Ds2.Core.Store;
using Promaker.Dialogs;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class View3DWindow
{
    private void InitCustomModelRegistry()
    {
        _customModelRegistry = new CustomModelRegistry();
        _customModelRegistry.LoadAll();

        // F# DevicePresets에 커스텀 이름 등록
        Ds2.View3D.DevicePresets.registerCustomNames(_customModelRegistry.GetRegisteredNames());

        // ThreeDViewState에 registry 주입 (WebView2로 전달될 수 있도록)
        _vm.SetCustomModelRegistry(_customModelRegistry);

        RefreshCustomModelList();
    }

    private void RefreshCustomModelList()
    {
        CustomModelList.ItemsSource = null;
        CustomModelList.ItemsSource = _customModelRegistry?.ModelNames ?? Array.Empty<string>();
    }

    private IEnumerable<string> GetProjectSystemTypes()
    {
        if (_store == null) return Enumerable.Empty<string>();
        return _store.Systems.Values
            .Where(s => Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.SystemType))
            .Select(s => s.SystemType.Value)
            .Where(st => !string.IsNullOrEmpty(st))
            .Distinct()
            .OrderBy(x => x);
    }

    private Dictionary<string, List<string>> GetSystemTypeApiDefMap()
    {
        // 같은 SystemType을 가진 모든 System의 ApiDef 이름을 합집합으로 수집.
        // 합집합이어야 하는 이유: 하나의 RB.device.json이 동일 SystemType의 모든 인스턴스에
        // 공유되므로, dirs 키에는 어떤 인스턴스든 Going이 될 수 있는 ApiDef가 모두 있어야 한다.
        // 각 디바이스는 자기가 보유한 ApiDef에 해당하는 dir만 런타임에 트리거한다.
        var map = new Dictionary<string, HashSet<string>>();
        if (_store == null) return new Dictionary<string, List<string>>();
        foreach (var system in _store.Systems.Values)
        {
            if (!Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(system.SystemType)) continue;
            var st = system.SystemType.Value;
            if (string.IsNullOrEmpty(st)) continue;
            if (!map.TryGetValue(st, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map[st] = set;
            }
            foreach (var apiDef in _store.ApiDefs.Values)
            {
                if (apiDef.ParentId == system.Id && !string.IsNullOrEmpty(apiDef.Name))
                    set.Add(apiDef.Name);
            }
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(n => n).ToList());
    }

    private void AddCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (_customModelRegistry == null) return;

        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var apiDefMap = GetSystemTypeApiDefMap();
        var dlg = new CustomModelDialog(_customModelRegistry, wwwroot, this, GetProjectSystemTypes(), apiDefMap);

        if (dlg.ShowDialog() == true && dlg.RegisteredSystemType != null)
        {
            // F# 프리셋에 이름 등록
            Ds2.View3D.DevicePresets.registerCustomName(dlg.RegisteredSystemType);
            RefreshCustomModelList();
            // 씬 리빌드 (새 모델 반영)
            RebuildSceneIfReady();
        }
    }

    private void DeleteCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (_customModelRegistry == null) return;
        var selected = CustomModelList.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;

        var result = MessageBox.Show(
            $"커스텀 모델 \"{selected}\"을 삭제하시겠습니까?",
            "모델 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _customModelRegistry.Delete(selected);
            RefreshCustomModelList();
            RebuildSceneIfReady();
        }
    }

    private void CustomModelList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_customModelRegistry == null) return;
        var selected = CustomModelList.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;

        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var apiDefMap = GetSystemTypeApiDefMap();
        var dlg = new CustomModelDialog(_customModelRegistry, wwwroot, this, GetProjectSystemTypes(), apiDefMap)
        {
            EditingSystemType = selected
        };

        if (dlg.ShowDialog() == true)
        {
            RefreshCustomModelList();
            RebuildSceneIfReady();
        }
    }
}
