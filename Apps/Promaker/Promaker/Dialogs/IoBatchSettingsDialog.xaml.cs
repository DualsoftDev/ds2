using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Ds2.Store;
using Ds2.IOList;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog : Window
{
    private readonly DsStore _store;
    private readonly ObservableCollection<IoBatchRow> _rows;
    private readonly ICollectionView _view;
    private readonly string? _currentFilePath;

    public IoBatchSettingsDialog(DsStore store, IReadOnlyList<IoBatchRow> rows, string? currentFilePath = null)
    {
        InitializeComponent();

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rows = new ObservableCollection<IoBatchRow>(rows);
        _currentFilePath = currentFilePath;

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        FlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        DeviceFilterBox.TextChanged += (_, _) => _view.Refresh();
        ApiFilterBox.TextChanged += (_, _) => _view.Refresh();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        var flow = FlowFilterBox.Text;
        var device = DeviceFilterBox.Text;
        var api = ApiFilterBox.Text;

        if (!string.IsNullOrEmpty(flow) && !row.Flow.Contains(flow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(device) && !row.Device.Contains(device, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(api) && !row.Api.Contains(api, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public IReadOnlyList<IoBatchRow> ChangedRows =>
        _rows.Where(r => r.IsChanged).ToList();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IoBatchRow.IsSelected))
            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
    }

    private void GenerateOutTags_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = _rows.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("먼저 하나 이상의 행을 선택하세요.", "태그 자동 생성", MessageBoxButton.OK, "ℹ");
            return;
        }

        var pattern = OutTagPatternBox.Text;
        var prefix = OutAddressPrefixBox.Text;

        if (!int.TryParse(OutAddressStartBox.Text, out int startAddr))
        {
            DialogHelpers.ShowThemedMessageBox("시작 주소는 숫자여야 합니다.", "태그 자동 생성", MessageBoxButton.OK, "⚠");
            return;
        }

        int currentWord = startAddr;
        int currentBit = 0;

        foreach (var row in selectedRows)
        {
            // Out Tag 생성 (매크로 치환: $(F), $(D), $(A)만 사용)
            string outTag = pattern
                .Replace("$(F)", row.Flow)
                .Replace("$(D)", row.Device)
                .Replace("$(A)", row.Api);

            row.OutSymbol = outTag;

            // Out Address 생성 (DataType에 따라 주소 증가)
            if (row.OutDataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            {
                // BOOL: 비트 주소 사용 (.0, .1, ... .15)
                row.OutAddress = $"{prefix}{currentWord}.{currentBit}";
                currentBit++;
                if (currentBit >= 16)
                {
                    currentBit = 0;
                    currentWord++;
                }
            }
            else
            {
                // INT, DINT 등: 워드 단위 증가
                row.OutAddress = $"{prefix}{currentWord}";
                currentWord++;
                currentBit = 0;  // 워드가 증가하면 비트 초기화
            }
        }

        DialogHelpers.ShowThemedMessageBox(
            $"{selectedRows.Count}개 행에 Out 태그가 생성되었습니다.",
            "태그 자동 생성",
            MessageBoxButton.OK,
            "✓");
    }

    private void GenerateInTags_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = _rows.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("먼저 하나 이상의 행을 선택하세요.", "태그 자동 생성", MessageBoxButton.OK, "ℹ");
            return;
        }

        var pattern = InTagPatternBox.Text;
        var prefix = InAddressPrefixBox.Text;

        if (!int.TryParse(InAddressStartBox.Text, out int startAddr))
        {
            DialogHelpers.ShowThemedMessageBox("시작 주소는 숫자여야 합니다.", "태그 자동 생성", MessageBoxButton.OK, "⚠");
            return;
        }

        int currentWord = startAddr;
        int currentBit = 0;

        foreach (var row in selectedRows)
        {
            // In Tag 생성 (매크로 치환: $(F), $(D), $(A)만 사용)
            string inTag = pattern
                .Replace("$(F)", row.Flow)
                .Replace("$(D)", row.Device)
                .Replace("$(A)", row.Api);

            row.InSymbol = inTag;

            // In Address 생성 (DataType에 따라 주소 증가)
            if (row.InDataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            {
                // BOOL: 비트 주소 사용 (.0, .1, ... .15)
                row.InAddress = $"{prefix}{currentWord}.{currentBit}";
                currentBit++;
                if (currentBit >= 16)
                {
                    currentBit = 0;
                    currentWord++;
                }
            }
            else
            {
                // INT, DINT 등: 워드 단위 증가
                row.InAddress = $"{prefix}{currentWord}";
                currentWord++;
                currentBit = 0;  // 워드가 증가하면 비트 초기화
            }
        }

        DialogHelpers.ShowThemedMessageBox(
            $"{selectedRows.Count}개 행에 In 태그가 생성되었습니다.",
            "태그 자동 생성",
            MessageBoxButton.OK,
            "✓");
    }

    private (string DeviceName, string ApiDefName) GetDeviceAndApiNames(Guid apiCallId)
    {
        if (apiCallId == Guid.Empty)
            return ("UNKNOWN", "UNKNOWN");

        try
        {
            // ApiCall 조회
            var apiCallKvp = _store.ApiCalls.FirstOrDefault(ac => ac.Key == apiCallId);
            if (apiCallKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiCall = apiCallKvp.Value;

            // ApiDefId 확인
            if (!FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
                return ("UNKNOWN", "UNKNOWN");

            var apiDefId = apiCall.ApiDefId.Value;

            // ApiDef 조회
            var apiDefKvp = _store.ApiDefs.FirstOrDefault(ad => ad.Key == apiDefId);
            if (apiDefKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiDef = apiDefKvp.Value;
            var apiDefName = apiDef.Name;  // $(A)

            // PassiveSystem 조회
            var passiveSystemKvp = _store.Systems.FirstOrDefault(s => s.Key == apiDef.ParentId);
            var deviceName = passiveSystemKvp.Key != Guid.Empty ? passiveSystemKvp.Value.Name : "UNKNOWN";  // $(D)

            return (deviceName, apiDefName);
        }
        catch
        {
            return ("UNKNOWN", "UNKNOWN");
        }
    }

    private void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckGridSelected<IoBatchRow>(IoGrid);

    private void UncheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckGridSelected<IoBatchRow>(IoGrid);

    private void CheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckAll(_rows);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckAll(_rows);

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BatchDialogHelper.DeselectOnEmptyAreaClick(sender, e);

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// TAG Wizard 버튼 클릭 - 3단계 위자드로 변경됨
    /// </summary>
    private void TagWizard_Click(object sender, RoutedEventArgs e)
    {
        // TAG Wizard는 이제 독립적인 3단계 위자드로 동작합니다.
        // 신호를 생성하고 자동으로 Store에 적용합니다.
        var wizardDialog = new TagWizardDialog(_store)
        {
            Owner = this
        };

        wizardDialog.ShowDialog();

        // 위자드가 완료되면 안내 메시지만 표시 (창은 유지)
        if (wizardDialog.DialogResult == true)
        {
            DialogHelpers.ShowThemedMessageBox(
                "TAG Wizard가 완료되었습니다.\n\n" +
                "IO 태그가 ApiCall에 자동으로 적용되었습니다.\n" +
                "필요한 경우 추가 편집 후 프로젝트를 저장하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "ℹ");

            // Batch Settings 창은 유지 - 사용자가 수동으로 닫을 수 있음
        }
    }

    /// <summary>
    /// Export IO List 버튼 클릭
    /// </summary>
    private void ExportIoList_Click(object sender, RoutedEventArgs e)
    {
        // Step 1: 포맷 선택 다이얼로그
        var formatDialog = new ExportFormatDialog
        {
            Owner = this
        };

        if (formatDialog.ShowDialog() != true)
            return;

        var format = formatDialog.SelectedFormat;
        var useTemplate = formatDialog.UseTemplate;
        var templatePath = formatDialog.TemplatePath;

        // Step 2: 파일 저장 경로 선택
        var saveDialog = new SaveFileDialog();

        // 기본 파일 이름 설정: 모델파일명_IOList
        var modelName = !string.IsNullOrEmpty(_currentFilePath)
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : "iolist";

        var defaultFileName = format == ExportFormat.Csv
            ? $"{modelName}_IOList"
            : $"{modelName}_IOList.xlsx";

        switch (format)
        {
            case ExportFormat.Csv:
                saveDialog.Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*";
                saveDialog.FileName = defaultFileName;
                saveDialog.Title = "Export IO List (CSV)";
                break;
            case ExportFormat.Excel:
                saveDialog.Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*";
                saveDialog.FileName = defaultFileName;
                saveDialog.Title = "Export IO List (Excel)";
                break;
        }

        if (saveDialog.ShowDialog() != true)
            return;

        var selectedPath = saveDialog.FileName;
        var directory = Path.GetDirectoryName(selectedPath) ?? Environment.CurrentDirectory;
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(selectedPath);

        // Step 3: Template 디렉토리 사용 (TemplateManager에서 관리)
        var templateDir = TemplateManager.TemplatesFolderPath;

        if (!Directory.Exists(templateDir))
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 폴더를 찾을 수 없습니다:\n{templateDir}\n\n" +
                "TAG Wizard를 먼저 실행하여 템플릿을 설정하세요.",
                "Export IO List - 오류",
                MessageBoxButton.OK,
                "⚠");
            return;
        }

        try
        {
            // Step 4: 신호 생성
            var generationResult = Pipeline.generate(_store, templateDir);

            // Step 5: 에러 확인
            var errorCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.Errors);
            if (errorCount > 0)
            {
                var errorList = Microsoft.FSharp.Collections.ListModule.ToArray(generationResult.Errors);
                var errorMessages = string.Join("\n", errorList.Take(5).Select(e => $"- {e.Message}"));
                if (errorCount > 5)
                    errorMessages += $"\n... 외 {errorCount - 5}개";

                DialogHelpers.ShowThemedMessageBox(
                    $"신호 생성 중 {errorCount}개의 오류가 발생했습니다:\n\n{errorMessages}\n\n계속 진행하시겠습니까?",
                    "Export IO List",
                    MessageBoxButton.YesNo,
                    "⚠");

                // 사용자가 No를 선택하면 중단
                // TODO: DialogHelpers.ShowThemedMessageBox가 MessageBoxResult를 반환하도록 수정 필요
            }

            // Step 6: Export 수행
            switch (format)
            {
                case ExportFormat.Csv:
                    {
                        var ioPath = Path.Combine(directory, $"{fileNameWithoutExt}_io.csv");
                        var dummyPath = Path.Combine(directory, $"{fileNameWithoutExt}_dummy.csv");

                        var ioResult = Pipeline.exportIoListExtended(generationResult, ioPath);
                        var dummyResult = Pipeline.exportDummyListExtended(generationResult, dummyPath);

                        if (ioResult.IsError)
                        {
                            var error = ioResult.ErrorValue;
                            DialogHelpers.ShowThemedMessageBox($"IO CSV 내보내기 실패:\n{error}", "Export Error", MessageBoxButton.OK, "❌");
                            return;
                        }

                        if (dummyResult.IsError)
                        {
                            var error = dummyResult.ErrorValue;
                            DialogHelpers.ShowThemedMessageBox($"Dummy CSV 내보내기 실패:\n{error}", "Export Error", MessageBoxButton.OK, "❌");
                            return;
                        }

                        var ioCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.IoSignals);
                        var dummyCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.DummySignals);

                        var openResult = DialogHelpers.ShowThemedMessageBox(
                            $"내보내기 완료:\n\n" +
                            $"- IO: {Path.GetFileName(ioPath)} ({ioCount}개)\n" +
                            $"- Dummy: {Path.GetFileName(dummyPath)} ({dummyCount}개)\n\n" +
                            $"파일이 저장된 폴더를 여시겠습니까?",
                            "Export IO List",
                            MessageBoxButton.YesNo,
                            "✓");

                        if (openResult == MessageBoxResult.Yes)
                        {
                            try
                            {
                                Process.Start("explorer.exe", directory);
                            }
                            catch (Exception ex)
                            {
                                DialogHelpers.ShowThemedMessageBox(
                                    $"폴더를 열 수 없습니다:\n{ex.Message}",
                                    "오류",
                                    MessageBoxButton.OK,
                                    "⚠");
                            }
                        }
                    }
                    break;

                case ExportFormat.Excel:
                    {
                        // 템플릿 사용 여부에 따라 적절한 함수 호출
                        Microsoft.FSharp.Core.FSharpResult<Microsoft.FSharp.Core.Unit, string> result;

                        if (useTemplate && !string.IsNullOrEmpty(templatePath))
                        {
                            // 템플릿 파일 존재 확인
                            if (!File.Exists(templatePath))
                            {
                                DialogHelpers.ShowThemedMessageBox(
                                    $"템플릿 파일을 찾을 수 없습니다:\n{templatePath}",
                                    "Export Error",
                                    MessageBoxButton.OK,
                                    "❌");
                                return;
                            }

                            var templateOption = Microsoft.FSharp.Core.FSharpOption<string>.Some(templatePath);
                            result = Pipeline.exportToExcelWithTemplate(generationResult, selectedPath, templateOption);
                        }
                        else
                        {
                            result = Pipeline.exportToExcel(generationResult, selectedPath);
                        }

                        if (result.IsError)
                        {
                            var error = result.ErrorValue;
                            DialogHelpers.ShowThemedMessageBox($"Excel 내보내기 실패:\n{error}", "Export Error", MessageBoxButton.OK, "❌");
                            return;
                        }

                        var ioCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.IoSignals);
                        var dummyCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.DummySignals);

                        var templateInfo = useTemplate && !string.IsNullOrEmpty(templatePath)
                            ? $"\n- 템플릿: {Path.GetFileName(templatePath)}"
                            : "";

                        var openResult = DialogHelpers.ShowThemedMessageBox(
                            $"내보내기 완료:\n\n" +
                            $"- 파일: {Path.GetFileName(selectedPath)}\n" +
                            $"- IO 신호: {ioCount}개\n" +
                            $"- Dummy 신호: {dummyCount}개{templateInfo}\n\n" +
                            $"파일을 여시겠습니까?",
                            "Export IO List",
                            MessageBoxButton.YesNo,
                            "✓");

                        if (openResult == MessageBoxResult.Yes)
                        {
                            try
                            {
                                var processInfo = new ProcessStartInfo(selectedPath)
                                {
                                    UseShellExecute = true
                                };
                                Process.Start(processInfo);
                            }
                            catch (Exception ex)
                            {
                                DialogHelpers.ShowThemedMessageBox(
                                    $"파일을 열 수 없습니다:\n{ex.Message}",
                                    "오류",
                                    MessageBoxButton.OK,
                                    "⚠");
                            }
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"내보내기 중 오류 발생:\n\n{ex.Message}",
                "Export Error",
                MessageBoxButton.OK,
                "❌");
        }
    }
}

