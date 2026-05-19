using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// Flow 주소 로드 — ACTIVE 시스템에 속한 Flow 만 대상.
    /// (Passive 시스템의 Flow 는 PLC 주소 설정 대상이 아님)
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

            // 기존 flow_base.txt 파일에서 설정 읽기
            var existingConfig = ParseFlowBaseFile();

            // 각 Flow에 대해 행 생성 (1000 단위로 자동 할당)
            for (int i = 0; i < flowNames.Count; i++)
            {
                var flowName = flowNames[i];
                var row = new FlowBaseRow { FlowName = flowName };

                if (existingConfig.TryGetValue(flowName, out var config))
                {
                    // 기존 설정이 있으면 사용
                    row.IW_Base = config.IW_Base?.ToString() ?? "";
                    row.QW_Base = config.QW_Base?.ToString() ?? "";
                    row.MW_Base = config.MW_Base?.ToString() ?? "";
                }
                else
                {
                    // 기존 설정이 없으면 1000 단위로 자동 할당 (0, 1000, 2000, ...)
                    int baseAddress = i * 1000;
                    row.IW_Base = baseAddress.ToString();
                    row.QW_Base = baseAddress.ToString();
                    row.MW_Base = baseAddress.ToString();
                }

                _flowBaseRows.Add(row);
            }

            Step1Section.FlowBaseStatusText.Text = flowNames.Count > 0
                ? $"{flowNames.Count}개의 Flow를 찾았습니다."
                : "프로젝트에 Flow가 없습니다.";
        }
        catch (Exception ex)
        {
            Step1Section.FlowBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// Flow 별 BaseAddressOverride 를 ControlFlowProperties 에서 직접 읽어온다 (외부 파일 불필요).
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseFlowBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in _store.Flows.Values)
        {
            var cfpOpt = flow.GetControlProperties();
            if (!FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt)) continue;
            var ov = cfpOpt.Value.BaseAddressOverride;
            if (!FSharpOption<FBBaseAddressSet>.get_IsSome(ov)) continue;
            var ba = ov.Value;
            result[flow.Name] = (TryParseNum(ba.InputBase), TryParseNum(ba.OutputBase), TryParseNum(ba.MemoryBase));
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
