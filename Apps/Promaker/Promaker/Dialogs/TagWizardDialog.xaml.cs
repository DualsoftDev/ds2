using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog : Window
{
    private readonly DsStore _store;
    private readonly IoListGeneratorService _generator;
    private readonly ObservableCollection<IoBatchRow> _ioRows;
    private readonly ObservableCollection<DummySignalRow> _dummyRows;
    private readonly ObservableCollection<UnmatchedSignalRow> _unmatchedRows;
    private readonly ObservableCollection<SystemBaseRow> _systemBaseRows;
    private readonly ObservableCollection<FlowBaseRow> _flowBaseRows;
    private readonly ObservableCollection<SignalPatternRow> _iwSignalRows;
    private readonly ObservableCollection<SignalPatternRow> _qwSignalRows;
    private readonly ObservableCollection<SignalPatternRow> _mwSignalRows;
    private readonly ObservableCollection<ErrorDisplayItem> _errorItems;
    private ICollectionView _ioView;
    private int _currentStep = 1;
    private int _successCount;
    private string _currentDeviceTemplateFile = "";

    public TagWizardDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generator = new IoListGeneratorService();
        _ioRows = new ObservableCollection<IoBatchRow>();
        _dummyRows = new ObservableCollection<DummySignalRow>();
        _unmatchedRows = new ObservableCollection<UnmatchedSignalRow>();
        _systemBaseRows = new ObservableCollection<SystemBaseRow>();
        _flowBaseRows = new ObservableCollection<FlowBaseRow>();
        _iwSignalRows = new ObservableCollection<SignalPatternRow>();
        _qwSignalRows = new ObservableCollection<SignalPatternRow>();
        _mwSignalRows = new ObservableCollection<SignalPatternRow>();
        _errorItems = new ObservableCollection<ErrorDisplayItem>();

        // Setup CollectionView with filtering
        _ioView = CollectionViewSource.GetDefaultView(_ioRows);
        _ioView.Filter = FilterIoRow;
        IoSignalPreviewGrid.ItemsSource = _ioView;

        DummySignalPreviewGrid.ItemsSource = _dummyRows;
        UnmatchedSignalGrid.ItemsSource = _unmatchedRows;
        ErrorsListBox.ItemsSource = _errorItems;

        // Bind DataGrids
        SystemBaseGrid.ItemsSource = _systemBaseRows;
        FlowBaseGrid.ItemsSource = _flowBaseRows;
        IwSignalGrid.ItemsSource = _iwSignalRows;
        QwSignalGrid.ItemsSource = _qwSignalRows;
        MwSignalGrid.ItemsSource = _mwSignalRows;

        // Bind filter text boxes
        PreviewFlowFilterBox.TextChanged += (_, _) => _ioView.Refresh();
        PreviewDeviceFilterBox.TextChanged += (_, _) => _ioView.Refresh();
        PreviewApiFilterBox.TextChanged += (_, _) => _ioView.Refresh();

        InitializeStep1();
    }

    internal ControlSystemProperties? GetOrCreateControlProps()
    {
        var projects = Queries.allProjects(_store);
        if (projects.IsEmpty) return null;
        var activeSystems = Queries.activeSystemsOf(projects.Head.Id, _store);
        if (activeSystems.IsEmpty) return null;
        var sys = activeSystems.Head;
        var ctrlOpt = sys.GetControlProperties();
        if (!FSharpOption<ControlSystemProperties>.get_IsSome(ctrlOpt))
        {
            var cp = new ControlSystemProperties();
            sys.SetControlProperties(cp);
            return cp;
        }
        return ctrlOpt.Value;
    }

    private bool FilterIoRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        var flow = PreviewFlowFilterBox.Text;
        var device = PreviewDeviceFilterBox.Text;
        var api = PreviewApiFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Flow.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(device) && !row.Device.Contains(device, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(api) && !row.Api.Contains(api, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Step 1 초기화
    /// </summary>
    private void InitializeStep1()
    {
        // 프로젝트 통계 조회
        var flows = Queries.allFlows(_store);
        var works = flows.SelectMany(f => Queries.worksOf(f.Id, _store)).ToArray();
        var calls = works.SelectMany(w => Queries.callsOf(w.Id, _store)).ToArray();
        var allApiCalls = Queries.allApiCalls(_store);

        FlowCountText.Text = $"{flows.Length}개";
        WorkCountText.Text = $"{works.Length}개";
        CallCountText.Text = $"{calls.Length}개";
        DeviceCountText.Text = $"{allApiCalls.Length}개";

        // 템플릿 경로
        TemplatePathText.Text = TemplateManager.TemplatesFolderPath;
    }

    /// <summary>
    /// 템플릿 폴더 열기
    /// </summary>
    private void OpenTemplateFolder_Click(object sender, RoutedEventArgs e)
    {
        TemplateManager.OpenTemplatesFolder();
    }

    /// <summary>
    /// 다음 버튼 클릭
    /// </summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            // Step 1 → Step 2: 템플릿 편집
            LoadTemplateFileList();
            MoveToStep(2);
        }
        else if (_currentStep == 2)
        {
            // Step 2 → Step 3: 신호 생성
            if (!GenerateSignals())
                return;

            MoveToStep(3);
        }
        else if (_currentStep == 3)
        {
            // Step 3 → Step 4: 신호 적용
            if (!ApplySignals())
                return;

            MoveToStep(4);
        }
    }

    /// <summary>
    /// 이전 버튼 클릭
    /// </summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            MoveToStep(_currentStep - 1);
        }
    }

    /// <summary>
    /// 닫기 버튼 클릭
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 단계 이동
    /// </summary>
    private void MoveToStep(int step)
    {
        _currentStep = step;

        // 컨텐츠 표시
        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Content.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        // 단계 인디케이터 업데이트
        UpdateStepIndicators();

        // 버튼 상태 업데이트
        UpdateButtons();
    }

    /// <summary>
    /// 단계 인디케이터 업데이트
    /// </summary>
    private void UpdateStepIndicators()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var greenBrush = (Brush)FindResource("GreenAccentBrush");
        var secondaryBrush = (Brush)FindResource("SecondaryBackgroundBrush");
        var lightText = (Brush)FindResource("AlwaysLightTextBrush");
        var secondaryText = (Brush)FindResource("SecondaryTextBrush");

        ApplyStepStyle(Step1Bar, Step1Text, 1, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step2Bar, Step2Text, 2, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step3Bar, Step3Text, 3, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step4Bar, Step4Text, 4, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
    }

    /// <summary>
    /// 개별 스텝 인디케이터 스타일 적용
    /// </summary>
    private void ApplyStepStyle(
        Border bar, TextBlock text, int stepNumber,
        Brush accentBrush, Brush greenBrush, Brush secondaryBrush,
        Brush lightText, Brush secondaryText)
    {
        if (_currentStep == stepNumber)
        {
            bar.Background = accentBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > stepNumber)
        {
            bar.Background = greenBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.Normal;
        }
        else
        {
            bar.Background = secondaryBrush;
            text.Foreground = secondaryText;
            text.FontWeight = FontWeights.Normal;
        }
    }

    /// <summary>
    /// 버튼 상태 업데이트
    /// </summary>
    private void UpdateButtons()
    {
        // 이전 버튼
        BackButton.Visibility = _currentStep > 1 && _currentStep < 4 ? Visibility.Visible : Visibility.Collapsed;

        // 다음 버튼
        if (_currentStep < 4)
        {
            NextButton.Visibility = Visibility.Visible;
            if (_currentStep == 1)
                NextButton.Content = "다음 ▶";
            else if (_currentStep == 2)
                NextButton.Content = "신호 생성 ▶";
            else if (_currentStep == 3)
                NextButton.Content = "적용 ▶";
            CloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            NextButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }
}

