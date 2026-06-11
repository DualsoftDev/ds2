using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using log4net;
using log4net.Appender;
using Promaker.Presentation;
using Promaker.ViewModels.Logging;

namespace Promaker.Controls.Logging;

/// <summary>
/// 앱 전역 log4net 출력 view. DataContext 는 ctor 에서 AppLogState.Instance 로 직접 set
/// — 호출처가 DataContext 를 따로 주입할 필요 없음 (singleton).
/// 이전 (`SimulationPanel.xaml` 의 Log tab) 에서 별도 독립 dock pane (logAnchor) 으로 분리됨.
/// </summary>
public partial class AppLogView : UserControl
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AppLogView));

    // 꼬리 따라가기 — 맨 아래에 있을 때만 새 로그를 따라 스크롤.
    // 사용자가 위로 올리면(과거 조회) 해제, 다시 맨 아래로 내리면 재개.
    private ScrollViewer? _scroll;
    private bool _followTail = true;

    public AppLogView()
    {
        InitializeComponent();
        DataContext = AppLogState.Instance;
        Loaded += (_, _) =>
        {
            if (_scroll is not null) return;
            _scroll = FindScrollViewer(AppLogListBox);
            if (_scroll is not null)
                _scroll.ScrollChanged += OnLogScrollChanged;
        };
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        if (e.ExtentHeightChange == 0)
        {
            // 콘텐츠 변화 없는 스크롤 = 사용자 조작 — 맨 아래 근처인지로 follow 여부 갱신.
            _followTail = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1.0;
        }
        else if (_followTail)
        {
            _scroll.ScrollToEnd();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    private void AppLogCopyAll_Click(object sender, RoutedEventArgs e)
        => ClipboardUtil.Copy(AppLogListBox.Items);

    private void AppLogCopySelected_Click(object sender, RoutedEventArgs e)
        => ClipboardUtil.Copy(AppLogListBox.SelectedItems.Count > 0
            ? AppLogListBox.SelectedItems
            : AppLogListBox.Items);

    /// <summary>현재 활성 로그 파일을 탐색기에서 선택한 채 폴더 열기.
    /// 경로는 log4net RollingFileAppender.File (절대경로) 에서 직접 취득 — config 의 상대경로가
    /// 어떤 작업 디렉터리로 해석됐든 실제 위치를 그대로 따라간다.</summary>
    private void AppLogOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = LogManager.GetRepository(typeof(AppLogView).Assembly)
            .GetAppenders()
            .OfType<RollingFileAppender>()
            .FirstOrDefault()?.File;

        if (string.IsNullOrEmpty(file))
        {
            Log.Warn("로그 위치 열기 실패 — RollingFileAppender.File 미해석.");
            return;
        }

        try
        {
            // 파일이 이미 있으면 선택한 채로, 없으면 폴더만 연다.
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                else
                    Log.Warn($"로그 위치 열기 실패 — 경로 없음: {file}");
            }
        }
        catch (Exception ex)
        {
            // explorer 실행은 외부 환경 의존 — 실패해도 앱 동작엔 영향 없음 (boundary catch, log 만 남김).
            Log.Warn($"로그 위치 열기 실패: {file}", ex);
        }
    }

    private void AppLogClear_Click(object sender, RoutedEventArgs e)
        => AppLogState.Instance.Clear();
}
