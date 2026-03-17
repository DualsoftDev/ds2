using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Mermaid;
using Ds2.UI.Core;
using Microsoft.Win32;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private const string MermaidFileFilter =
        "Mermaid Files (*.md;*.mmd)|*.md;*.mmd|All Files (*.*)|*.*";

    [RelayCommand]
    private void ImportMermaid()
    {
        var node = SelectedNode;
        if (node is null) return;

        var kind = node.EntityType;
        if (!EntityKindRules.canImportMermaid(kind))
            return;

        // 1. 파일 선택
        var dlg = new OpenFileDialog { Filter = MermaidFileFilter };
        if (dlg.ShowDialog() != true) return;

        // 2. 파싱
        var parseResult = MermaidImporter.parseFile(dlg.FileName);
        if (parseResult.IsError)
        {
            DialogHelpers.Warn($"Mermaid 파싱 실패:\n{string.Join("\n", parseResult.ErrorValue)}");
            return;
        }
        var graph = parseResult.ResultValue;

        // 3. depth 분석 + 가능 레벨
        var depth = MermaidAnalyzer.analyzeDepth(graph);
        var allLevels = MermaidAnalyzer.availableLevels(depth);

        // 선택 노드에 맞는 기본 레벨
        var defaultLevel = kind switch
        {
            EntityKind.Flow => ImportLevel.FlowLevel,
            EntityKind.Work => ImportLevel.WorkLevel,
            _ => allLevels.First()
        };

        // 기본 레벨이 가능 목록에 없으면 첫 번째로 대체
        if (!allLevels.Contains(defaultLevel))
            defaultLevel = allLevels.First();

        // 4. 프리뷰 다이얼로그
        var preview = new MermaidImportDialog(
            graph, depth, allLevels, defaultLevel, node.Name);

        if (!DialogHelpers.ShowOwnedDialog(preview))
            return;

        var selectedLevel = preview.SelectedLevel;

        // 5. 임포트 실행
        TryRunFileOperation(
            $"Mermaid import into '{node.Name}'",
            () =>
            {
                var result = MermaidImporter.importIntoStore(_store, graph, selectedLevel, node.Id);
                if (result.IsError)
                {
                    DialogHelpers.Warn($"임포트 실패:\n{string.Join("\n", result.ErrorValue)}");
                    return;
                }

                StatusText = $"Mermaid 임포트 완료 ({node.Name})";
                RequestRebuildAll();
            },
            ex => $"Mermaid 임포트 실패: {ex.Message}");
    }
}
