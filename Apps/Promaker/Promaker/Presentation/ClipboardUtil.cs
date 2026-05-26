using System;
using System.Collections;
using System.Text;
using System.Windows;
using log4net;

namespace Promaker.Presentation;

/// <summary>
/// 여러 ListBox/DataGrid 등에서 공통으로 사용하는 clipboard copy helper.
/// 이전: `SimulationPanel.xaml.cs` 와 `AppLogView.xaml.cs` 에 동일 본문 중복 — 본 helper 로 통합.
/// </summary>
internal static class ClipboardUtil
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ClipboardUtil));

    /// <summary>items 의 각 항목을 ToString() + newline 으로 이어 클립보드에 기록. 빈 컬렉션은 no-op.</summary>
    public static void Copy(IList items)
    {
        if (items.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var item in items)
            sb.AppendLine(item?.ToString() ?? "");
        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            // 외부 환경 (다른 프로세스의 clipboard lock 등) — log4net 으로 흔적만 남기고 진행.
            Log.Warn($"클립보드 접근 실패: {ex.Message}");
        }
    }
}
