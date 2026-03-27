using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.CSV;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Presentation;

namespace Promaker.Dialogs;

public class CsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ApiName { get; set; } = "";
    public string InName { get; set; } = "";
    public string InAddress { get; set; } = "";
    public string OutName { get; set; } = "";
    public string OutAddress { get; set; } = "";
}

public partial class CsvImportDialog : Window
{
    private const string DefaultImportedName = "csv_import";
    private const string DefaultSourceText = "또는 아래에 CSV를 직접 붙여넣으세요.";
    private const string EmptyPreviewText = "CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.";
    private const string PreviewFailureText = "미리보기를 생성하지 못했습니다.";
    private const string SampleCsv = @"Flow,Work,Device,Api,InName,InAddress,OutName,OutAddress
Cutting,Load,Cylinder,Up,입력신호,X10A0,출력신호,Y10B0
Cutting,Load,Sensor,Detect,,X10A2,,
Cutting,Load,Motor,Run,,X10A3,,Y10B3
Cutting,Unload,Cylinder,Down,하강신호,X10B0,하강출력,Y10C0
Cutting,Unload,Conveyor,Forward,,,,Y10C1
Assembly,PartIn,Gripper,Grip,그립신호,X20A0,그립출력,Y20B0
Assembly,PartIn,Gripper,Release,릴리즈신호,X20A1,릴리즈출력,Y20B1
Assembly,PartIn,Sensor,Detect,,X20A2,,
Assembly,Process,Press,Down,프레스하강,X20C0,프레스출력,Y20D0
Assembly,Process,Press,Up,프레스상승,X20C1,프레스상승출력,Y20D1
Assembly,PartOut,Ejector,Push,,X20E0,,Y20F0
Assembly,PartOut,Ejector,Return,,X20E1,,Y20F1";

    private CsvDocument? _document;
    private string _autoProjectName = DefaultImportedName;
    private string _autoSystemName = DefaultImportedName;
    private string _sourceDisplayName = "붙여넣기";
    private bool _loadingFileContent;

    public CsvImportDialog()
    {
        InitializeComponent();

        ProjectNameBox.Text = DefaultImportedName;
        SystemNameBox.Text = DefaultImportedName;
        SourceText.Text = DefaultSourceText;
        ResetPreview(EmptyPreviewText);

        Loaded += (_, _) => ContentBox.Focus();
    }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string SystemName => SystemNameBox.Text.Trim();

    public CsvDocument Document =>
        _document ?? throw new InvalidOperationException("CSV document is not loaded.");

    public string SourceDisplayName => _sourceDisplayName;

    private static string BuildPreviewSummary(CsvImportPreview preview, int entryCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개")
            .AppendLine($"✓ Call: {preview.CallNames.Length}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개")
            .AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Call 샘플", preview.CallNames, 5);

        if (entryCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {entryCount}개 항목 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSyntheticWarningText(int syntheticApiCount) =>
        syntheticApiCount > 0
            ? $"⚠ Api 열이 비어 있는 {syntheticApiCount}개 항목은 Signal_<addr> 형식으로 자동 생성됩니다."
            : "";

    private static void AppendSample(StringBuilder sb, string label, IEnumerable<string> items, int take)
    {
        var sample = string.Join(", ", items.Take(take));
        if (!string.IsNullOrWhiteSpace(sample))
            sb.AppendLine($"{label}: {sample}");
    }

    private static bool ValidateRequired(Window owner, TextBox textBox, string label)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text?.Trim()))
            return true;

