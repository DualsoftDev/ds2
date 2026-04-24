using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Plc.Xgi;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 디바이스 타입(SystemType)별 FBTagMap 편집 다이얼로그.
/// 시스템 인스턴스별이 아니라 SystemType 전역 프리셋을 편집.
/// 프리셋은 첫 번째 ActiveSystem의 ControlSystemProperties에 저장되며 AASX에 포함된다.
/// </summary>
public partial class FBTagMapEditorDialog : Window
{
    private readonly string _deviceType;
    private readonly DsStore _store;

    public ObservableCollection<FBTagMapPortRow> Ports { get; } = new();

    private Microsoft.FSharp.Collections.FSharpMap<string, FBPortDefs> _fbPortDefs =
        Microsoft.FSharp.Collections.MapModule.Empty<string, FBPortDefs>();

    public FBTagMapEditorDialog(string deviceType, string xgiTemplatePath, DsStore store)
    {
        InitializeComponent();
        _deviceType = deviceType;
        _store      = store;

        Title = $"FBTagMap 편집 — {deviceType}";
        SystemNameLabel.Text = $"디바이스 타입: {deviceType}";

        // Load FB definitions from XGI_Template.xml
        _fbPortDefs = FBPortReader.readFromXml(xgiTemplatePath);
        var fbNames = _fbPortDefs.Keys.OrderBy(k => k).ToList();
        FBTypeComboBox.ItemsSource = fbNames;

        // Load existing settings from ActiveSystem ControlSystemProperties (AppData fallback)
        var all = FBTagMapStore.LoadAll(store);
        if (all.TryGetValue(deviceType, out var existing))
        {
            if (!string.IsNullOrEmpty(existing.FBTagMapName))
                FBTypeComboBox.SelectedItem = existing.FBTagMapName;

            foreach (var p in existing.Ports ?? new List<FBTagMapPortDto>())
                Ports.Add(new FBTagMapPortRow
                {
                    FBPort     = p.FBPort,
                    Direction  = p.Direction,
                    DataType   = p.DataType,
                    TagPattern = p.TagPattern,
                    IsDummy    = p.IsDummy,
                });
        }

        PortGrid.ItemsSource = Ports;
    }

    // ── FB 타입 선택 ──────────────────────────────────────────────────────────

    private void FBTypeComboBox_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Selection changed — user can still click "포트 자동 채우기" to apply
    }

    private void AutoFillPorts_Click(object sender, RoutedEventArgs e)
    {
        if (FBTypeComboBox.SelectedItem is not string fbType) return;
        if (!_fbPortDefs.ContainsKey(fbType)) return;

        var defs = _fbPortDefs[fbType];

        Ports.Clear();

        foreach (var port in defs.InputPorts)
            Ports.Add(new FBTagMapPortRow
            {
                FBPort     = port.Name,
                Direction  = "Input",
                DataType   = port.TypeName,
                TagPattern = $"$(F)_I_$(D)_{port.Name}",
                IsDummy    = false,
            });

        foreach (var port in defs.OutputPorts)
            Ports.Add(new FBTagMapPortRow
            {
                FBPort     = port.Name,
                Direction  = "Output",
                DataType   = port.TypeName,
                TagPattern = $"$(F)_Q_$(D)_{port.Name}",
                IsDummy    = false,
            });
    }

    // ── 행 추가/삭제 ──────────────────────────────────────────────────────────

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        Ports.Add(new FBTagMapPortRow());
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is FBTagMapPortRow row)
            Ports.Remove(row);
    }

    // ── OK ───────────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        PortGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, exitEditingMode: true);

        var preset = new FBTagMapPresetDto
        {
            FBTagMapName = FBTypeComboBox.SelectedItem as string ?? "",
            Ports = Ports.Select(row => new FBTagMapPortDto
            {
                FBPort     = row.FBPort,
                Direction  = row.Direction,
                DataType   = row.DataType,
                TagPattern = row.TagPattern,
                IsDummy    = row.IsDummy,
            }).ToList()
        };

        FBTagMapStore.Save(_store, _deviceType, preset);
        DialogResult = true;
    }
}

/// <summary>DataGrid 바인딩용 mutable row 모델</summary>
public class FBTagMapPortRow
{
    public string FBPort     { get; set; } = "";
    public string Direction  { get; set; } = "Input";
    public string DataType   { get; set; } = "BOOL";
    public string TagPattern { get; set; } = "";
    public bool   IsDummy    { get; set; } = false;
}
