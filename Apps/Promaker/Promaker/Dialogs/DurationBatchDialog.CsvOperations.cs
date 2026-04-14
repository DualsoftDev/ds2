using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class DurationBatchDialog
{
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_workRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("내보낼 데이터가 없습니다.", "CSV 내보내기", MessageBoxButton.OK, "⚠");
            return;
        }

        var modelName = !string.IsNullOrEmpty(_currentFilePath)
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : "duration_batch";

        var picker = new SaveFileDialog
        {
            Title = "Duration CSV 내보내기",
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"{modelName}_duration.csv"
        };

        if (picker.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("System,Flow,Work,Duration");
        foreach (var row in _workRows)
        {
            sb.AppendLine(string.Join(",",
                BatchDialogHelper.EscapeCsvField(row.SystemName),
                BatchDialogHelper.EscapeCsvField(row.FlowName),
                BatchDialogHelper.EscapeCsvField(row.WorkName),
                BatchDialogHelper.EscapeCsvField(row.Duration)));
        }

        File.WriteAllText(picker.FileName, sb.ToString(), Encoding.UTF8);

        CsvFileHelper.PromptOpenAfterExport(picker.FileName, _workRows.Count, "Duration CSV 내보내기");
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Duration CSV 가져오기",
            Filter = "CSV Files|*.csv|All Files|*.*",
            DefaultExt = ".csv"
        };

        if (picker.ShowDialog(this) != true)
            return;

        List<DurationImportRow> importRows;
        try
        {
            importRows = ParseDurationCsv(picker.FileName);
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"CSV 파싱 오류:\n{ex.Message}", "CSV Import 오류", MessageBoxButton.OK, "⚠");
            return;
        }

        if (importRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("가져올 데이터가 없습니다.", "CSV Import", MessageBoxButton.OK, "⚠");
            return;
        }

        var result = ApplyImportedRows(_workRows, importRows);
        var unmatchedDetails = FormatUnmatchedTargetRows(result.UnmatchedTargetRows);
        var icon = result.UnmatchedTargetRows.Count > 0 ? "⚠" : "✓";

        DialogHelpers.ShowThemedMessageBox(
            $"CSV 가져오기 완료:\n\n" +
            $"- 프로젝트 전체 행: {result.TotalTargetRows}건\n" +
            $"- 적용된 행: {result.AppliedTargetRows}건\n" +
            $"- 미적용 행: {result.UnmatchedTargetRows.Count}건" +
            unmatchedDetails,
            "Duration CSV Import", MessageBoxButton.OK, icon);
    }

    private static List<DurationImportRow> ParseDurationCsv(string filePath)
    {
        string[] lines;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }
        if (lines.Length < 2)
            throw new InvalidOperationException("CSV 파일에 데이터가 없습니다.");

        var header = lines[0].ToLowerInvariant();
        if (!header.Contains("work") || !header.Contains("duration"))
            throw new InvalidOperationException("CSV 헤더를 인식할 수 없습니다. (System,Flow,Work,Duration 형식 필요)");

        var results = new List<DurationImportRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var fields = ParseCsvFields(line);
            if (fields.Count < 4) continue;

            results.Add(new DurationImportRow(fields[0], fields[1], fields[2], fields[3]));
        }

        return results;
    }

    private static List<string> ParseCsvFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private sealed record ImportApplyResult(
        int TotalTargetRows,
        int AppliedTargetRows,
        int TargetCellsUpdated,
        IReadOnlyList<DurationRow> UnmatchedTargetRows);

    private static ImportApplyResult ApplyImportedRows(
        IReadOnlyCollection<DurationRow> targetRows,
        IEnumerable<DurationImportRow> importRows)
    {
        var rowMap = targetRows.ToLookup(
            row => BuildImportKey(row.SystemName, row.FlowName, row.WorkName),
            StringComparer.OrdinalIgnoreCase);

        var matchedTargetRows = new HashSet<DurationRow>();
        var targetCellsUpdated = 0;

        foreach (var importRow in importRows)
        {
            var key = BuildImportKey(importRow.System, importRow.Flow, importRow.Work);
            if (!rowMap.Contains(key) || string.IsNullOrEmpty(importRow.Duration))
                continue;

            foreach (var target in rowMap[key])
            {
                target.Duration = importRow.Duration;
                targetCellsUpdated++;
                matchedTargetRows.Add(target);
            }
        }

        var unmatched = targetRows.Where(r => !matchedTargetRows.Contains(r)).ToList();
        return new ImportApplyResult(targetRows.Count, matchedTargetRows.Count, targetCellsUpdated, unmatched);
    }

    private static string FormatUnmatchedTargetRows(IReadOnlyList<DurationRow> unmatched, int previewLimit = 10)
    {
        if (unmatched.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine();

        var controlRows = unmatched.Where(r => !r.IsDeviceWork).ToList();
        var deviceRows = unmatched.Where(r => r.IsDeviceWork).ToList();

        FormatGroup(sb, "Control 미적용", controlRows, r => $"{r.FlowName}/{r.WorkName}", previewLimit);
        FormatGroup(sb, "Device 미적용", deviceRows, r => $"{r.SystemName}/{r.WorkName}", previewLimit);

        return sb.ToString();

        static void FormatGroup(StringBuilder sb, string header, List<DurationRow> rows, Func<DurationRow, string> formatter, int limit)
        {
            if (rows.Count == 0) return;
            sb.AppendLine($"[{header}] {rows.Count}건");
            var preview = Math.Min(rows.Count, limit);
            for (var i = 0; i < preview; i++)
                sb.AppendLine($"  · {formatter(rows[i])}");
            if (rows.Count > preview)
                sb.AppendLine($"  … 외 {rows.Count - preview}건");
        }
    }

    private static string BuildImportKey(string system, string flow, string work) =>
        string.Join("\u001F", system, flow, work);

    private sealed record DurationImportRow(string System, string Flow, string Work, string Duration);
}
