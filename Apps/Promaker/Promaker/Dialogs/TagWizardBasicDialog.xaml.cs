using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 기본 모드 TAG Wizard — 심볼 매크로와 Flow 선두주소만으로 ApiCall.InTag/OutTag 일괄 생성.
/// 신호 생성/적용은 BasicMacroIoGenerator + IoTagApplier 서비스로 위임.
/// </summary>
public partial class TagWizardBasicDialog : Window
{
    private readonly DsStore _store;
    private readonly ObservableCollection<FlowBaseBasicRow> _flowRows = new();
    private readonly ObservableCollection<IoBatchRow> _previewRows = new();

    public TagWizardBasicDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));

        FlowBaseGrid.ItemsSource = _flowRows;
        PreviewGrid.ItemsSource   = _previewRows;

        LoadFlows();
        RefreshPreview();

        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); RefreshPreview(); };
        IwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
        QwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
        MwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
    }

    private void LoadFlows()
    {
        _flowRows.Clear();
        var projects = Queries.allProjects(_store);
        if (projects.IsEmpty) return;

        var activeFlows = Queries.activeSystemsOf(projects.Head.Id, _store)
            .SelectMany(sys => Queries.flowsOf(sys.Id, _store))
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int i = 0;
        foreach (var name in activeFlows)
        {
            var baseAddr = (i * 1000).ToString();
            _flowRows.Add(new FlowBaseBasicRow
            {
                FlowName = name,
                IW_Base  = baseAddr,
                QW_Base  = baseAddr,
                MW_Base  = baseAddr,
            });
            i++;
        }

        StatusText.Text = activeFlows.Count > 0
            ? $"{activeFlows.Count}개 Flow 로드됨"
            : "Active 시스템 Flow 가 없습니다.";
    }

    private void ReloadFlows_Click(object sender, RoutedEventArgs e)
    {
        LoadFlows();
        RefreshPreview();
    }

    private readonly System.Windows.Threading.DispatcherTimer _previewDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void QueuePreviewRefresh()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        _previewRows.Clear();

        var bases = _flowRows
            .Select(r => new BasicMacroIoGenerator.FlowBase(
                r.FlowName,
                int.TryParse(r.IW_Base, out var iw) ? iw : 0,
                int.TryParse(r.QW_Base, out var qw) ? qw : 0,
                int.TryParse(r.MW_Base, out var mw) ? mw : 0))
            .ToList();

        var input = new BasicMacroIoGenerator.Input(
            IwMacro: IwMacroBox.Text ?? "",
            QwMacro: QwMacroBox.Text ?? "",
            MwMacro: MwMacroBox.Text ?? "",
            FlowBases: bases);

        foreach (var row in BasicMacroIoGenerator.Generate(_store, input))
            _previewRows.Add(row);

        StatusText.Text = $"미리보기 {_previewRows.Count}개 생성됨";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 미리보기 행이 없습니다.",
                "TAG Wizard (기본)",
                MessageBoxButton.OK, "ℹ");
            return;
        }

        var confirm = DialogHelpers.ShowThemedMessageBox(
            $"{_previewRows.Count}개 ApiCall 의 InTag/OutTag 에 덮어쓰기를 실행합니다.\n" +
            "기존 수동 설정값은 모두 새 매크로 결과로 교체됩니다.\n\n계속하시겠습니까?",
            "TAG Wizard (기본) — 적용 확인",
            MessageBoxButton.YesNo, "⚠");
        if (confirm != MessageBoxResult.Yes) return;

        var result = IoTagApplier.Apply(_store, _previewRows);

        DialogHelpers.ShowThemedMessageBox(
            $"✓ {result.SuccessCount}개 ApiCall 적용 완료" +
            (result.AnyFailed ? $" / {result.FailedCount}개 실패" : ""),
            "TAG Wizard (기본)",
            MessageBoxButton.OK, "✓");
        DialogResult = true;
        Close();
    }
}

public class FlowBaseBasicRow : INotifyPropertyChanged
{
    private string _flow = "", _iw = "", _qw = "", _mw = "";
    public string FlowName { get => _flow; set { _flow = value ?? ""; Notify(nameof(FlowName)); } }
    public string IW_Base  { get => _iw;   set { _iw   = value ?? ""; Notify(nameof(IW_Base));  } }
    public string QW_Base  { get => _qw;   set { _qw   = value ?? ""; Notify(nameof(QW_Base));  } }
    public string MW_Base  { get => _mw;   set { _mw   = value ?? ""; Notify(nameof(MW_Base));  } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