/// <summary>
/// Dummy 신호 행 (Preview용)
/// </summary>
public record DummySignalRow(
    string Flow,
    string Work,
    string Call,
    string Symbol,
    string Address,
    string Type
);

/// <summary>
/// 매칭 실패 신호 행
/// </summary>
public record UnmatchedSignalRow(
    string Flow,
    string Device,
    string Api,
    string OutSymbol,
    string OutAddress,
    string InSymbol,
    string InAddress,
    string FailureReason
);

/// <summary>
/// 시스템 주소 설정 행 (DataGrid 바인딩용)
/// </summary>
public class SystemBaseRow : INotifyPropertyChanged
{
    private string _systemType = "";
    private bool _isEnabled = false;
    private string _iwBase = "";
    private string _qwBase = "";
    private string _mwBase = "";

    public string SystemType
    {
        get => _systemType;
        set { _systemType = value; OnPropertyChanged(nameof(SystemType)); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
    }

    public string IW_Base
    {
        get => _iwBase;
        set { _iwBase = value; OnPropertyChanged(nameof(IW_Base)); }
    }

    public string QW_Base
    {
        get => _qwBase;
        set { _qwBase = value; OnPropertyChanged(nameof(QW_Base)); }
    }

    public string MW_Base
    {
        get => _mwBase;
        set { _mwBase = value; OnPropertyChanged(nameof(MW_Base)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Flow 주소 설정 행 (DataGrid 바인딩용)
/// </summary>
public class FlowBaseRow : INotifyPropertyChanged
{
    private string _flowName = "";
    private string _iwBase = "";
    private string _qwBase = "";
    private string _mwBase = "";

    public string FlowName
    {
        get => _flowName;
        set { _flowName = value; OnPropertyChanged(nameof(FlowName)); }
    }

    public string IW_Base
    {
        get => _iwBase;
        set { _iwBase = value; OnPropertyChanged(nameof(IW_Base)); }
    }

    public string QW_Base
    {
        get => _qwBase;
        set { _qwBase = value; OnPropertyChanged(nameof(QW_Base)); }
    }

    public string MW_Base
    {
        get => _mwBase;
        set { _mwBase = value; OnPropertyChanged(nameof(MW_Base)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 신호 패턴 행 (IW/QW/MW 그리드 바인딩용)
/// </summary>
public class SignalPatternRow : INotifyPropertyChanged
{
    private string _apiName = "";
    private string _pattern = "";

    public string ApiName
    {
        get => _apiName;
        set { _apiName = value; OnPropertyChanged(nameof(ApiName)); }
    }

    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; OnPropertyChanged(nameof(Pattern)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 오류 표시 항목 (ListBox 바인딩용)
/// </summary>
public class ErrorDisplayItem
{
    public string ErrorType { get; set; } = "";
    public string Message { get; set; } = "";
}
