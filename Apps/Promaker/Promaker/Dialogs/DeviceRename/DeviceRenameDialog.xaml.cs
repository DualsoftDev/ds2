using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

/// <summary>
/// 디바이스(Passive System) 이름 + 그 디바이스 Action(API) 이름들을 한 번에 편집하는 다이얼로그.
/// 영향 미리보기(총 N건 + 대소문자 표류 경고 K건)는 P2 SSOT <see cref="DsStoreRenameDeviceExtensions"/>
/// 의 CollectRenameImpact 를 그대로 호출해 채운다(dry-run/apply 동일 소스). 실제 적용(RenameDeviceBatch)은
/// 호출 측(P4)이 NewDeviceNameOption / BuildApiRenames() 결과로 수행한다 — 본 다이얼로그는 미리보기만 담당.
/// 배치 다이얼로그 패턴은 DurationBatchDialog 차용(ObservableCollection + ChangedRows).
/// </summary>
public partial class DeviceRenameDialog : Window
{
    private readonly DsStore _store;
    private readonly Guid _systemId;
    private readonly string _originalDeviceName;
    private readonly ObservableCollection<DeviceApiRenameRow> _apiRows;

    public DeviceRenameDialog(
        DsStore store,
        Guid systemId,
        string deviceName,
        IReadOnlyList<ApiDefPanelItem> apiDefs)
    {
        _store = store;
        _systemId = systemId;
        _originalDeviceName = deviceName;

        InitializeComponent();

        HeaderText.Text = $"디바이스 일괄 이름 변경: {deviceName}";
        Title = $"디바이스 일괄 이름 변경: {deviceName}";
        DeviceNameBox.Text = deviceName;

        _apiRows = new ObservableCollection<DeviceApiRenameRow>(
            apiDefs.Select(d => new DeviceApiRenameRow(d.Id, d.Name)));

        foreach (var row in _apiRows)
            row.PropertyChanged += Row_PropertyChanged;

        ApiRenameGrid.ItemsSource = _apiRows;

        DeviceNameBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    /// <summary>변경된 Action 행(현재명 ≠ 새이름, 새이름 비어있지 않음)만.</summary>
    public IReadOnlyList<DeviceApiRenameRow> ChangedRows =>
        _apiRows.Where(r => r.IsChanged).ToList();

    /// <summary>디바이스명이 실제로 바뀌었는지(공백 trim 후 원본과 다름).</summary>
    public bool DeviceNameChanged =>
        !string.IsNullOrWhiteSpace(DeviceNameBox.Text)
        && DeviceNameBox.Text.Trim() != _originalDeviceName;

    /// <summary>F#(CollectRenameImpact/RenameDeviceBatch)에 넘길 디바이스명 인자.
    /// 변경 시 Some(trim), 아니면 None. preview(다이얼로그)와 apply(호출 측 P4)가 공유한다.</summary>
    public FSharpOption<string> NewDeviceNameOption =>
        DeviceNameChanged
            ? FSharpOption<string>.Some(DeviceNameBox.Text.Trim())
            : FSharpOption<string>.None;

    /// <summary>F#(CollectRenameImpact/RenameDeviceBatch)에 넘길 (apiDefId, newName) 목록.
    /// 변경된 행만, NewName 은 <c>.Trim()</c> 후 전달 — preview 가 trim 값으로 계산하므로 apply 도 동일해야
    /// preview↔apply 가 일치한다. CollectRenameImpact 는 oldName==newName 항목을 스스로 제외한다.</summary>
    public FSharpList<Tuple<Guid, string>> BuildApiRenames() =>
        ListModule.OfSeq(
            _apiRows
                .Where(r => r.IsChanged)
                .Select(r => Tuple.Create(r.ApiDefId, r.NewName.Trim())));

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceApiRenameRow.NewName))
            RefreshPreview();
    }

    /// <summary>현재 입력(디바이스명 + 변경 Action 행)으로 CollectRenameImpact 를 호출해 영향 미리보기 갱신.
    /// CollectRenameImpact 는 oldName==newName 항목을 스스로 제외하므로 변경 후보 전부를 넘겨도 안전.</summary>
    private void RefreshPreview()
    {
        var preview = _store.CollectRenameImpact(_systemId, NewDeviceNameOption, BuildApiRenames());

        ImpactSummaryText.Text = $"영향 미리보기 — Call/ApiCall 외 총 {preview.TotalCount}건";

        var driftCount = preview.DriftItems.Length;
        if (driftCount > 0)
        {
            DriftWarningText.Text =
                $"⚠ 대소문자 표류로 자동 정리되지 않는 항목 {driftCount}건";
            DriftWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            DriftWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// 디바이스 Action(API) 이름 변경 행 — 현재명(읽기전용) / 새이름(편집) / 변경 여부.
/// BatchRowBase 재활용(IsSelected/IsUnmatched/HasError + SetField/INotifyPropertyChanged).
/// </summary>
public sealed class DeviceApiRenameRow : BatchRowBase
{
    private string _newName;

    public DeviceApiRenameRow(Guid apiDefId, string currentName)
    {
        ApiDefId = apiDefId;
        CurrentName = currentName;
        _newName = currentName;
    }

    public Guid ApiDefId { get; }
    public string CurrentName { get; }

    public string NewName
    {
        get => _newName;
        set
        {
            if (SetField(ref _newName, value))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    /// <summary>새 이름이 비어있지 않고 현재명과 다르면 변경된 행.</summary>
    public bool IsChanged =>
        !string.IsNullOrWhiteSpace(NewName) && NewName.Trim() != CurrentName;
}
