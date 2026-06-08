using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using log4net;

namespace Promaker.Services;

internal sealed record ExcelComPdfConversion(string PdfPath, bool KeepForDebug);

internal static class ExcelComPdfConverter
{
    private const int XlTypePdf = 0;
    private const int XlQualityStandard = 0;
    private const int MsoAutomationSecurityForceDisable = 3;
    private const int ConversionTimeoutMs = 180_000;

#if DEBUG
    private const bool KeepConvertedPdf = true;
#else
    private const bool KeepConvertedPdf = false;
#endif

    private static readonly ILog Log = LogManager.GetLogger(typeof(ExcelComPdfConverter));

    public static ExcelComPdfConversion Convert(string xlsxPath)
    {
        ExcelComPdfConversion? result = null;
        Exception? error = null;
        var excelProcessId = 0;
        var abandoned = 0;
        var resultLock = new object();
        var thread = new Thread(() =>
        {
            try
            {
                var conversion = ConvertCore(
                    xlsxPath,
                    pid => Volatile.Write(ref excelProcessId, pid),
                    () => Volatile.Read(ref abandoned) != 0);
                lock (resultLock)
                {
                    if (Volatile.Read(ref abandoned) != 0)
                    {
                        DeleteIfTemporary(conversion);
                        return;
                    }
                    result = conversion;
                }
            }
            catch (Exception ex)
            {
                lock (resultLock)
                {
                    if (Volatile.Read(ref abandoned) == 0)
                        error = ex;
                }
            }
        });

        thread.Name = "Promaker Excel COM PDF Converter";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(ConversionTimeoutMs))
        {
            lock (resultLock)
            {
                Volatile.Write(ref abandoned, 1);
                if (result is not null)
                {
                    DeleteIfTemporary(result);
                    result = null;
                }
            }
            var pid = Volatile.Read(ref excelProcessId);
            if (pid != 0)
                KillExcelProcessBestEffort(pid);
            throw new TimeoutException($"Excel PDF 변환 제한 시간 {ConversionTimeoutMs / 1000}초를 초과했습니다.");
        }

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();

        return result ?? throw new InvalidOperationException("Excel PDF 변환 결과가 비어 있습니다.");
    }

    public static void DeleteIfTemporary(ExcelComPdfConversion conversion)
    {
        if (conversion.KeepForDebug) return;

        try
        {
            if (File.Exists(conversion.PdfPath))
                File.Delete(conversion.PdfPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"xlsx 변환 임시 PDF 삭제 실패: {conversion.PdfPath}", ex);
        }
    }

    private static ExcelComPdfConversion ConvertCore(
        string xlsxPath,
        Action<int> onExcelProcessId,
        Func<bool> isAbandoned)
    {
        if (string.IsNullOrWhiteSpace(xlsxPath))
            throw new ArgumentException("xlsx path 필수.", nameof(xlsxPath));
        if (!File.Exists(xlsxPath))
            throw new FileNotFoundException("xlsx 파일을 찾을 수 없습니다.", xlsxPath);

        var excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType is null)
            throw new InvalidOperationException("Microsoft Excel COM 등록을 찾을 수 없습니다. Excel 설치가 필요합니다.");

        var pdfPath = CreatePdfPath(xlsxPath);
        object? excelObj = null;
        object? workbooksObj = null;
        object? workbookObj = null;
        var success = false;

        try
        {
            excelObj = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel.Application 생성 실패.");

            dynamic excel = excelObj;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.AskToUpdateLinks = false;
            excel.EnableEvents = false;
            excel.ScreenUpdating = false;
            excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
            CaptureExcelProcessId(excel, onExcelProcessId);

            workbooksObj = excel.Workbooks;
            dynamic workbooks = workbooksObj;
            workbookObj = workbooks.Open(
                xlsxPath,
                0,
                true,
                Type.Missing,
                Type.Missing,
                Type.Missing,
                true,
                Type.Missing,
                Type.Missing,
                false,
                false,
                Type.Missing,
                false,
                Type.Missing,
                Type.Missing);

            dynamic workbook = workbookObj;
            workbook.ExportAsFixedFormat(
                XlTypePdf,
                pdfPath,
                XlQualityStandard,
                true,
                false,
                Type.Missing,
                Type.Missing,
                false,
                Type.Missing);

            var info = new FileInfo(pdfPath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("Excel PDF 변환 결과 파일이 생성되지 않았습니다.");

            if (isAbandoned())
            {
#if DEBUG
                Log.Info($"DEBUG timeout 이후 완료된 xlsx 변환 PDF 유지: {pdfPath}");
#else
                DeleteFileBestEffort(pdfPath);
#endif
                throw new TimeoutException("Excel PDF 변환이 timeout 이후 완료되어 결과를 폐기했습니다.");
            }

            if (KeepConvertedPdf)
                Log.Info($"DEBUG xlsx 변환 PDF 유지: {pdfPath}");

            success = true;
            return new ExcelComPdfConversion(pdfPath, KeepConvertedPdf);
        }
        finally
        {
            CloseWorkbook(workbookObj);
            QuitExcel(excelObj);
            ReleaseCom(workbookObj);
            ReleaseCom(workbooksObj);
            ReleaseCom(excelObj);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if ((!success || isAbandoned()) && !KeepConvertedPdf)
                DeleteFileBestEffort(pdfPath);
        }
    }

    private static string CreatePdfPath(string xlsxPath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Promaker.LlmAgent",
            KeepConvertedPdf ? "xlsx-pdf-debug" : "xlsx-pdf");
        Directory.CreateDirectory(root);

        var stem = SanitizeFileStem(Path.GetFileNameWithoutExtension(xlsxPath));
        var filename = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}-{stem}.pdf";
        return Path.Combine(root, filename);
    }

    private static string SanitizeFileStem(string value)
    {
        var chars = value.ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var sanitized = new string(chars).Trim();
        if (sanitized.Length == 0) return "workbook";
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private static void CloseWorkbook(object? workbookObj)
    {
        if (workbookObj is null) return;
        try
        {
            dynamic workbook = workbookObj;
            workbook.Close(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Excel workbook close 실패", ex);
        }
    }

    private static void QuitExcel(object? excelObj)
    {
        if (excelObj is null) return;
        try
        {
            dynamic excel = excelObj;
            excel.Quit();
        }
        catch (Exception ex)
        {
            Log.Warn("Excel quit 실패", ex);
        }
    }

    private static void ReleaseCom(object? comObj)
    {
        if (comObj is null) return;
        try
        {
            if (Marshal.IsComObject(comObj))
                Marshal.FinalReleaseComObject(comObj);
        }
        catch (Exception ex)
        {
            Log.Warn("Excel COM release 실패", ex);
        }
    }

    private static void CaptureExcelProcessId(dynamic excel, Action<int> onExcelProcessId)
    {
        var hwnd = new IntPtr((int)excel.Hwnd);
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId != 0)
            onExcelProcessId(processId);
    }

    private static void KillExcelProcessBestEffort(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"timeout 된 Excel 변환 프로세스 종료 실패: PID={processId}", ex);
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"xlsx 변환 실패 partial PDF 삭제 실패: {path}", ex);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
