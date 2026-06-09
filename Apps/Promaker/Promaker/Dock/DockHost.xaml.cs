using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using AvalonDock.Themes;

namespace Promaker.Dock;

/// <summary>
/// AvalonDock <c>DockingManager</c> 를 캡슐화하는 UserControl. 외부 노출 API = <see cref="IDockManager"/>.
/// <para>
/// PR-A1 (DX 제거 + AvalonDock 복귀): DevExpress.Wpf.Docking 의 모든 type 을 AvalonDock 으로 swap.
/// 외부 API (IDockManager / DockAnchor / DockAnchorPosition) 표면은 동일 — Promaker 본체 (App.xaml.cs /
/// MainWindow.xaml.cs / MainToolbarEtcContent.xaml) 의 호출 측 코드는 변경 0.
/// </para>
/// </summary>
public partial class DockHost : UserControl, IDockManager
{
    /// <summary>
    /// ContentId → 등록된 <see cref="LayoutContent"/> 매핑. (LayoutAnchorable | LayoutDocument 공통 부모).
    /// AvalonDock layout 트리 안의 자식 탐색은 깊이가 있어 dictionary 가 더 단순/안정.
    /// </summary>
    private readonly Dictionary<string, LayoutContent> _itemsByContentId = new(StringComparer.Ordinal);

    /// <summary>
    /// SetTheme 정적 helper 가 dock manager Theme 를 교체할 수 있도록 최신 인스턴스 참조 보관.
    /// Promaker 는 단일 MainWindow 모델 — 다중 DockHost 미가정.
    /// </summary>
    private static DockHost? _latest;

    /// <summary>
    /// SetTheme 가 DockHost 생성 이전에 호출된 경우(App.OnStartup 의 초기 theme 적용 시점), pending 값을 보관해
    /// ctor 에서 적용. DockHost 생성 이후 호출은 즉시 적용.
    /// </summary>
    private static bool? _pendingDark;

    /// <summary>
    /// PR-A1 — Promaker 라이트/다크 테마 연동. App startup 의 ThemeManager 초기화 시점 + ThemeChanged 이벤트에서 호출.
    /// AvalonDock 의 <see cref="DockingManager.Theme"/> 를 VS2013 Light/Dark Theme 로 교체.
    /// (Theme 객체는 ResourceDictionary 를 노출하는 wrapper — 교체 시 dock chrome 즉시 재적용.)
    /// </summary>
    public static void SetTheme(bool dark)
    {
        if (_latest?._dockManager is { } mgr)
        {
            mgr.Theme = dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
        }
        else
        {
            _pendingDark = dark;
        }
    }

    /// <summary>
    /// PR-A1 — DX 시절 NetCore HintPath 어셈블리 resolve hook 용. AvalonDock 은 일반 NuGet 어셈블리이므로
    /// 동적 load 가 필요 없음 — 외부 호출 호환을 위해 method 만 남기고 no-op. App.xaml.cs 의 기존 호출은 그대로 유효.
    /// </summary>
    public static void RegisterAssemblyResolve()
    {
        // intentionally empty — AvalonDock 은 표준 NuGet 어셈블리, AppDomain.AssemblyResolve hook 불필요.
    }

    public DockHost()
    {
        InitializeComponent();
        _latest = this;
        // App.OnStartup 의 초기 SetTheme 가 DockHost 생성 이전에 호출됐으면 그 시점 값 적용.
        if (_pendingDark is bool dark)
        {
            _dockManager.Theme = dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
            _pendingDark = null;
        }
    }

    public event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;

    /// <summary>
    /// anchor caption 의 Help 버튼(?) click event. 매개 = ContentId 문자열 (AvalonDock type 외부 노출 0).
    /// </summary>
    public event EventHandler<string>? AnchorHelpRequested;

    public void RegisterAnchor(DockAnchor anchor)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (anchor.DefaultPosition == DockAnchorPosition.Document)
            throw new ArgumentException(
                $"DockAnchorPosition.Document is for RegisterDocument(), not RegisterAnchor(). ContentId={anchor.ContentId}",
                nameof(anchor));
        if (_itemsByContentId.ContainsKey(anchor.ContentId))
            throw new InvalidOperationException($"ContentId '{anchor.ContentId}' is already registered.");

