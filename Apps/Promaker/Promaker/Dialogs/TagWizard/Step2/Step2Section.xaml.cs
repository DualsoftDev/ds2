using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Promaker.Dialogs;

/// <summary>
/// TagWizard Step 2 — 신호 템플릿 편집 (SystemType preset / IW·QW·MW signal patterns / AUX·End ports).
/// Click/SelectionChanged handler 는 ancestor <see cref="TagWizardDialog"/> 로 routing.
/// 실 logic 은 dialog partial files DeviceTemplate.cs / Step4Mapping.cs / Dialog.xaml.cs 에 그대로.
/// </summary>
public partial class TagWizardStep2Section : UserControl
{
    public TagWizardStep2Section()
    {
        InitializeComponent();
    }

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    // ── 자식 Tab UserControl 의 named element forwarding (dialog 의 기존 사용처 보존용) ──
    internal DataGrid IwSignalGrid => IwTab.IwSignalGrid;
    internal FrameworkElement IwChunkedView => IwTab.IwChunkedView;
    internal CheckBox IwChunkedToggle => IwTab.IwChunkedToggle;

    internal DataGrid QwSignalGrid => QwTab.QwSignalGrid;
    internal FrameworkElement QwChunkedView => QwTab.QwChunkedView;
    internal CheckBox QwChunkedToggle => QwTab.QwChunkedToggle;

    internal DataGrid MwSignalGrid => MwTab.MwSignalGrid;
    internal FrameworkElement MwChunkedView => MwTab.MwChunkedView;
    internal CheckBox MwChunkedToggle => MwTab.MwChunkedToggle;

    internal DataGrid AuxPortGrid => AuxTab.AuxPortGrid;
    internal System.Windows.Controls.Primitives.ToggleButton AuxFilterInput => AuxTab.AuxFilterInput;
    internal System.Windows.Controls.Primitives.ToggleButton AuxFilterOutput => AuxTab.AuxFilterOutput;
    internal System.Windows.Controls.Primitives.ToggleButton AuxFilterAll => AuxTab.AuxFilterAll;

    internal DataGrid EndPortGrid => EndTab.EndPortGrid;

    // DeviceTemplate 사이드 (12)
    private void AddIwRow_Click(object s, RoutedEventArgs e) => Host?.AddIwRow_Click(s, e);
    private void AddQwRow_Click(object s, RoutedEventArgs e) => Host?.AddQwRow_Click(s, e);
    private void AddMwRow_Click(object s, RoutedEventArgs e) => Host?.AddMwRow_Click(s, e);
    private void RemoveIwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveIwRow_Click(s, e);
    private void RemoveQwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveQwRow_Click(s, e);
    private void RemoveMwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveMwRow_Click(s, e);
    private void MoveIwUp_Click(object s, RoutedEventArgs e) => Host?.MoveIwUp_Click(s, e);
    private void MoveIwDown_Click(object s, RoutedEventArgs e) => Host?.MoveIwDown_Click(s, e);
    private void MoveQwUp_Click(object s, RoutedEventArgs e) => Host?.MoveQwUp_Click(s, e);
    private void MoveQwDown_Click(object s, RoutedEventArgs e) => Host?.MoveQwDown_Click(s, e);
    private void MoveMwUp_Click(object s, RoutedEventArgs e) => Host?.MoveMwUp_Click(s, e);
    private void MoveMwDown_Click(object s, RoutedEventArgs e) => Host?.MoveMwDown_Click(s, e);

    private void DeviceTemplateListBox_SelectionChanged(object s, SelectionChangedEventArgs e)
        => Host?.DeviceTemplateListBox_SelectionChanged(s, e);

    // Step 4 Mapping (Aux/End/SignalSection)
    private void EditPreFbCondition_Click(object s, RoutedEventArgs e) => Host?.EditPreFbCondition_Click(s, e);
    private void EditAuxPortCondition_Click(object s, RoutedEventArgs e) => Host?.EditAuxPortCondition_Click(s, e);
    private void AuxRowSelector_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
        => Host?.AuxRowSelector_PreviewMouseLeftButtonDown(s, e);
    private void SignalSectionTab_SelectionChanged(object s, SelectionChangedEventArgs e)
        => Host?.SignalSectionTab_SelectionChanged(s, e);
    private void AddAuxPortRow_Click(object s, RoutedEventArgs e) => Host?.AddAuxPortRow_Click(s, e);
    private void RemoveAuxPortRow_Click(object s, RoutedEventArgs e) => Host?.RemoveAuxPortRow_Click(s, e);
    private void MoveAuxRowUp_Click(object s, RoutedEventArgs e) => Host?.MoveAuxRowUp_Click(s, e);
    private void MoveAuxRowDown_Click(object s, RoutedEventArgs e) => Host?.MoveAuxRowDown_Click(s, e);

    // Dialog.xaml.cs 측 (Aux filter / GlobalFB / EndPort / Chunked toggle)
    private void AuxFilter_Changed(object s, RoutedEventArgs e) => Host?.AuxFilter_Changed(s, e);
    private void GlobalFBType_Changed(object s, SelectionChangedEventArgs e) => Host?.GlobalFBType_Changed(s, e);
    private void AddEndPortRow_Click(object s, RoutedEventArgs e) => Host?.AddEndPortRow_Click(s, e);
    private void RemoveEndPortRow_Click(object s, RoutedEventArgs e) => Host?.RemoveEndPortRow_Click(s, e);
    private void ChunkedToggle_Changed(object s, RoutedEventArgs e) => Host?.ChunkedToggle_Changed(s, e);
}
