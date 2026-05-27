using System.Linq;
using System.Windows;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class CallMoveAcrossFlowsDialog : Window
{
    public CrossFlowDeviceMode? SelectedMode { get; private set; }

    public CallMoveAcrossFlowsDialog(CrossFlowDeviceModePromptContext context)
    {
        InitializeComponent();
        var verb = context.IsCut ? "이동" : "복사";
        HeaderText.Text = $"{context.CallCount}개 Call 을 다른 Flow 로 {verb}";

        // Prefix 교체 모드 사전 가능성 검사 — 공유 device / target name taken 충돌 있으면 라디오 disable.
        // 사용자가 선택조차 못 하게 → 확인 후 차단 UX 회피.
        var conflicts = context.Store.GetRenameSourceSystemConflicts(context.SourceCallIds, context.TargetWorkId);
        if (conflicts.Any())
        {
            var sharedAliases = conflicts
                .Where(c => c.Item2.IsSharedWithOtherCalls)
                .Select(c => c.Item1)
                .Distinct()
                .ToArray();
            var nameTakenAliases = conflicts
                .Where(c => c.Item2.IsNameTaken)
                .Select(c => c.Item1)
                .Distinct()
                .ToArray();

            RadioRename.IsEnabled = false;
            var reasons = new System.Collections.Generic.List<string>();
            if (sharedAliases.Length > 0)
                reasons.Add($"공유 device: {string.Join(", ", sharedAliases)} (다른 Flow Call 도 참조 중)");
            if (nameTakenAliases.Length > 0)
                reasons.Add($"대상 이름 이미 존재: {string.Join(", ", nameTakenAliases)}");
            RadioRename.ToolTip = "사용 불가 — " + string.Join(" / ", reasons) + ". 'Device System 복사' 를 사용하세요.";
        }
    }

    private void OnOk_Click(object sender, RoutedEventArgs e)
    {
        if (RadioClone.IsChecked == true) SelectedMode = CrossFlowDeviceMode.CloneSystem;
        else if (RadioRename.IsChecked == true) SelectedMode = CrossFlowDeviceMode.RenameSourceSystem;
        else if (RadioKeep.IsChecked == true) SelectedMode = CrossFlowDeviceMode.KeepReferences;
        else SelectedMode = CrossFlowDeviceMode.CloneSystem;
        DialogResult = true;
    }
}
