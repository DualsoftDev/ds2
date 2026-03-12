using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.Mermaid;

namespace Promaker.Dialogs;

public partial class MermaidImportDialog : Window
{
    private readonly MermaidGraph _graph;

    public MermaidImportDialog(
        MermaidGraph graph,
        DepthInfo depth,
        IReadOnlyList<ImportLevel> levels,
        ImportLevel defaultLevel,
        string targetName)
    {
        InitializeComponent();

        _graph = graph;
        SelectedLevel = defaultLevel;

        DepthInfoText.Text = depth.HasSubgraphs
            ? $"감지된 구조: 2-depth (subgraph {depth.SubgraphCount}개, 노드 {depth.TotalNodeCount}개)"
            : $"감지된 구조: 1-depth (노드 {depth.TotalNodeCount}개)";

        TargetText.Text = $"대상: {targetName}";

        foreach (var level in levels)
            LevelCombo.Items.Add(new ComboBoxItem { Content = LevelToString(level), Tag = level });

        var defaultIndex = levels.ToList().IndexOf(defaultLevel);
        LevelCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;

        Loaded += (_, _) => OkButton.Focus();
    }

    public ImportLevel SelectedLevel { get; private set; }

    private void LevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelCombo.SelectedItem is ComboBoxItem { Tag: ImportLevel level })
        {
            SelectedLevel = level;
            UpdatePreview(level);
        }
    }

    private void UpdatePreview(ImportLevel level)
    {
        var preview = MermaidMapper.buildPreview(_graph, level);

        var sb = new StringBuilder();
        var systemNames = preview.SystemNames.ToList();
        var deviceNames = preview.DeviceSystemNames.ToList();
        var flowNames = preview.FlowNames.ToList();
        var workNames = preview.WorkNames.ToList();
        var callNames = preview.CallNames.ToList();

        if (systemNames.Count > 0)
            sb.AppendLine($"System {systemNames.Count}개: {string.Join(", ", systemNames)}");
        if (deviceNames.Count > 0)
            sb.AppendLine($"Device System {deviceNames.Count}개: {string.Join(", ", deviceNames)}");
        if (flowNames.Count > 0)
            sb.AppendLine($"Flow {flowNames.Count}개: {string.Join(", ", flowNames)}");
        if (workNames.Count > 0)
            sb.AppendLine($"Work {workNames.Count}개: {string.Join(", ", workNames)}");
        if (callNames.Count > 0)
            sb.AppendLine($"Call {callNames.Count}개: {string.Join(", ", callNames)}");
        if (preview.ArrowWorksCount > 0)
            sb.AppendLine($"ArrowBetweenWorks {preview.ArrowWorksCount}개");
        if (preview.ArrowCallsCount > 0)
            sb.AppendLine($"ArrowBetweenCalls {preview.ArrowCallsCount}개");

        if (sb.Length == 0)
            sb.Append("생성할 항목 없음");

        PreviewText.Text = sb.ToString().TrimEnd();

        var warnings = new List<string>();
        foreach (var edge in preview.IgnoredEdges)
            warnings.Add($"무시: {edge.Item1} ({edge.Item2})");
        foreach (var w in preview.Warnings)
            warnings.Add(w);

        if (warnings.Count > 0)
        {
            WarningBorder.Visibility = Visibility.Visible;
            WarningText.Text = string.Join("\n", warnings);
        }
        else
        {
            WarningBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private static string LevelToString(ImportLevel level) =>
        level.IsSystemLevel ? "System 레벨 (Flow/Work 생성)"
        : level.IsFlowLevel ? "Flow 레벨 (Work/Call 생성)"
        : "Work 레벨 (Call 생성)";
}
