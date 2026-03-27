using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    private readonly Func<IReadOnlyList<IoBatchRow>, bool>? _applyChanges;

    public IoBatchSettingsDialog(
        DsStore store,
        IReadOnlyList<IoBatchRow> rows,
        string? currentFilePath = null,
        Func<IReadOnlyList<IoBatchRow>, bool>? applyChanges = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applyChanges = applyChanges;
        _currentFilePath = currentFilePath;

        _rows = new ObservableCollection<IoBatchRow>(rows);

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);

        FlowFilterBox.TextChanged += (_, _) => _view.Refresh();
        DeviceFilterBox.TextChanged += (_, _) => _view.Refresh();
        ApiFilterBox.TextChanged += (_, _) => _view.Refresh();
        RefreshApplyButtonState();
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

        RefreshApplyButtonState();
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
            string outTag = pattern
                .Replace("$(F)", row.Flow)
                .Replace("$(D)", row.Device)
                .Replace("$(A)", row.Api);

            row.OutSymbol = outTag;

            if (row.OutDataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            {
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
                row.OutAddress = $"{prefix}{currentWord}";
                currentWord++;
                currentBit = 0;
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
            string inTag = pattern
                .Replace("$(F)", row.Flow)
                .Replace("$(D)", row.Device)
                .Replace("$(A)", row.Api);

            row.InSymbol = inTag;

            if (row.InDataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            {
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
                row.InAddress = $"{prefix}{currentWord}";
                currentWord++;
                currentBit = 0;
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
            var apiCallKvp = _store.ApiCalls.FirstOrDefault(ac => ac.Key == apiCallId);
            if (apiCallKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiCall = apiCallKvp.Value;

            if (!FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
                return ("UNKNOWN", "UNKNOWN");

            var apiDefId = apiCall.ApiDefId.Value;

            var apiDefKvp = _store.ApiDefs.FirstOrDefault(ad => ad.Key == apiDefId);
            if (apiDefKvp.Key == Guid.Empty)
                return ("UNKNOWN", "UNKNOWN");

            var apiDef = apiDefKvp.Value;
            var apiDefName = apiDef.Name;

            var passiveSystemKvp = _store.Systems.FirstOrDefault(s => s.Key == apiDef.ParentId);
            var deviceName = passiveSystemKvp.Key != Guid.Empty ? passiveSystemKvp.Value.Name : "UNKNOWN";

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

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: IoBatchRow row } checkBox)
            return;

        BatchDialogHelper.ApplyCheckStateToSelectedRows(IoGrid, row, checkBox.IsChecked == true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        IoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IoGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var changed = ChangedRows;
        if (changed.Count == 0)
            return;

        if (_applyChanges is null)
        {
            DialogResult = true;
            return;
        }

        if (!_applyChanges(changed))
            return;

        foreach (var row in changed)
            row.AcceptChanges();

        RefreshApplyButtonState();
    }

    private void TagWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizardDialog = new TagWizardDialog(_store)
        {
            Owner = this
        };

        wizardDialog.ShowDialog();

        if (wizardDialog.DialogResult == true)
        {
            DialogHelpers.ShowThemedMessageBox(
                "TAG Wizard가 완료되었습니다.\n\n" +
                "IO 태그가 ApiCall에 자동으로 적용되었습니다.\n" +
                "필요한 경우 추가 편집 후 프로젝트를 저장하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "ℹ");
        }
    }

    private void ExportIoList_Click(object sender, RoutedEventArgs e)
    {
        var formatDialog = new ExportFormatDialog
        {
            Owner = this
        };

        if (formatDialog.ShowDialog() != true)
            return;

        var format = formatDialog.SelectedFormat;
        var useTemplate = formatDialog.UseTemplate;
        var templatePath = formatDialog.TemplatePath;

        var saveDialog = new SaveFileDialog();

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
            var generationResult = Pipeline.generate(_store, templateDir);

            var errorCount = Microsoft.FSharp.Collections.ListModule.Length(generationResult.Errors);
            if (errorCount > 0)
            {
                var errorList = Microsoft.FSharp.Collections.ListModule.ToArray(generationResult.Errors);
                var errorMessages = string.Join("\n", errorList.Take(5).Select(err => $"- {err.Message}"));
                if (errorCount > 5)
                    errorMessages += $"\n... 외 {errorCount - 5}개";

                DialogHelpers.ShowThemedMessageBox(
                    $"신호 생성 중 {errorCount}개의 오류가 발생했습니다:\n\n{errorMessages}\n\n계속 진행하시겠습니까?",
                    "Export IO List",
                    MessageBoxButton.YesNo,
                    "⚠");
            }

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
                            DialogHelpers.ShowThemedMessageBox($"IO CSV 내보내기 실패:\n{ioResult.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
                            return;
                        }

                        if (dummyResult.IsError)
                        {
                            DialogHelpers.ShowThemedMessageBox($"Dummy CSV 내보내기 실패:\n{dummyResult.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
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
                            try { Process.Start("explorer.exe", directory); }
                            catch (Exception ex)
                            {
                                DialogHelpers.ShowThemedMessageBox($"폴더를 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
                            }
                        }
                    }
                    break;

                case ExportFormat.Excel:
                    {
                        Microsoft.FSharp.Core.FSharpResult<Microsoft.FSharp.Core.Unit, string> result;

                        if (useTemplate && !string.IsNullOrEmpty(templatePath))
                        {
                            if (!File.Exists(templatePath))
                            {
                                DialogHelpers.ShowThemedMessageBox($"템플릿 파일을 찾을 수 없습니다:\n{templatePath}", "Export Error", MessageBoxButton.OK, "❌");
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
                            DialogHelpers.ShowThemedMessageBox($"Excel 내보내기 실패:\n{result.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
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
                                Process.Start(new ProcessStartInfo(selectedPath) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                DialogHelpers.ShowThemedMessageBox($"파일을 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
                            }
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"내보내기 중 오류 발생:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, "❌");
        }
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static int ApplyImportedRows(
        IEnumerable<IoBatchRow> targetRows,
        IEnumerable<CsvImporter.IoImportRow> importRows)
    {
        var rowMap = targetRows.ToDictionary(
            row => BuildImportKey(row.Flow, row.Device, row.Api),
            StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        foreach (var importRow in importRows)
        {
            if (!rowMap.TryGetValue(BuildImportKey(importRow.FlowName, importRow.DeviceName, importRow.ApiName), out var target))
                continue;

            if (string.Equals(importRow.Direction, "Output", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(importRow.Address))
                    target.OutAddress = importRow.Address;
                if (!string.IsNullOrEmpty(importRow.VarName))
                    target.OutSymbol = importRow.VarName;
                if (!string.IsNullOrEmpty(importRow.DataType))
                    target.OutDataType = importRow.DataType;
                matched++;
                continue;
            }

            if (!string.Equals(importRow.Direction, "Input", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(importRow.Address))
                target.InAddress = importRow.Address;
            if (!string.IsNullOrEmpty(importRow.VarName))
                target.InSymbol = importRow.VarName;
            if (!string.IsNullOrEmpty(importRow.DataType))
                target.InDataType = importRow.DataType;
            matched++;
        }

        return matched;
    }

    private static string BuildImportKey(string flow, string device, string api) =>
        string.Join("\u001F", flow, device, api);

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("내보낼 데이터가 없습니다.", "CSV 내보내기", MessageBoxButton.OK, "⚠");
            return;
        }

        var modelName = !string.IsNullOrEmpty(_currentFilePath)
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : "io_batch";

        var picker = new SaveFileDialog
        {
            Title = "I/O CSV 내보내기",
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"{modelName}_io_batch.csv"
        };

        if (picker.ShowDialog(this) != true)
            return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Flow,Device,Api,OutSymbol,OutDataType,OutAddress,InSymbol,InDataType,InAddress");
        foreach (var row in _rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsvField(row.Flow),
                EscapeCsvField(row.Device),
                EscapeCsvField(row.Api),
                EscapeCsvField(row.OutSymbol),
                EscapeCsvField(row.OutDataType),
                EscapeCsvField(row.OutAddress),
                EscapeCsvField(row.InSymbol),
                EscapeCsvField(row.InDataType),
                EscapeCsvField(row.InAddress)));
        }

        File.WriteAllText(picker.FileName, sb.ToString(), new UTF8Encoding(false));
        DialogHelpers.ShowThemedMessageBox(
            $"CSV 내보내기 완료: {_rows.Count}건\n\n파일: {Path.GetFileName(picker.FileName)}",
            "CSV 내보내기", MessageBoxButton.OK, "✓");
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "I/O CSV 가져오기",
            Filter = "CSV Files|*.csv|All Files|*.*",
            DefaultExt = ".csv"
        };

        if (picker.ShowDialog(this) != true)
            return;

        var result = Ds2.IOList.CsvImporter.parseIoCsv(picker.FileName);
        if (result.IsError)
        {
            DialogHelpers.ShowThemedMessageBox(result.ErrorValue, "CSV Import 오류", MessageBoxButton.OK, "⚠");
            return;
        }

        var importRows = Microsoft.FSharp.Collections.ListModule.ToArray(result.ResultValue);
        if (importRows.Length == 0)
            return;

        var matched = ApplyImportedRows(_rows, importRows);

        RefreshApplyButtonState();
        var total = importRows.Length;
        DialogHelpers.ShowThemedMessageBox(
            $"CSV 가져오기 완료:\n\n- 전체: {total}건\n- 매칭: {matched}건\n- 미매칭: {total - matched}건",
            "CSV Import", MessageBoxButton.OK, "✓");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshApplyButtonState() =>
        ApplyButton.IsEnabled = ChangedRows.Count > 0;
}

public sealed class IoBatchRow : BatchRowBase
{
    private string _inAddress;
    private string _inSymbol;
    private string _outAddress;
    private string _outSymbol;
    private string _outDataType;
    private string _inDataType;
    private string _originalInAddress;
    private string _originalInSymbol;
    private string _originalOutAddress;
    private string _originalOutSymbol;

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
        _originalInAddress = inAddress;
        _originalInSymbol = inSymbol;
        _originalOutAddress = outAddress;
        _originalOutSymbol = outSymbol;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Device { get; }
    public string Api { get; }

    public string OriginalInAddress => _originalInAddress;
    public string OriginalInSymbol => _originalInSymbol;
    public string OriginalOutAddress => _originalOutAddress;
    public string OriginalOutSymbol => _originalOutSymbol;

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
        !string.Equals(OutSymbol, OriginalOutSymbol, StringComparison.Ordinal);

    public void AcceptChanges()
    {
        _originalInAddress = InAddress;
        _originalInSymbol = InSymbol;
        _originalOutAddress = OutAddress;
        _originalOutSymbol = OutSymbol;
    }
}
