using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using AAStoPLC.TagWizard;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>SignalPatternRow / AuxPortRow 가 자체 정의한 어떤 INPC 변경에서도 자동 persist.
    /// (필터 없이 모든 PropertyChanged 에 반응 — debounce 가 다회 호출을 1회 save 로 합쳐줌.)</summary>
    private TRow HookAutoSave<TRow>(TRow row) where TRow : INotifyPropertyChanged
    {
        row.PropertyChanged += (_, _) => PersistCurrentPreset();
        return row;
    }

    /// <summary>
    /// 현재 SystemType 의 Preset 을 저장 (auto-save).
    /// LoadDeviceTemplate 진행 중에는 race 방지 위해 skip.
    /// 짧은 시간(150ms) 내 다회 호출은 debounce 처리 — 그리드 일괄 변경 시 마지막 한 번만 실제 save.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _persistDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };
    private bool _persistDebounceWired;

    private void PersistCurrentPreset()
    {
        if (_isLoadingTemplate) return;
        if (string.IsNullOrWhiteSpace(_currentDeviceTemplateFile)) return;

        if (!_persistDebounceWired)
        {
            _persistDebounceWired = true;
            _persistDebounce.Tick += (_, _) => { _persistDebounce.Stop(); PersistCurrentPresetNow(); };
        }
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    /// <summary>실제 저장 본체 — debounce 만료 시 1회만 실행.</summary>
    private void PersistCurrentPresetNow()
    {
        var systemType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(systemType)) return;

        // LoadOne — 단일 SystemType DTO 만 가져옴 (debounced auto-save 빈번 호출 최적화).
        var presetDto = FBTagMapStore.LoadOne(_store, systemType) ?? new FBTagMapPresetDto();

        var selectedFb = GlobalFBTypeCombo?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedFb))
            presetDto.FBTagMapName = selectedFb;

        // IW/QW/MW 모든 섹션 일괄 저장.
        foreach (var sec in AllSections())
            SaveSectionRows(sec, presetDto);

        presetDto.AutoAuxPortMap.Clear();
        presetDto.ComAuxPortMap.Clear();
        foreach (var row in _auxPortRows)
        {
            if (string.IsNullOrWhiteSpace(row.ApiName)) continue;
            if (!string.IsNullOrWhiteSpace(row.AutoAuxPort))
                presetDto.AutoAuxPortMap[row.ApiName] = row.AutoAuxPort;
            if (!string.IsNullOrWhiteSpace(row.ComAuxPort))
                presetDto.ComAuxPortMap[row.ApiName] = row.ComAuxPort;
        }

        // legacy Ports/FBTagMapTemplate 재생성 제거 — Direction 은 XGI_Template 가 단일 출처.
        FBTagMapStore.Save(_store, systemType, presetDto);
    }


    /// <summary>섹션 1개에 빈 행 추가 — 기본 패턴은 SignalSectionInfo 가 보유.</summary>
    private void AddSignalRow(SignalSectionInfo sec)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        sec.Rows.Add(HookAutoSave(new SignalPatternRow {
            ApiName = DefaultApiName(), Pattern = sec.DefaultPattern, TargetFBType = fb }));
        PersistCurrentPreset();
    }

    private void AddIwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[0]);
    private void AddQwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[1]);
    private void AddMwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[2]);

    private void RemoveIwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(IwSignalGrid, _iwSignalRows);
    private void RemoveQwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(QwSignalGrid, _qwSignalRows);
    private void RemoveMwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(MwSignalGrid, _mwSignalRows);

    /// <summary>
    /// 선택된 행 제거 후 가장 작은 삭제 인덱스 위치로 커서 이동.
    /// 삭제 버튼을 연속으로 눌러 여러 행을 순차 제거할 수 있게 한다.
    /// </summary>
    private void RemoveSelectedAndAdvance<T>(
        System.Windows.Controls.DataGrid grid,
        ObservableCollection<T> rows) where T : class
    {
        if (grid == null || rows == null || rows.Count == 0) return;
        var selected = grid.SelectedItems.Cast<T>().ToList();
        if (selected.Count == 0) return;

        // 삭제될 행들의 최소 인덱스를 기록 — 삭제 후 그 자리의 행이 새 선택.
        int anchor = selected.Min(r => rows.IndexOf(r));

        foreach (var row in selected)
            rows.Remove(row);

        // 행 제거 후 Preset 도 반영 — 💾 없이 자동 persist.
        PersistCurrentPreset();

        grid.SelectedItems.Clear();
        if (rows.Count == 0) return;

        int next = Math.Min(anchor, rows.Count - 1);
        if (next < 0) return;
        var target = rows[next];
        grid.SelectedItem = target;
        grid.CurrentItem = target;
        grid.ScrollIntoView(target);
        grid.Focus();
    }
}
