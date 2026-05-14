using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using log4net;

namespace Promaker.Services;

/// <summary>
/// Monitoring 모드 + IsRealPlcConnected 시 메인 윈도우를 트레이로 전환하는 서비스.
/// 단일 윈도우 + 단일 트레이 아이콘만 관리. STOP / 모드 전환 시 자동으로 윈도우 복원하고 트레이 제거.
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("TrayService");

    private TaskbarIcon? _icon;
    private Window? _ownerWindow;
    private bool _disposed;

    /// <summary>현재 트레이 모드 활성 여부.</summary>
    public bool IsActive => _icon is not null;

    /// <summary>"종료" 컨텍스트 메뉴 클릭 시 호출. 호출자가 정리 + 앱 종료를 결정.</summary>
    public event Action? ExitRequested;

    /// <summary>"STOP" 컨텍스트 메뉴 클릭 시 호출. 호출자가 시뮬 정지 + 트레이 제거를 결정.</summary>
    public event Action? StopRequested;

    /// <summary>"열기" 메뉴 또는 더블클릭 시 호출. 기본 동작(윈도우 복원) 은 RestoreWindow 가 처리.</summary>
    public event Action? OpenRequested;

    /// <summary>
    /// 트레이로 전환. 윈도우를 숨기고 NotifyIcon 표시.
    /// 이미 활성 상태면 no-op.
    /// </summary>
    public void HideToTray(Window window, string tooltip)
    {
        if (_disposed) return;
        if (_icon is not null) return; // 이미 트레이 모드

        _ownerWindow = window;

        var icon = new TaskbarIcon
        {
            ToolTipText = tooltip,
            Visibility = Visibility.Visible,
        };

        // 아이콘 로드 — IconSource (ImageSource) 가 WPF-native, System.Drawing 우회.
        try
        {
            var uri = new Uri("pack://application:,,,/Promaker;component/Assets/Promaker.ico", UriKind.Absolute);
            icon.IconSource = new BitmapImage(uri);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon load failed: {ex.Message}");
        }

        // 컨텍스트 메뉴: 열기 / STOP / 종료
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "열기" };
        openItem.Click += (_, _) => OnOpen();
        var stopItem = new MenuItem { Header = "STOP (시뮬레이션 정지)" };
        stopItem.Click += (_, _) => StopRequested?.Invoke();
        var exitItem = new MenuItem { Header = "종료" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(stopItem);
        menu.Items.Add(exitItem);
        icon.ContextMenu = menu;

        // 더블클릭 = 열기 (좌클릭 단발은 무시 — context menu 와 충돌 방지).
        icon.TrayMouseDoubleClick += (_, _) => OnOpen();

        // Win32 NotifyIcon 등록 강제 — XAML 없이 코드로 만들 때 필수.
        // 이게 누락되면 visual tree 렌더 패스가 돌지 않는 한 트레이에 안 나타남.
        try { icon.ForceCreate(); }
        catch (Exception ex) { Log.Warn($"TaskbarIcon.ForceCreate failed: {ex.Message}"); }

        _icon = icon;

        // 아이콘 등록 완료 후 메인 윈도우 숨김 — 순서 중요. 윈도우 살아있는 동안 등록되어야 함.
        window.Hide();
        window.ShowInTaskbar = false;

        Log.Info($"Tray icon shown — {tooltip}");
    }

    /// <summary>윈도우 복원 + 트레이 아이콘 제거. 트레이 아닌 상태면 no-op.</summary>
    public void RestoreWindow()
    {
        if (_icon is null) return;

        try
        {
            _icon.Visibility = Visibility.Collapsed;
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon dispose: {ex.Message}");
        }
        _icon = null;

        if (_ownerWindow is { } win)
        {
            win.ShowInTaskbar = true;
            win.Show();
            if (win.WindowState == WindowState.Minimized)
                win.WindowState = WindowState.Normal;
            win.Activate();
        }

        Log.Info("Tray icon removed, window restored");
    }

    private void OnOpen()
    {
        OpenRequested?.Invoke();
        RestoreWindow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _icon?.Dispose(); } catch { /* ignore */ }
        _icon = null;
        _ownerWindow = null;
    }
}
