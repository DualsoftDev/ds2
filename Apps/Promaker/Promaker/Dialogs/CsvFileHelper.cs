using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// CSV export/import 공통 헬퍼.
/// 모든 CSV 진입점이 동일한 방식으로 파일을 읽고, export 후 자동 열기 다이얼로그를 표시한다.
/// </summary>
internal static class CsvFileHelper
{
    /// <summary>
    /// CSV 파일을 다른 프로세스(Excel 등)가 점유 중이어도 읽을 수 있도록
    /// FileShare.ReadWrite | FileShare.Delete 모드로 텍스트 읽기.
    /// </summary>
    public static string ReadAllTextShared(string filePath)
    {
        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// CSV 파일을 줄 단위로 읽기 (BOM 처리 포함, FileShare 안전).
    /// File.ReadAllLines와 동일한 newline 분할 방식.
    /// </summary>
    public static string[] ReadAllLinesShared(string filePath)
    {
        var text = ReadAllTextShared(filePath);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text.Substring(1);
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    }

    /// <summary>
    /// Export 완료 후 사용자에게 "파일을 여시겠습니까?" 묻고 Yes일 경우 기본 연결 프로그램으로 연다.
    /// </summary>
    public static void PromptOpenAfterExport(string filePath, int? itemCount = null, string title = "CSV 내보내기")
    {
        var countText = itemCount.HasValue ? $": {itemCount}건" : "";
        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"내보내기 완료{countText}\n\n파일: {Path.GetFileName(filePath)}\n\n파일을 여시겠습니까?",
            title, MessageBoxButton.YesNo, "✓");

        if (openResult != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"파일을 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
        }
    }

    /// <summary>
    /// Export 완료 후 상세 summary와 함께 "파일을 여시겠습니까?" 묻고 Yes일 경우 기본 연결 프로그램으로 연다.
    /// </summary>
    public static void PromptOpenAfterExportWithSummary(string filePath, string summary, string title = "CSV 내보내기")
    {
        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"{summary}\n\n파일을 여시겠습니까?",
            title, MessageBoxButton.YesNo, "✓");

        if (openResult != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"파일을 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
        }
    }

    /// <summary>
    /// Export 완료 후 사용자에게 "폴더를 여시겠습니까?" 묻고 Yes일 경우 탐색기를 연다.
    /// (다중 파일 export용)
    /// </summary>
    public static void PromptOpenFolderAfterExport(string directory, string summary, string title = "CSV 내보내기")
    {
        var openResult = DialogHelpers.ShowThemedMessageBox(
            $"{summary}\n\n폴더를 여시겠습니까?",
            title, MessageBoxButton.YesNo, "✓");

        if (openResult != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"폴더를 열 수 없습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, "⚠");
        }
    }

    /// <summary>
    /// Import 실패 메시지를 일관된 형식으로 표시.
    /// </summary>
    public static void ShowImportError(string detail, string title = "CSV Import 오류")
    {
        DialogHelpers.ShowThemedMessageBox(
            $"CSV 가져오기 실패:\n\n{detail}",
            title, MessageBoxButton.OK, "⚠");
    }
}