        var pane = ResolveAnchorPane(anchor.DefaultPosition);
        var anchorable = new LayoutAnchorable
        {
            ContentId = anchor.ContentId,
            Title = anchor.Title,
            CanClose = false,  // 사용자 X 버튼으로 영구 제거 차단 — Hide 만 허용 (보기 메뉴로 복원).
            CanHide = true,
            Content = anchor.HasHelp ? WrapWithHelp(anchor.Content, anchor.ContentId) : anchor.Content,
        };

        // visibility 통보 — AvalonDock 은 IsHiddenChanged 이벤트가 없고, LayoutContent 가 INotifyPropertyChanged 를
        // 구현해 IsHidden 변경 시 PropertyChanged 발화. PropertyName="IsHidden" 인 경우만 통보 (DX ItemIsVisibleChanged 와 동등).
        anchorable.PropertyChanged += OnAnchorablePropertyChanged;

        pane.Children.Add(anchorable);
        _itemsByContentId[anchor.ContentId] = anchorable;
    }

    public void RegisterDocument(DockAnchor document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (document.DefaultPosition != DockAnchorPosition.Document)
            throw new ArgumentException(
                $"RegisterDocument() expects DockAnchorPosition.Document; got {document.DefaultPosition}. " +
                $"Use RegisterAnchor() for non-document anchors. ContentId={document.ContentId}",
                nameof(document));
        if (_itemsByContentId.ContainsKey(document.ContentId))
            throw new InvalidOperationException($"ContentId '{document.ContentId}' is already registered.");

        var doc = new LayoutDocument
        {
            ContentId = document.ContentId,
            Title = document.Title,
            CanClose = false,  // Welcome/Canvas 는 영구 제거 차단 — visibility 토글만 (MainWindow 가 HasProject 동기화).
            CanFloat = true,
            Content = document.Content,
        };
        _documentPane.Children.Add(doc);
        _itemsByContentId[document.ContentId] = doc;
    }

    /// <summary>
    /// AvalonDock 의 visibility 모델은 anchorable / document 가 서로 다름:
    /// - <see cref="LayoutAnchorable.Hide"/> / <see cref="LayoutAnchorable.Show"/> — IsHidden 토글.
    /// - <see cref="LayoutDocument"/> 는 IsHidden 이 직접 존재 — 단순 set.
    /// </summary>
    public void SetAnchorVisible(string contentId, bool visible)
    {
        var item = FindLayoutItem(contentId);
        switch (item)
        {
            case LayoutAnchorable a:
                if (visible) a.Show(); else a.Hide();
                break;
            case LayoutDocument d:
                // LayoutDocument 는 Hide()/Show() 가 별도로 없어 IsVisible 을 통해 통제 (보기 메뉴에서 직접 사용 케이스 없음).
                // Welcome/Canvas 의 HasProject 동기화는 MainWindow 가 본 메서드를 호출 — 단순 IsVisible bool 매핑.
                if (!visible) d.Close(); else { /* Closed document 의 재오픈은 별도 흐름 — 본 메서드 scope 외 */ }
                break;
            default:
                throw new InvalidOperationException($"Unsupported LayoutContent type for ContentId={contentId}: {item.GetType().Name}");
        }
    }

    public bool IsAnchorVisible(string contentId)
    {
        var item = FindLayoutItem(contentId);
        return item switch
        {
            LayoutAnchorable a => !a.IsHidden,
            LayoutDocument d => d.IsVisible,
            _ => false,
        };
    }

    /// <summary>
    /// AvalonDock <see cref="XmlLayoutSerializer"/> wrapping. ContentId 매칭으로 기존 LayoutContent 인스턴스 재연결.
    /// 상위 디렉토리 미존재 시 생성.
    /// </summary>
    public void SaveLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        var dir = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var serializer = new XmlLayoutSerializer(_dockManager);
        using var fs = new FileStream(filepath, FileMode.Create, FileAccess.Write);
        serializer.Serialize(fs);
    }

    /// <summary>
    /// AvalonDock <see cref="XmlLayoutSerializer"/> 로 layout 복원. 파일 미존재 시 default layout 유지.
    /// parse / restore 실패 시에도 default 유지 (fail-safe — 외부 환경 예외).
    /// LayoutSerializationCallback 에서 ContentId 매칭으로 기존 등록된 Content 재연결.
    /// </summary>
    public void RestoreLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        if (!File.Exists(filepath)) return;

        try
        {
            var serializer = new XmlLayoutSerializer(_dockManager);
            serializer.LayoutSerializationCallback += (s, e) =>
            {
                // XML 안의 ContentId 와 매핑된 기존 인스턴스의 Content / 이벤트 hookup 을 그대로 재사용.
                if (e.Model is LayoutContent lc &&
                    !string.IsNullOrEmpty(lc.ContentId) &&
                    _itemsByContentId.TryGetValue(lc.ContentId, out var existing))
                {
                    e.Content = existing.Content;
                }
                else
                {
                    // 등록되지 않은 ContentId 는 skip (외부 환경 예외 — 구버전 layout xml 잔재 가능).
                    e.Cancel = true;
                }
            };
            using var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read);
            serializer.Deserialize(fs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"DockHost.RestoreLayout failed for '{filepath}': {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="DockAnchorPosition"/> → XAML 박제 LayoutAnchorablePane 매핑.
    /// post-D8 fix (Log 독립 anchor 승격) 매핑:
    ///   Left=explorer / BottomLeft=simulation / BottomRight=log / RightTop=property / RightMiddle=history / RightBottom=llmchat.
    /// </summary>
    private LayoutAnchorablePane ResolveAnchorPane(DockAnchorPosition position) => position switch
    {
        DockAnchorPosition.Left => _leftPane,
        DockAnchorPosition.BottomLeft => _bottomLeftPane,
        DockAnchorPosition.BottomRight => _bottomRightPane,
        DockAnchorPosition.RightTop => _rightTopPane,
        DockAnchorPosition.RightMiddle => _rightMiddlePane,
        DockAnchorPosition.RightBottom => _rightBottomPane,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unsupported anchor position."),
    };

    private LayoutContent FindLayoutItem(string contentId)
    {
        if (string.IsNullOrEmpty(contentId)) throw new ArgumentException("contentId is required.", nameof(contentId));
        if (!_itemsByContentId.TryGetValue(contentId, out var item))
            throw new InvalidOperationException($"DockAnchor not registered: ContentId={contentId}");
        return item;
    }

    private void OnAnchorablePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LayoutAnchorable.IsHidden)) return;
        if (sender is LayoutAnchorable a && !string.IsNullOrEmpty(a.ContentId))
        {
            AnchorVisibilityChanged?.Invoke(this, new DockAnchorVisibilityChangedEventArgs(a.ContentId, !a.IsHidden));
        }
    }

    /// <summary>
    /// PR-A1 — Help 뱃지 우회 구현. anchor content 상단에 얇은 20px 의 Help bar 를 Grid 합성으로 wrap.
    /// AvalonDock 의 AnchorableHeaderTemplate 은 tab caption 으로 합쳐졌을 때 binding 한계가 있고, Help 동작이
    /// Promaker.Help 의존을 끌어와 격리 원칙(§7 #4)을 깨므로 본 PR 에서는 content body 안 상단 bar 방식으로 박제.
    /// click 시 <see cref="AnchorHelpRequested"/> 발화 — MainWindow 가 HelpNavigator.NavigateCommand 로 hook.
    /// </summary>
    private FrameworkElement WrapWithHelp(FrameworkElement content, string contentId)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var helpBar = new Border
        {
            Height = 20,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var helpBtn = new Button
        {
            Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "도움말",
            Content = new TextBlock
            {
                Text = "?",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Tag = contentId,
        };
        // DynamicResource 로 Promaker AccentBrush 적용 — Promaker.Dock 격리상 직접 Style 참조 불가, runtime resource lookup 만.
        // (Application.Resources 까지 도달 — 미발견 시 Button 기본 chrome).
        helpBtn.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        helpBtn.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
        helpBtn.Click += HelpButton_Click;

        helpBar.Child = helpBtn;
        Grid.SetRow(helpBar, 0);
        grid.Children.Add(helpBar);

        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        return grid;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string contentId && !string.IsNullOrEmpty(contentId))
        {
            AnchorHelpRequested?.Invoke(this, contentId);
            e.Handled = true;
        }
    }
}
