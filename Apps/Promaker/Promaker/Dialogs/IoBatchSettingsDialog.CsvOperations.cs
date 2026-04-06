using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.IOList;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog
{
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
                    ExportAsCsv(generationResult, directory, fileNameWithoutExt);
                    break;
                case ExportFormat.Excel:
                    ExportAsExcel(generationResult, selectedPath, useTemplate, templatePath);
                    break;
            }
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"내보내기 중 오류 발생:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, "❌");
        }
    }

    private static void ExportAsCsv(
        Ds2.IOList.GenerationResult generationResult,
        string directory,
        string fileNameWithoutExt)
    {
        var ioPath = Path.Combine(directory, $"{fileNameWithoutExt}_io.csv");
        var dummyPath = Path.Combine(directory, $"{fileNameWithoutExt}_dummy.csv");

        var ioResult = Pipeline.exportIoListExtended(generationResult, ioPath);
        var dummyResult = Pipeline.exportDummyListExtended(generationResult, dummyPath);

        if (ioResult.IsError)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO CSV 내보내기 실패:\n{ioResult.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
            return;
        }

        if (dummyResult.IsError)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"Dummy CSV 내보내기 실패:\n{dummyResult.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
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
                DialogHelpers.ShowThemedMessageBox(
                    $"폴더를 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
            }
        }
    }

    private static void ExportAsExcel(
        Ds2.IOList.GenerationResult generationResult,
        string selectedPath,
        bool useTemplate,
        string? templatePath)
    {
        Microsoft.FSharp.Core.FSharpResult<Microsoft.FSharp.Core.Unit, string> result;

        if (useTemplate && !string.IsNullOrEmpty(templatePath))
        {
            if (!File.Exists(templatePath))
            {
                DialogHelpers.ShowThemedMessageBox(
                    $"템플릿 파일을 찾을 수 없습니다:\n{templatePath}", "Export Error", MessageBoxButton.OK, "❌");
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
            DialogHelpers.ShowThemedMessageBox(
                $"Excel 내보내기 실패:\n{result.ErrorValue}", "Export Error", MessageBoxButton.OK, "❌");
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
                DialogHelpers.ShowThemedMessageBox(
                    $"파일을 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
            }
        }
    }

    private static string EscapeCsvField(string value) =>
        BatchDialogHelper.EscapeCsvField(value);

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

        var sb = new StringBuilder();
        sb.AppendLine("Flow,Work,Device,Api,OutSymbol,OutDataType,OutAddress,InSymbol,InDataType,InAddress");
        foreach (var row in _rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsvField(row.Flow),
                EscapeCsvField(row.Work),
                EscapeCsvField(row.Device),
                EscapeCsvField(row.Api),
                EscapeCsvField(row.OutSymbol),
                EscapeCsvField(row.OutDataType),
                EscapeCsvField(row.OutAddress),
                EscapeCsvField(row.InSymbol),
                EscapeCsvField(row.InDataType),
                EscapeCsvField(row.InAddress)));
        }

        File.WriteAllText(picker.FileName, sb.ToString(), Encoding.UTF8);

        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"CSV 내보내기 완료: {_rows.Count}건\n\n" +
            $"파일: {Path.GetFileName(picker.FileName)}\n\n" +
            $"파일을 여시겠습니까?",
            "CSV 내보내기",
            MessageBoxButton.YesNo,
            "✓");

        if (openResult == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(picker.FileName) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                DialogHelpers.ShowThemedMessageBox(
                    $"파일을 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
            }
        }
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
}
