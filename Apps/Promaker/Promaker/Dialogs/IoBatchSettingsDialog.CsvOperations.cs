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

        var summary =
            $"내보내기 완료:\n\n" +
            $"- IO: {Path.GetFileName(ioPath)} ({ioCount}개)\n" +
            $"- Dummy: {Path.GetFileName(dummyPath)} ({dummyCount}개)";
        CsvFileHelper.PromptOpenFolderAfterExport(directory, summary, "Export IO List");
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

        var summary =
            $"내보내기 완료:\n\n" +
            $"- 파일: {Path.GetFileName(selectedPath)}\n" +
            $"- IO 신호: {ioCount}개\n" +
            $"- Dummy 신호: {dummyCount}개{templateInfo}";
        // Excel도 같은 헬퍼로 자동 열기 통일 (UseShellExecute=true)
        CsvFileHelper.PromptOpenAfterExportWithSummary(selectedPath, summary, "Export IO List");
    }

    private static string EscapeCsvField(string value) =>
        BatchDialogHelper.EscapeCsvField(value);

    /// <summary>
    /// CSV import 적용 결과. 프로젝트 관점으로 미적용 행을 추적한다.
    /// UnmatchedTargetRows는 import 후에도 CSV로 한 번도 touched 되지 않은 IoBatchRow.
    /// </summary>
    private sealed record ImportApplyResult(
        int TotalTargetRows,
        int AppliedTargetRows,
        int TargetCellsUpdated,
        IReadOnlyList<IoBatchRow> UnmatchedTargetRows);

    private static ImportApplyResult ApplyImportedRows(
        IReadOnlyCollection<IoBatchRow> targetRows,
        IEnumerable<CsvImporter.IoImportRow> importRows)
    {
        // 같은 (Flow, Device, Api)를 호출하는 Work가 둘 이상이면 키가 중복되므로
        // ToDictionary 대신 ToLookup으로 fan-out을 허용한다.
        // 정확 매칭용: (Flow, Work, Device, Api) — Work까지 일치하는 행만
        // Fallback 매칭용: (Flow, "", Device, Api) — import에 Work 정보가 없을 때
        //   같은 (Flow, Device, Api)를 가진 모든 Work에 fan-out 적용
        var exactMap = targetRows.ToLookup(
            row => BuildImportKey(row.Flow, row.Work, row.Device, row.Api),
            StringComparer.OrdinalIgnoreCase);
        var fanoutMap = targetRows.ToLookup(
            row => BuildImportKey(row.Flow, string.Empty, row.Device, row.Api),
            StringComparer.OrdinalIgnoreCase);

        // 프로젝트 관점 미적용 추적: 한 번이라도 In/Out 셀이 CSV로 업데이트된 IoBatchRow를 기록
        var matchedTargetRows = new HashSet<IoBatchRow>();
        var targetCellsUpdated = 0;

        foreach (var importRow in importRows)
        {
            IEnumerable<IoBatchRow> targets;
            if (!string.IsNullOrEmpty(importRow.WorkName))
            {
                var exactKey = BuildImportKey(importRow.FlowName, importRow.WorkName, importRow.DeviceName, importRow.ApiName);
                targets = exactMap.Contains(exactKey) ? exactMap[exactKey] : Array.Empty<IoBatchRow>();
            }
            else
            {
                var fanoutKey = BuildImportKey(importRow.FlowName, string.Empty, importRow.DeviceName, importRow.ApiName);
                targets = fanoutMap.Contains(fanoutKey) ? fanoutMap[fanoutKey] : Array.Empty<IoBatchRow>();
            }

            foreach (var target in targets)
            {
                if (string.Equals(importRow.Direction, "Output", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(importRow.Address))
                        target.OutAddress = importRow.Address;
                    if (!string.IsNullOrEmpty(importRow.VarName))
                        target.OutSymbol = importRow.VarName;
                    if (!string.IsNullOrEmpty(importRow.DataType))
                        target.OutDataType = importRow.DataType;
                    targetCellsUpdated++;
                    matchedTargetRows.Add(target);
                }
                else if (string.Equals(importRow.Direction, "Input", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(importRow.Address))
                        target.InAddress = importRow.Address;
                    if (!string.IsNullOrEmpty(importRow.VarName))
                        target.InSymbol = importRow.VarName;
                    if (!string.IsNullOrEmpty(importRow.DataType))
                        target.InDataType = importRow.DataType;
                    targetCellsUpdated++;
                    matchedTargetRows.Add(target);
                }
            }
        }

        var unmatchedTargets = targetRows
            .Where(row => !matchedTargetRows.Contains(row))
            .ToList();

        return new ImportApplyResult(
            targetRows.Count,
            matchedTargetRows.Count,
            targetCellsUpdated,
            unmatchedTargets);
    }

    private static string BuildImportKey(string flow, string work, string device, string api) =>
        string.Join("\u001F", flow, work, device, api);

    /// <summary>
    /// 프로젝트 관점에서 CSV에 매칭되지 않은 IoBatchRow 목록을 메시지박스 본문으로 포맷.
    /// 처음 N건만 (Flow/Work/Device.Api) 키로 나열하고 나머지는 "외 K건"으로 요약.
    /// </summary>
    private static string FormatUnmatchedTargetRows(IReadOnlyList<IoBatchRow> unmatched, int previewLimit = 10)
    {
        if (unmatched.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("프로젝트에서 CSV에 매칭되지 않은 행:");
        var preview = Math.Min(unmatched.Count, previewLimit);
        for (var i = 0; i < preview; i++)
        {
            var r = unmatched[i];
            var workPart = string.IsNullOrEmpty(r.Work) ? "" : $"/{r.Work}";
            sb.AppendLine($"  · {r.Flow}{workPart}/{r.Device}.{r.Api}");
        }
        if (unmatched.Count > preview)
            sb.AppendLine($"  … 외 {unmatched.Count - preview}건");
        return sb.ToString();
    }

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
        sb.AppendLine("Flow,Work,Device,Api,OutName,OutDataType,OutAddress,InName,InDataType,InAddress");
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

        CsvFileHelper.PromptOpenAfterExport(picker.FileName, _rows.Count, "I/O CSV 내보내기");
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

        try
        {
            var result = Ds2.IOList.CsvImporter.parseIoCsv(picker.FileName);
            if (result.IsError)
            {
                DialogHelpers.ShowThemedMessageBox(result.ErrorValue, "CSV Import 오류", MessageBoxButton.OK, "⚠");
                return;
            }

            var importRows = Microsoft.FSharp.Collections.ListModule.ToArray(result.ResultValue);
            if (importRows.Length == 0)
            {
                DialogHelpers.ShowThemedMessageBox("가져올 데이터가 없습니다.", "CSV Import", MessageBoxButton.OK, "⚠");
                return;
            }

            var applyResult = ApplyImportedRows(_rows, importRows);

            // 미매치 행 하이라이트 설정
            foreach (var row in _rows) row.IsUnmatched = false;
            foreach (var row in applyResult.UnmatchedTargetRows) row.IsUnmatched = true;
            ShowOnlyUnmatchedCheckBox.Visibility = applyResult.UnmatchedTargetRows.Count > 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            _view.Refresh();

            RefreshApplyButtonState();
            var unmatchedDetails = FormatUnmatchedTargetRows(applyResult.UnmatchedTargetRows);
            var icon = applyResult.UnmatchedTargetRows.Count > 0 ? "⚠" : "✓";
            DialogHelpers.ShowThemedMessageBox(
                $"CSV 가져오기 완료:\n\n" +
                $"- 프로젝트 전체 행: {applyResult.TotalTargetRows}건\n" +
                $"- 적용된 행: {applyResult.AppliedTargetRows}건\n" +
                $"- 미적용 행: {applyResult.UnmatchedTargetRows.Count}건" +
                unmatchedDetails,
                "CSV Import", MessageBoxButton.OK, icon);
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"CSV 가져오기 중 오류:\n{ex.Message}", "CSV Import 오류", MessageBoxButton.OK, "⚠");
        }
    }
}
