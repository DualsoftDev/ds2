using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public partial class TagInspectorDialog
{
    /// <summary>IO 탭 — 현재 필터 적용된 행을 UTF-8 BOM CSV 로 내보낸다.</summary>
    private void ExportIoCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = _view.Cast<IoBatchRow>().ToList();
        if (rows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("내보낼 IO 행이 없습니다.",
                "IO CSV 내보내기", MessageBoxButton.OK, "ℹ");
            return;
        }

        if (!TryGetSavePath("IoList", out var path)) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Flow,TagName,DataType,Address,Description");
            int emitted = 0;
            foreach (var r in rows)
            {
                emitted += AppendCsvLine(sb, r, isInput: true);
                emitted += AppendCsvLine(sb, r, isInput: false);
            }

            File.WriteAllText(path, sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            AfterExport(emitted, path, "IO");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n{ex.Message}",
                "IO CSV 내보내기 오류", MessageBoxButton.OK, "✖");
        }
    }

    /// <summary>Dummy 탭 — 모든 dummy 행을 CSV 로 내보낸다.</summary>
    private void ExportDummyCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_dummyRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("내보낼 Dummy 행이 없습니다.",
                "Dummy CSV 내보내기", MessageBoxButton.OK, "ℹ");
            return;
        }

        if (!TryGetSavePath("DummyList", out var path)) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Flow,Work,Call,Symbol,DataType,Address,Type");
            foreach (var d in _dummyRows)
            {
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Flow));     sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Work));     sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Call));     sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Symbol));   sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.DataType)); sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Address));  sb.Append(',');
                sb.Append(BatchDialogHelper.EscapeCsvField(d.Type));
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            AfterExport(_dummyRows.Count, path, "Dummy");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n{ex.Message}",
                "Dummy CSV 내보내기 오류", MessageBoxButton.OK, "✖");
        }
    }

    private bool TryGetSavePath(string namePrefix, out string path)
    {
        var dlg = new SaveFileDialog
        {
            Title      = "CSV 저장",
            Filter     = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            FileName   = $"{namePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) == true) { path = dlg.FileName; return true; }
        path = ""; return false;
    }

    private void AfterExport(int emitted, string path, string kind)
    {
        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"{emitted}개 항목을 저장했습니다.\n{path}\n\n파일을 바로 열어 볼까요?",
            $"{kind} CSV 내보내기 완료", MessageBoxButton.YesNo, "✓");

        if (openResult == MessageBoxResult.Yes) TryOpenFile(path);
    }

    private static int AppendCsvLine(StringBuilder sb, IoBatchRow r, bool isInput)
    {
        var symbol  = isInput ? r.InSymbol    : r.OutSymbol;
        var address = isInput ? r.InAddress   : r.OutAddress;
        var dtype   = isInput ? r.InDataType  : r.OutDataType;
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(address))
            return 0;

        var dir = isInput ? "In" : "Out";
        sb.Append(BatchDialogHelper.EscapeCsvField(r.Flow));    sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(symbol));    sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(dtype));     sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField(address));   sb.Append(',');
        sb.Append(BatchDialogHelper.EscapeCsvField($"{r.Work}/{r.Device}/{r.Api} ({dir})"));
        sb.AppendLine();
        return 1;
    }

    private void TryOpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"파일 열기에 실패했습니다:\n{ex.Message}",
                "열기 실패", MessageBoxButton.OK, "⚠");
        }
    }
}
