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

namespace Promaker.Dialogs;

public partial class CsvImportDialog : Window
{
    private const string DefaultImportedName = "csv_import";

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
        SourceText.Text = "또는 아래에 CSV를 직접 붙여넣으세요.";
        ResetPreview("CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.");

        Loaded += (_, _) => ContentBox.Focus();
    }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string SystemName => SystemNameBox.Text.Trim();

    public CsvDocument Document =>
        _document ?? throw new InvalidOperationException("CSV document is not loaded.");

    public string SourceDisplayName => _sourceDisplayName;

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

        MessageBox.Show(owner, $"{label} 이름을 입력하세요.", "CSV 불러오기", MessageBoxButton.OK, MessageBoxImage.Information);
        textBox.Focus();
        return false;
    }

    private void UpdatePreview(CsvImportPreview preview)
    {
        var sb = new StringBuilder()
            .AppendLine($"Flow {preview.FlowNames.Length}개")
            .AppendLine($"Work {preview.WorkNames.Length}개")
            .AppendLine($"Call {preview.CallNames.Length}개")
            .AppendLine($"Passive Device System {preview.PassiveSystemNames.Length}개");

        AppendSample(sb, "Flow 예시", preview.FlowNames, 6);
        AppendSample(sb, "Work 예시", preview.WorkNames, 6);
        AppendSample(sb, "Call 예시", preview.CallNames, 6);
        AppendSample(sb, "Passive 예시", preview.PassiveSystemNames, 4);

        PreviewText.Text = sb.ToString().TrimEnd();
        ErrorBorder.Visibility = Visibility.Collapsed;
        WarningBorder.Visibility =
            preview.SyntheticApiCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text =
            preview.SyntheticApiCount > 0
                ? $"api 열이 비어 있는 {preview.SyntheticApiCount}개 항목은 Signal_<addr> 형식으로 보정되어 passive device가 생성됩니다."
                : "";
    }

    private void ResetPreview(string message)
    {
        _document = null;
        PreviewText.Text = message;
        ErrorBorder.Visibility = Visibility.Collapsed;
        WarningBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        _document = null;
        ErrorBorder.Visibility = Visibility.Visible;
        ErrorText.Text = string.Join("\n", errors);
        WarningBorder.Visibility = Visibility.Collapsed;
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
            ResetPreview("CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.");
            return false;
        }

        if (!TryGetDocument(CsvImporter.parseContent(content), out var document))
            return false;

        _document = document;
        UpdatePreview(CsvImporter.preview(document));
        return true;
    }

    private bool TryGetDocument(FSharpResult<CsvDocument, FSharpList<string>> result, out CsvDocument document)
    {
        if (result.IsError)
        {
            ShowErrors(result.ErrorValue);
            PreviewText.Text = "미리보기를 생성하지 못했습니다.";
            document = default!;
            return false;
        }

        document = result.ResultValue;
        return true;
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
            _sourceDisplayName = Path.GetFileName(picker.FileName);
            SourceText.Text = $"원본: {_sourceDisplayName}";
            ApplyAutoNames(Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception ex)
        {
            ShowErrors([ $"파일 읽기 실패: {ex.Message}" ]);
            PreviewText.Text = "미리보기를 생성하지 못했습니다.";
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
            _sourceDisplayName = "붙여넣기";
            SourceText.Text = "또는 아래에 CSV를 직접 붙여넣으세요.";
            ResetPreview("CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.");
            return;
        }

        if (!_loadingFileContent)
        {
            _sourceDisplayName = "붙여넣기";
            SourceText.Text = "원본: 직접 입력";
        }

        TryLoadDocument();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired(this, ProjectNameBox, "Project") ||
            !ValidateRequired(this, SystemNameBox, "Active System"))
            return;

        if (!TryLoadDocument())
        {
            MessageBox.Show(this, "유효한 CSV 내용을 먼저 입력하세요.", "CSV 불러오기", MessageBoxButton.OK, MessageBoxImage.Information);
            ContentBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
