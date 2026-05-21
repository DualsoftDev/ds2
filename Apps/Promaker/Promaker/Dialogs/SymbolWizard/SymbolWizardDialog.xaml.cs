using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.SymbolImport;
using static Ds2.SymbolImport.CsvTypes;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using SymbolValidation = Ds2.SymbolImport.Validation;

namespace Promaker.Dialogs;

public partial class SymbolWizardDialog : Window
{
    /// <summary>Step 2 의 DataGrid row — mapping 결과 + 원본 entry 정보.</summary>
    public sealed class MappingRow
    {
        public string Address { get; init; } = "";
        public string Name { get; init; } = "";
        public string Direction { get; init; } = "";
        public string Flow { get; init; } = "";
        public string Work { get; init; } = "";
        public string Device { get; init; } = "";
        public string Api { get; init; } = "";
    }

    private readonly DsStore _store;
    private FSharpList<ModelGenerator.SystemPlan>? _pendingPlans;
    private Mapper.MappingBatch? _pendingBatch;

    public SymbolWizardDialog(DsStore store)
    {
        _store = store;
        InitializeComponent();
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "PLC 심볼 파일 선택",
            Filter = "All supported (*.csv;*.xml)|*.csv;*.xml|Mitsubishi CSV (*.csv)|*.csv|XG5000 XML (*.xml)|*.xml",
        };
        if (dlg.ShowDialog() == true)
            FilePathBox.Text = dlg.FileName;
    }

    private Vendor SelectedVendor() => (VendorCombo.SelectedIndex) switch
    {
        0 => Vendor.Mitsubishi,
        1 => Vendor.XG5000,
        _ => Vendor.AB,
    };

    private void Parse_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePathBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = "[Error] 파일을 먼저 선택하세요.";
            return;
        }

        var vendor = SelectedVendor();
        var parseResult = CsvParser.parseFile(vendor, path);
        var entries = parseResult.Entries.ToList();
        var batch = Mapper.map(parseResult.Entries);
        var plans = ModelGenerator.generate(batch);
        var issues = SymbolValidation.validate(batch, plans);

        _pendingBatch = batch;
        _pendingPlans = plans;

        // Mapping 의 OutputEntry 우선, 없으면 첫 InputEntry 로 row 1건 표시.
        // dsev2 매칭 결과는 Output/Input 페어 형태 — UI 는 *대표 1건* 만 보여줌.
        // (상세 페어 보려면 별도 Detail panel 또는 expand 필요 — 다음 단계.)
        var rows = batch.Mapped.Select(m =>
        {
            var rep = Microsoft.FSharp.Core.FSharpOption<CsvTypes.SymbolEntry>.get_IsSome(m.OutputEntry)
                ? m.OutputEntry.Value
                : m.InputEntries.FirstOrDefault();
            return new MappingRow
            {
                Address = rep?.Address ?? "",
                Name = rep?.Name ?? $"{m.DeviceName}.{m.ApiName}",
                Direction = rep is null ? "" : rep.Direction.ToString(),
                Flow = m.FlowName,
                Work = m.WorkName,
                Device = m.DeviceName,
                Api = m.ApiName,
            };
        }).ToList();
        MappingGrid.ItemsSource = rows;

        PreviewTree.Items.Clear();
        foreach (var plan in plans)
        {
            var sysNode = new TreeViewItem
            {
                Header = $"{(plan.IsActive ? "[Active]" : "[Passive]")} {plan.Name}",
                IsExpanded = true,
            };
            if (plan.IsActive)
            {
                foreach (var flow in plan.Flows)
                {
                    var flowNode = new TreeViewItem { Header = $"Flow: {flow.Name}", IsExpanded = true };
                    foreach (var work in flow.Works)
                    {
                        var workNode = new TreeViewItem { Header = $"Work: {work.Name}", IsExpanded = true };
                        foreach (var call in work.Calls)
                        {
                            var tags = new List<string>();
                            if (FSharpOption<IOTag>.get_IsSome(call.InTag))  tags.Add($"In={call.InTag.Value.Address}");
                            if (FSharpOption<IOTag>.get_IsSome(call.OutTag)) tags.Add($"Out={call.OutTag.Value.Address}");
                            workNode.Items.Add(new TreeViewItem
                            {
                                Header = $"Call: {call.Name}  ({string.Join(", ", tags)})",
                            });
                        }
                        flowNode.Items.Add(workNode);
                    }
                    sysNode.Items.Add(flowNode);
                }
            }
            else
            {
                foreach (var apiDef in plan.ApiDefs)
                {
                    sysNode.Items.Add(new TreeViewItem
                    {
                        Header = $"ApiDef: {apiDef.Name}  Action={apiDef.ActionType}  Sensing={apiDef.SensingType}",
                    });
                }
            }
            PreviewTree.Items.Add(sysNode);
        }

        IssuesBox.Text = string.Join("\n",
            parseResult.Warnings.Select(w => $"[Warning] {w}")
                .Concat(issues.Select(i => $"[{i.Severity}] {i.Code} — {i.Message}")));

        var mappedCount = batch.Mapped.Length;
        var unmatchedCount = batch.Unmatched.Length;
        StatusText.Text = $"파싱 완료 — {entries.Count} 심볼 / 매칭 {mappedCount} / 미매칭 {unmatchedCount}";

        ApplyButton.IsEnabled = mappedCount > 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingPlans is null)
            return;

        var msg = "모델 적용 후 되돌리려면 Undo 사용. 계속하시겠습니까?";
        if (MessageBox.Show(msg, "Symbol Import Wizard", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        // v10: SymbolImport 의 ModelGenerator plan 을 DsStore 에 단일 transaction 으로 적용.
        // 호출자(C#) 가 plan 의 모든 엔티티를 store mutation 으로 옮김.
        // 첫 Project 또는 신규 Project 안에 System/Flow/Work/Call/ApiDef 생성.
        var projectId = EnsureProject();
        ApplyPlansToStore(projectId, _pendingPlans);

        StatusText.Text = "모델 적용 완료.";
        DialogResult = true;
    }

    private Guid EnsureProject()
    {
        var existing = Queries.allProjects(_store);
        if (!existing.IsEmpty) return existing.Head.Id;
        return _store.AddProject("SymbolImported");
    }

    /// <summary>v10 spec: plan → DsStore mutation. ActionType / SensingType 은 plan 의 값 그대로.</summary>
    private void ApplyPlansToStore(Guid projectId, FSharpList<ModelGenerator.SystemPlan> plans)
    {
        // Device(passive) System 먼저 생성 — Controller 의 ApiCall 이 ApiDef 가리키므로 선행 필요.
        var deviceIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var apiDefIdByDeviceApi = new Dictionary<(string, string), Guid>();

        foreach (var plan in plans)
        {
            if (plan.IsActive) continue;
            // passive = isActive: false
            var systemId = _store.AddSystem(plan.Name, projectId, isActive: false);
            deviceIdByName[plan.Name] = systemId;
            foreach (var apiDef in plan.ApiDefs)
            {
                var apiDefId = _store.AddApiDefWithProperties(apiDef.Name, systemId);
                _store.UpdateApiDef(apiDefId, apiDef.Name,
                    apiDef.ActionType, apiDef.SensingType,
                    FSharpOption<Guid>.None, FSharpOption<Guid>.None, "");
                apiDefIdByDeviceApi[(plan.Name, apiDef.Name)] = apiDefId;
            }
        }

        // Controller(active) — Flow / Work / Call.
        foreach (var plan in plans)
        {
            if (!plan.IsActive) continue;
            var systemId = _store.AddSystem(plan.Name, projectId, isActive: true);
            foreach (var flow in plan.Flows)
            {
                var flowId = _store.AddFlow(flow.Name, systemId);
                foreach (var work in flow.Works)
                {
                    var workId = _store.AddWork(work.Name, flowId);
                    foreach (var call in work.Calls)
                    {
                        if (!apiDefIdByDeviceApi.TryGetValue((call.DeviceName, call.ApiName), out _))
                            continue;
                        _store.AddCallsWithDevice(projectId, workId,
                            new[] { call.Name }, true, FSharpOption<string>.None);
                    }
                }
            }
        }
    }

    private void OpenConfigEditor_Click(object sender, RoutedEventArgs e)
    {
        // input-matching-config.json GUI 편집. 저장 시 Ds2.SymbolImport.Matching 의
        // InputMatching / DeviceGroupingUtils 캐시 invalidate (FS module mutable state).
        var vm = new ConfigEditor.ConfigEditorViewModel();
        var window = new ConfigEditor.ConfigEditorWindow(vm) { Owner = this };
        window.ShowDialog();
    }
}
