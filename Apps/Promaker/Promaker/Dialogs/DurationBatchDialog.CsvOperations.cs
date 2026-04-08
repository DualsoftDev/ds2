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

        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"CSV 내보내기 완료: {_workRows.Count}건\n\n" +
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

        var matched = ApplyImportedRows(_workRows, importRows);

        DialogHelpers.ShowThemedMessageBox(
            $"CSV 가져오기 완료:\n\n- 전체: {importRows.Count}건\n- 매칭: {matched}건\n- 미매칭: {importRows.Count - matched}건",
            "CSV Import", MessageBoxButton.OK, "✓");
    }

    private static List<DurationImportRow> ParseDurationCsv(string filePath)
    {
        string[] lines;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
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

    private static int ApplyImportedRows(
        IEnumerable<DurationRow> targetRows,
        IEnumerable<DurationImportRow> importRows)
    {
        var rowMap = targetRows.ToLookup(
            row => BuildImportKey(row.SystemName, row.FlowName, row.WorkName),
            StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        foreach (var importRow in importRows)
        {
            var key = BuildImportKey(importRow.System, importRow.Flow, importRow.Work);
            if (!rowMap.Contains(key))
                continue;

            if (string.IsNullOrEmpty(importRow.Duration))
                continue;

            foreach (var target in rowMap[key])
            {
                target.Duration = importRow.Duration;
                matched++;
            }
        }

        return matched;
    }

    private static string BuildImportKey(string system, string flow, string work) =>
        string.Join("\u001F", system, flow, work);

    private sealed record DurationImportRow(string System, string Flow, string Work, string Duration);
}