public sealed class IoBatchRow : BatchRowBase
{
    private string _inAddress;
    private string _inSymbol;
    private string _outAddress;
    private string _outSymbol;
    private string _outDataType;
    private string _inDataType;

    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string device, string api,
                      string inAddress, string inSymbol, string outAddress, string outSymbol,
                      string outDataType = "BOOL", string inDataType = "BOOL")
    {
        CallId = callId;
        ApiCallId = apiCallId;
        Flow = flow;
        Device = device;
        Api = api;
        _inAddress = inAddress;
        _inSymbol = inSymbol;
        _outAddress = outAddress;
        _outSymbol = outSymbol;
        _outDataType = outDataType;
        _inDataType = inDataType;
        OriginalInAddress = inAddress;
        OriginalInSymbol = inSymbol;
        OriginalOutAddress = outAddress;
        OriginalOutSymbol = outSymbol;
        OriginalOutDataType = outDataType;
        OriginalInDataType = inDataType;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Device { get; }
    public string Api { get; }

    public string OriginalInAddress { get; }
    public string OriginalInSymbol { get; }
    public string OriginalOutAddress { get; }
    public string OriginalOutSymbol { get; }
    public string OriginalOutDataType { get; }
    public string OriginalInDataType { get; }

    public string InAddress
    {
        get => _inAddress;
        set => SetField(ref _inAddress, value);
    }

    public string InSymbol
    {
        get => _inSymbol;
        set => SetField(ref _inSymbol, value);
    }

    public string OutAddress
    {
        get => _outAddress;
        set => SetField(ref _outAddress, value);
    }

    public string OutSymbol
    {
        get => _outSymbol;
        set => SetField(ref _outSymbol, value);
    }

    public string OutDataType
    {
        get => _outDataType;
        set => SetField(ref _outDataType, value);
    }

    public string InDataType
    {
        get => _inDataType;
        set => SetField(ref _inDataType, value);
    }

    public bool IsChanged =>
        !string.Equals(InAddress, OriginalInAddress, StringComparison.Ordinal) ||
        !string.Equals(InSymbol, OriginalInSymbol, StringComparison.Ordinal) ||
        !string.Equals(OutAddress, OriginalOutAddress, StringComparison.Ordinal) ||
        !string.Equals(OutSymbol, OriginalOutSymbol, StringComparison.Ordinal) ||
        !string.Equals(OutDataType, OriginalOutDataType, StringComparison.Ordinal) ||
        !string.Equals(InDataType, OriginalInDataType, StringComparison.Ordinal);
}
