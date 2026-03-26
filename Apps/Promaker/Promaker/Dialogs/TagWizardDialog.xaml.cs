using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Store;
using Ds2.IOList;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog : Window
{
    private readonly DsStore _store;
    private readonly IoListGeneratorService _generator;
    private readonly ObservableCollection<IoBatchRow> _ioRows;
    private readonly ObservableCollection<DummySignalRow> _dummyRows;
    private readonly ObservableCollection<UnmatchedSignalRow> _unmatchedRows;
    private ICollectionView _ioView;
    private int _currentStep = 1;
    private int _successCount;
    private string _currentTemplateFile = "";

    public TagWizardDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generator = new IoListGeneratorService();
        _ioRows = new ObservableCollection<IoBatchRow>();
        _dummyRows = new ObservableCollection<DummySignalRow>();
        _unmatchedRows = new ObservableCollection<UnmatchedSignalRow>();

        // Setup CollectionView with filtering
        _ioView = CollectionViewSource.GetDefaultView(_ioRows);
        _ioView.Filter = FilterIoRow;
        IoSignalPreviewGrid.ItemsSource = _ioView;

        DummySignalPreviewGrid.ItemsSource = _dummyRows;
        UnmatchedSignalGrid.ItemsSource = _unmatchedRows;

        // Bind filter text boxes
        PreviewFlowFilterBox.TextChanged += (_, _) => _ioView.Refresh();
        PreviewDeviceFilterBox.TextChanged += (_, _) => _ioView.Refresh();
        PreviewApiFilterBox.TextChanged += (_, _) => _ioView.Refresh();

        InitializeStep1();
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
        var flows = DsQuery.allFlows(_store);
        var works = flows.SelectMany(f => DsQuery.worksOf(f.Id, _store)).ToArray();
        var calls = works.SelectMany(w => DsQuery.callsOf(w.Id, _store)).ToArray();
        var allApiCalls = DsQuery.allApiCalls(_store);

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

        // Step 1
        if (_currentStep == 1)
        {
            Step1Bar.Background = accentBrush;
            Step1Text.Foreground = lightText;
            Step1Text.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            Step1Bar.Background = greenBrush;
            Step1Text.Foreground = lightText;
            Step1Text.FontWeight = FontWeights.Normal;
        }

        // Step 2
        if (_currentStep == 2)
        {
            Step2Bar.Background = accentBrush;
            Step2Text.Foreground = lightText;
            Step2Text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > 2)
        {
            Step2Bar.Background = greenBrush;
            Step2Text.Foreground = lightText;
            Step2Text.FontWeight = FontWeights.Normal;
        }
        else
        {
            Step2Bar.Background = secondaryBrush;
            Step2Text.Foreground = secondaryText;
            Step2Text.FontWeight = FontWeights.Normal;
        }

        // Step 3
        if (_currentStep == 3)
        {
            Step3Bar.Background = accentBrush;
            Step3Text.Foreground = lightText;
            Step3Text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > 3)
        {
            Step3Bar.Background = greenBrush;
            Step3Text.Foreground = lightText;
            Step3Text.FontWeight = FontWeights.Normal;
        }
        else
        {
            Step3Bar.Background = secondaryBrush;
            Step3Text.Foreground = secondaryText;
            Step3Text.FontWeight = FontWeights.Normal;
        }

        // Step 4
        if (_currentStep == 4)
        {
            Step4Bar.Background = accentBrush;
            Step4Text.Foreground = lightText;
            Step4Text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > 4)
        {
            Step4Bar.Background = greenBrush;
            Step4Text.Foreground = lightText;
            Step4Text.FontWeight = FontWeights.Normal;
        }
        else
        {
            Step4Bar.Background = secondaryBrush;
            Step4Text.Foreground = secondaryText;
            Step4Text.FontWeight = FontWeights.Normal;
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

    /// <summary>
    /// 신호 생성
    /// </summary>
    private bool GenerateSignals()
    {
        var templateDir = TemplateManager.TemplatesFolderPath;
        var configPath = TemplateManager.AddressConfigPath;

        if (!File.Exists(configPath))
        {
            DialogHelpers.ShowThemedMessageBox(
                "address_config.txt 파일을 찾을 수 없습니다.\n\n" +
                "템플릿 폴더를 열어서 설정 파일을 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            var result = _generator.Generate(_store, templateDir);

            if (!_generator.IsSuccess(result))
            {
                var errors = _generator.GetErrorSummary(result);
                DialogHelpers.ShowThemedMessageBox(
                    $"신호 생성 중 오류가 발생했습니다:\n\n{errors}",
                    "TAG Wizard - 오류",
                    MessageBoxButton.OK,
                    "✖");
                return false;
            }

            // IO 및 Dummy 신호 변환
            _ioRows.Clear();
            _dummyRows.Clear();

            var ioConvertedRows = ConvertSignalsToRows(result);
            var dummyConvertedRows = ConvertDummySignalsToRows(result);

            foreach (var row in ioConvertedRows)
            {
                _ioRows.Add(row);
            }

            foreach (var row in dummyConvertedRows)
            {
                _dummyRows.Add(row);
            }

            // 매칭 검증 및 분류
            ValidateAndClassifySignals();

            // 상태 메시지
            var unmatchedCount = _unmatchedRows.Count;
            if (unmatchedCount > 0)
            {
                GenerationStatusText.Text = $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개 생성 | ⚠ 매칭 실패 {unmatchedCount}개";
            }
            else
            {
                GenerationStatusText.Text = $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개가 생성되었습니다. 모든 신호가 매칭되었습니다.";
            }

            return true;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"신호 생성 중 예외가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "적용 ▶";
        }
    }

    /// <summary>
    /// 매칭 검증 및 분류
    /// </summary>
    private void ValidateAndClassifySignals()
    {
        _unmatchedRows.Clear();

        foreach (var row in _ioRows)
        {
            if (row.CallId == Guid.Empty || row.ApiCallId == Guid.Empty)
            {
                string reason = "";
                if (row.CallId == Guid.Empty && row.ApiCallId == Guid.Empty)
                {
                    reason = "Call 및 ApiCall을 찾을 수 없음";
                }
                else if (row.CallId == Guid.Empty)
                {
                    reason = "Call을 찾을 수 없음";
                }
                else if (row.ApiCallId == Guid.Empty)
                {
                    reason = "ApiCall(Device)을 찾을 수 없음";
                }

                _unmatchedRows.Add(new UnmatchedSignalRow(
                    Flow: row.Flow,
                    Device: row.Device,
                    Api: row.Api,
                    OutSymbol: row.OutSymbol,
                    OutAddress: row.OutAddress,
                    InSymbol: row.InSymbol,
                    InAddress: row.InAddress,
                    FailureReason: reason
                ));
            }
        }

        // 탭 표시 업데이트
        if (_unmatchedRows.Count > 0)
        {
            UnmatchedTabItem.Visibility = Visibility.Visible;
            UnmatchedCountText.Text = _unmatchedRows.Count.ToString();
        }
        else
        {
            UnmatchedTabItem.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 신호 적용
    /// </summary>
    private bool ApplySignals()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 IO 신호가 없습니다.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        var validRows = _ioRows.Where(r => r.ApiCallId != Guid.Empty && r.CallId != Guid.Empty).ToList();
        var unmatchedCount = _unmatchedRows.Count;

        if (unmatchedCount > 0)
        {
            var result = DialogHelpers.ShowThemedMessageBox(
                $"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않았습니다.\n\n" +
                $"'매칭 실패' 탭에서 상세 내역을 확인할 수 있습니다.\n\n" +
                $"✓ 매칭된 {validRows.Count}개 항목만 적용됩니다.\n\n" +
                $"계속하시겠습니까?",
                "TAG Wizard - 확인",
                MessageBoxButton.YesNo,
                "?");

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";

            _successCount = 0;
            var failedItems = new List<string>();

            foreach (var row in validRows)
            {
                try
                {
                    _store.UpdateApiCallIoTags(
                        row.CallId,
                        row.ApiCallId,
                        row.OutSymbol,
                        row.OutAddress,
                        row.InSymbol,
                        row.InAddress);

                    _successCount++;
                }
                catch (Exception ex)
                {
                    failedItems.Add($"{row.Flow}/{row.Device}/{row.Api}: {ex.Message}");
                }
            }

            // 완료 메시지 구성
            var summary = new StringBuilder();
            summary.AppendLine($"✅ {_successCount}개 ApiCall에 IO 태그가 성공적으로 적용되었습니다.");
            summary.AppendLine($"📊 IO 신호: {_ioRows.Count}개");
            summary.AppendLine($"📊 Dummy 신호: {_dummyRows.Count}개");

            if (failedItems.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"⚠️ {failedItems.Count}개 항목 적용 실패:");
                foreach (var item in failedItems.Take(3))
                {
                    summary.AppendLine($"  • {item}");
                }
                if (failedItems.Count > 3)
                {
                    summary.AppendLine($"  ... 외 {failedItems.Count - 3}개");
                }
            }

            CompletionSummaryText.Text = summary.ToString();

            return _successCount > 0;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// SignalRecord → IoBatchRow 변환
    /// </summary>
    private List<IoBatchRow> ConvertSignalsToRows(GenerationResult result)
    {
        var rows = new List<IoBatchRow>();

        // Group signals by Flow/Work/Call/Device
        var grouped = result.IoSignals
            .GroupBy(s => new { s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;

            // Find input and output signals (IoType starts with I/Q)
            var inputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase));
            var outputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase));

            // ApiCall 매칭
            var matchedCall = FindCallByName(_store, key.FlowName, key.WorkName, key.CallName);
            var callId = matchedCall?.Id ?? Guid.Empty;

            var apiCallId = Guid.Empty;
            string device = "UNKNOWN";
            string api = key.DeviceName;  // Initial value (DeviceName represents ApiDef.Name)

            if (matchedCall != null)
            {
                // DeviceName은 ApiDef.Name이므로 ApiDefId를 통해 매칭
                var matchedApiCall = matchedCall.ApiCalls
                    .FirstOrDefault(ac =>
                    {
                        // ApiDefId 확인
                        if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId))
                            return false;

                        var apiDefId = ac.ApiDefId.Value;

                        // ApiDef 조회
                        var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                        if (!FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                            return false;

                        var apiDef = apiDefOption.Value;

                        // ApiDef.Name과 DeviceName 비교
                        return apiDef.Name.Equals(key.DeviceName, StringComparison.OrdinalIgnoreCase);
                    });
                apiCallId = matchedApiCall?.Id ?? Guid.Empty;

                // Extract Device (System.Name) and Api (ApiDef.Name) from matched ApiCall
                if (matchedApiCall != null && FSharpOption<Guid>.get_IsSome(matchedApiCall.ApiDefId))
                {
                    var apiDefId = matchedApiCall.ApiDefId.Value;
                    var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                    if (FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                    {
                        var apiDef = apiDefOption.Value;
                        api = apiDef.Name;

                        // Get parent System name
                        if (_store.Systems.TryGetValue(apiDef.ParentId, out var system))
                        {
                            device = system.Name;
                        }
                    }
                }
            }

            rows.Add(new IoBatchRow(
                callId: callId,
                apiCallId: apiCallId,
                flow: key.FlowName,
                device: device,
                api: api,
                inAddress: inputSignal?.Address ?? "",
                inSymbol: inputSignal?.VarName ?? "",
                outAddress: outputSignal?.Address ?? "",
                outSymbol: outputSignal?.VarName ?? ""
            ));
        }

        return rows;
    }

    /// <summary>
    /// DummySignal → DummySignalRow 변환
    /// </summary>
    private List<DummySignalRow> ConvertDummySignalsToRows(GenerationResult result)
    {
        return result.DummySignals
            .Select(signal => new DummySignalRow(
                Flow: signal.FlowName,
                Work: signal.WorkName,
                Call: signal.CallName,
                Symbol: signal.VarName,
                Address: signal.Address,
                Type: signal.IoType
            ))
            .ToList();
    }

    /// <summary>
    /// Call 이름으로 검색
    /// </summary>
    private static Call? FindCallByName(DsStore store, string flowName, string workName, string callName)
    {
        var flows = DsQuery.allFlows(store);

        foreach (var flow in flows)
        {
            if (!flow.Name.Equals(flowName, StringComparison.OrdinalIgnoreCase))
                continue;

            var works = DsQuery.worksOf(flow.Id, store);
            foreach (var work in works)
            {
                if (!work.Name.Equals(workName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var calls = DsQuery.callsOf(work.Id, store);
                foreach (var call in calls)
                {
                    if (call.Name.Equals(callName, StringComparison.OrdinalIgnoreCase))
                        return call;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 템플릿 파일 목록 로드
    /// </summary>
    private void LoadTemplateFileList()
    {
        try
        {
            TemplateFilesListBox.Items.Clear();

            var templateDir = TemplateManager.TemplatesFolderPath;
            if (!Directory.Exists(templateDir))
            {
                TemplateStatusText.Text = "템플릿 폴더가 존재하지 않습니다.";
                return;
            }

            // 모든 .txt와 .cfg 파일 가져오기
            var files = Directory.GetFiles(templateDir, "*.*")
                .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                           f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
            {
                TemplateStatusText.Text = "템플릿 파일이 없습니다.";
                return;
            }

            foreach (var file in files)
            {
                TemplateFilesListBox.Items.Add(file);
            }

            // address_config.txt를 기본 선택
            var defaultFile = files.FirstOrDefault(f => f?.Equals("address_config.txt", StringComparison.OrdinalIgnoreCase) == true);
            if (defaultFile != null)
            {
                TemplateFilesListBox.SelectedItem = defaultFile;
            }
            else if (files.Count > 0)
            {
                TemplateFilesListBox.SelectedIndex = 0;
            }

            TemplateStatusText.Text = $"{files.Count}개의 템플릿 파일이 발견되었습니다.";
        }
        catch (Exception ex)
        {
            TemplateStatusText.Text = $"파일 목록 로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 파일 선택 시 내용 로드
    /// </summary>
    private void TemplateFilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TemplateFilesListBox.SelectedItem is string fileName)
        {
            LoadTemplateFile(fileName);
        }
    }

    /// <summary>
    /// 템플릿 파일 로드
    /// </summary>
    private void LoadTemplateFile(string fileName)
    {
        try
        {
            var filePath = Path.Combine(TemplateManager.TemplatesFolderPath, fileName);

            if (!File.Exists(filePath))
            {
                TemplateStatusText.Text = $"파일을 찾을 수 없습니다: {fileName}";
                TemplateEditBox.Text = "";
                TemplateEditBox.IsEnabled = false;
                return;
            }

            _currentTemplateFile = filePath;
            CurrentFileNameText.Text = fileName;
            TemplateEditBox.Text = File.ReadAllText(filePath, Encoding.UTF8);
            TemplateEditBox.IsEnabled = true;

            var fileInfo = new FileInfo(filePath);
            var sizeKb = fileInfo.Length / 1024.0;
            TemplateStatusText.Text = $"✓ 로드 완료 | 크기: {sizeKb:F1} KB | 마지막 수정: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"파일 로드 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            TemplateStatusText.Text = $"로드 실패: {ex.Message}";
            TemplateEditBox.IsEnabled = false;
        }
    }

    /// <summary>
    /// 템플릿 저장 버튼 클릭
    /// </summary>
    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentTemplateFile))
        {
            TemplateStatusText.Text = "저장할 파일을 선택하세요.";
            return;
        }

        try
        {
            File.WriteAllText(_currentTemplateFile, TemplateEditBox.Text, Encoding.UTF8);

            var fileInfo = new FileInfo(_currentTemplateFile);
            var sizeKb = fileInfo.Length / 1024.0;
            TemplateStatusText.Text = $"✓ 저장 완료 | 크기: {sizeKb:F1} KB | {DateTime.Now:HH:mm:ss}";

            DialogHelpers.ShowThemedMessageBox(
                $"'{Path.GetFileName(_currentTemplateFile)}' 파일이 저장되었습니다.",
                "저장 완료",
                MessageBoxButton.OK,
                "ℹ");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 저장 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            TemplateStatusText.Text = $"저장 실패: {ex.Message}";
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
