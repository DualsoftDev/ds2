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
    public string SourceDisplayName { get; private set; } = "PLC 심볼";

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
        CsvTypes.CsvParseResult parseResult;
        try
        {
            parseResult = CsvParser.parseFile(vendor, path);
        }
        catch (IOException ex)
        {
            HandleFileReadFailure(path, ex);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            HandleFileReadFailure(path, ex);
            return;
        }
        catch (Exception ex)
        {
            HandleSymbolProcessingFailure(
                "[Error] PLC 심볼 파일 파싱 실패.",
                $"PLC 심볼 파일 파싱 중 오류가 발생했습니다.\n\n{ex.Message}",
                "PLC 심볼 파싱 오류");
            return;
        }

        try
        {
            LoadParseResult(path, vendor, parseResult);
        }
        catch (Exception ex)
        {
            HandleSymbolProcessingFailure(
                "[Error] PLC 심볼 매칭 실패.",
                $"PLC 심볼 매칭/미리보기 생성 중 오류가 발생했습니다.\n\n{ex.Message}",
                "PLC 심볼 처리 오류");
        }
    }

    private void LoadParseResult(string path, Vendor vendor, CsvTypes.CsvParseResult parseResult)
    {
        SourceDisplayName = Path.GetFileName(path);
        var entries = parseResult.Entries.ToList();
        var batch = Mapper.map(vendor, parseResult.Entries);
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

    private void ResetParseState()
    {
        ApplyButton.IsEnabled = false;
        _pendingBatch = null;
        _pendingPlans = null;
        MappingGrid.ItemsSource = null;
        PreviewTree.Items.Clear();
        IssuesBox.Text = "";
    }

    private void HandleSymbolProcessingFailure(string statusText, string message, string title)
    {
        ResetParseState();
        StatusText.Text = statusText;
        DialogHelpers.Error(this, message, title);
    }

    private void HandleFileReadFailure(string path, Exception ex)
    {
        ResetParseState();
        StatusText.Text = "[Error] PLC 심볼 파일을 읽을 수 없습니다.";
        DialogHelpers.Warn(
            this,
            $"PLC 심볼 파일을 읽을 수 없습니다.\n\n파일이 다른 프로그램에서 독점적으로 열려 있거나 접근 권한이 없습니다.\n파일을 닫고 다시 시도하세요.\n\n파일: {path}\n상세: {ex.Message}",
            "PLC 심볼 파일 열기 실패");
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
        try
        {
            var projectId = EnsureProject();
            ApplyPlansToStore(_store, projectId, _pendingPlans);
        }
        catch (Exception ex)
        {
            StatusText.Text = "[Error] 모델 적용 실패.";
            DialogHelpers.Error(this, $"PLC 심볼 모델 적용 중 오류가 발생했습니다.\n\n{ex.Message}", "PLC 심볼 적용 오류");
            return;
        }

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
    internal static void ApplyPlansToStore(DsStore store, Guid projectId, FSharpList<ModelGenerator.SystemPlan> plans)
    {
        RemoveDefaultEmptyControlSystem(store, projectId);

        // Device(passive) System 먼저 생성 — Controller 의 ApiCall 이 ApiDef 가리키므로 선행 필요.
        var apiDefIdByDeviceApi = new Dictionary<(string, string), Guid>();

        foreach (var plan in plans)
        {
            if (plan.IsActive) continue;
            // passive = isActive: false
            var systemId = store.AddSystem(plan.Name, projectId, isActive: false);
            foreach (var apiDef in plan.ApiDefs)
            {
                var apiDefId = store.AddApiDefWithProperties(apiDef.Name, systemId);
                store.UpdateApiDef(apiDefId, apiDef.Name,
                    apiDef.ActionType, apiDef.SensingType,
                    FSharpOption<Guid>.None, FSharpOption<Guid>.None, "");
                apiDefIdByDeviceApi[(plan.Name, apiDef.Name)] = apiDefId;
            }
        }

        // Controller(active) — Flow / Work / Call.
        foreach (var plan in plans)
        {
            if (!plan.IsActive) continue;
            var systemId = store.AddSystem(plan.Name, projectId, isActive: true);
            foreach (var flow in plan.Flows)
            {
                var flowId = store.AddFlow(flow.Name, systemId);
                foreach (var work in flow.Works)
                {
                    var workId = store.AddWork(work.Name, flowId);
                    // Wizard entries — (callName, outTag, inTag). null IOTag 는 F# 에서 None 으로 저장.
                    // AddCallsWithDeviceAndTags 가 1 transaction 안에서 Call + ApiCall + OutTag/InTag 까지 채움.
                    var entries = new List<(string Name, IOTag OutTag, IOTag InTag)>();
                    foreach (var call in work.Calls)
                    {
                        if (!apiDefIdByDeviceApi.TryGetValue((call.DeviceName, call.ApiName), out _))
                            continue;
                        var outTag = FSharpOption<IOTag>.get_IsSome(call.OutTag) ? call.OutTag.Value : null!;
                        var inTag  = FSharpOption<IOTag>.get_IsSome(call.InTag)  ? call.InTag.Value  : null!;
                        entries.Add((call.Name, outTag, inTag));
                    }
                    if (entries.Count == 0) continue;
                    store.AddCallsWithDeviceAndTags(projectId, workId, entries, true, FSharpOption<string>.None);
                }
            }
        }
    }

    private static void RemoveDefaultEmptyControlSystem(DsStore store, Guid projectId)
    {
        var activeSystems = Queries.activeSystemsOf(projectId, store).ToList();
        foreach (var system in activeSystems)
        {
            if (!string.Equals(system.Name, "NewSystem", StringComparison.Ordinal))
                continue;

            var flows = Queries.flowsOf(system.Id, store).ToList();
            var apiDefs = Queries.apiDefsOf(system.Id, store).ToList();
            if (apiDefs.Count != 0)
                continue;

            var hasOnlyEmptyNewFlow =
                flows.Count == 0
                || (flows.Count == 1
                    && string.Equals(flows[0].Name, "NewFlow", StringComparison.Ordinal)
                    && !Queries.worksOf(flows[0].Id, store).Any());

            if (hasOnlyEmptyNewFlow)
                store.RemoveEntities(new[] { Tuple.Create(EntityKind.System, system.Id) });
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
