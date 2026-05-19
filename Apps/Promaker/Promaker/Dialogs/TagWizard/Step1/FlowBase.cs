using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// Flow 주소 로드 — ACTIVE 시스템에 속한 Flow 만 대상.
    /// (Passive 시스템의 Flow 는 PLC 주소 설정 대상이 아님)
    /// 1000 단위 자동 할당 룰은 F# TagWizardBaseAddress 단일 source.
    /// </summary>
    private void LoadFlowBase()
    {
        try
        {
            _flowBaseRows.Clear();

            var projects = Queries.allProjects(_store);
            var flows = projects.IsEmpty
                ? new System.Collections.Generic.List<Flow>()
                : Queries.activeSystemsOf(projects.Head.Id, _store)
                    .SelectMany(sys => Queries.flowsOf(sys.Id, _store))
                    .ToList();
            var flowNames = flows
                .Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            var existingConfig = ReadFlowBaseOverrides();
            var assignments = TagWizardBaseAddress.AssignDefaultFlowBases(flowNames, existingConfig);

            foreach (var a in assignments)
                _flowBaseRows.Add(new FlowBaseRow
                {
                    FlowName = a.FlowName,
                    IW_Base = a.IwBase,
                    QW_Base = a.QwBase,
                    MW_Base = a.MwBase,
                });

            Step1Section.FlowBaseStatusText.Text = flowNames.Count > 0
                ? $"{flowNames.Count}개의 Flow를 찾았습니다."
                : "프로젝트에 Flow가 없습니다.";
        }
        catch (Exception ex)
        {
            Step1Section.FlowBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>Flow 별 BaseAddressOverride 를 ControlFlowProperties 에서 직접 읽어온다.
    /// 정수 파싱은 F# TagWizardBaseAddress.TryParseFirstNumeric 단일 source.</summary>
    private IReadOnlyDictionary<string, TagWizardBaseAddress.FlowBaseExisting> ReadFlowBaseOverrides()
    {
        var result = new Dictionary<string, TagWizardBaseAddress.FlowBaseExisting>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in _store.Flows.Values)
        {
            var cfpOpt = flow.GetControlProperties();
            if (!FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt)) continue;
            var ov = cfpOpt.Value.BaseAddressOverride;
            if (!FSharpOption<FBBaseAddressSet>.get_IsSome(ov)) continue;
            var ba = ov.Value;
            result[flow.Name] = new TagWizardBaseAddress.FlowBaseExisting(
                TagWizardBaseAddress.TryParseFirstNumeric(ba.InputBase),
                TagWizardBaseAddress.TryParseFirstNumeric(ba.OutputBase),
                TagWizardBaseAddress.TryParseFirstNumeric(ba.MemoryBase));
        }
        return result;
    }

    /// <summary>FlowBase 행을 ControlFlowProperties.BaseAddressOverride 에 직접 반영 — 텍스트 round-trip 불필요.</summary>
    internal void SaveFlowBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = _flowBaseRows.ToDictionary(r => r.FlowName, StringComparer.OrdinalIgnoreCase);
            foreach (var flow in _store.Flows.Values)
            {
                if (!rows.TryGetValue(flow.Name, out var row)) continue;
                if (string.IsNullOrWhiteSpace(row.IW_Base)
                    && string.IsNullOrWhiteSpace(row.QW_Base)
                    && string.IsNullOrWhiteSpace(row.MW_Base)) continue;

                var cfpOpt = flow.GetControlProperties();
                ControlFlowProperties cfp;
                if (FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt))
                    cfp = cfpOpt.Value;
                else
                {
                    cfp = new ControlFlowProperties();
                    flow.SetControlProperties(cfp);
                }
                var ba = new FBBaseAddressSet();
                if (int.TryParse(row.IW_Base, out _)) ba.InputBase  = row.IW_Base;
                if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                cfp.BaseAddressOverride = FSharpOption<FBBaseAddressSet>.Some(ba);
            }
            Step1Section.FlowBaseStatusText.Text = $"✓ 저장 완료 | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox("Flow 주소 설정이 저장되었습니다.", "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
            Step1Section.FlowBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }
}