        DialogHelpers.Info(owner, $"{label} 이름을 입력하세요.", "CSV 불러오기");
        textBox.Focus();
        return false;
    }

    private static string OptionText(FSharpOption<string> value) =>
        FSharpOption<string>.get_IsSome(value) ? value.Value : "";

    private static CsvRowViewModel ToRowViewModel(CsvEntry entry) =>
        new()
        {
            FlowName = entry.FlowName,
            WorkName = entry.WorkName,
            DeviceName = entry.DeviceName,
            ApiName = entry.ApiName,
            InName = OptionText(entry.InName),
            InAddress = OptionText(entry.InAddress),
            OutName = OptionText(entry.OutName),
            OutAddress = OptionText(entry.OutAddress)
        };

    private void SetSourceDisplay(string displayName, string description)
    {
        _sourceDisplayName = displayName;
        SourceText.Text = description;
    }

    private void ResetDirectInputPreview()
    {
        SetSourceDisplay("붙여넣기", DefaultSourceText);
        ResetPreview(EmptyPreviewText);
    }

    private void SetPreviewState(
        CsvDocument? document,
        IEnumerable<CsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _document = document;
        PreviewGrid.ItemsSource = rows?.ToList();
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ShowPreviewFailure(string message)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: message);
    }

    private void ShowInfo(string message, string title = "CSV 불러오기") =>
        DialogHelpers.Info(this, message, title);

    private void ShowError(string message, string title = "오류") =>
        DialogHelpers.Error(this, message, title);

    private void ApplyPreview(CsvDocument document, CsvImportPreview preview)
    {
        SetPreviewState(
            document,
            document.Entries.Take(100).Select(ToRowViewModel),
            BuildPreviewSummary(preview, document.Entries.Length),
            warningText: BuildSyntheticWarningText(preview.SyntheticApiCount));
    }

    private void ResetPreview(string message)
    {
        SetPreviewState(null, null, message);
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: string.Join("\n", errors));
    }

    private void ApplyAutoNames(string defaultName)
    {
        var normalized = string.IsNullOrWhiteSpace(defaultName)
            ? DefaultImportedName
            : defaultName.Trim();

        UpdateAutoName(ProjectNameBox, ref _autoProjectName, normalized);
        UpdateAutoName(SystemNameBox, ref _autoSystemName, normalized);
    }

    private static void UpdateAutoName(TextBox textBox, ref string previousAutoName, string nextAutoName)
    {
        var current = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, previousAutoName, StringComparison.Ordinal))
            textBox.Text = nextAutoName;

        previousAutoName = nextAutoName;
    }

    private bool TryLoadDocument()
    {
        var content = ContentBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            ResetPreview(EmptyPreviewText);
            return false;
        }

        if (!TryGetDocument(CsvImporter.parseContent(content), out var document))
            return false;

        ApplyPreview(document, CsvImporter.preview(document));
        return true;
    }

    private bool TryGetDocument(FSharpResult<CsvDocument, FSharpList<string>> result, out CsvDocument document)
    {
        if (result.IsError)
        {
            ShowErrors(result.ErrorValue);
            document = default!;
            return false;
        }

        document = result.ResultValue;
        return true;
    }

    private void SaveSample_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            DefaultExt = FileExtensions.Csv,
            FileName = "sample.csv"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(picker.FileName, SampleCsv, Encoding.UTF8);
            ShowInfo($"샘플 CSV 파일이 저장되었습니다.\n\n{picker.FileName}", "샘플 저장 완료");
        }
        catch (Exception ex)
        {
            ShowError($"샘플 저장 실패: {ex.Message}");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            _loadingFileContent = true;
            ContentBox.Text = File.ReadAllText(picker.FileName);
            SetSourceDisplay(Path.GetFileName(picker.FileName), $"원본: {Path.GetFileName(picker.FileName)}");
            ApplyAutoNames(Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception ex)
        {
            ShowPreviewFailure($"파일 읽기 실패: {ex.Message}");
        }
        finally
        {
            _loadingFileContent = false;
        }
    }

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        if (!_loadingFileContent)
            SetSourceDisplay("붙여넣기", "원본: 직접 입력");

        TryLoadDocument();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired(this, ProjectNameBox, "Project") ||
            !ValidateRequired(this, SystemNameBox, "Active System"))
            return;

        if (!TryLoadDocument())
        {
            ShowInfo("유효한 CSV 내용을 먼저 입력하세요.");
            ContentBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
