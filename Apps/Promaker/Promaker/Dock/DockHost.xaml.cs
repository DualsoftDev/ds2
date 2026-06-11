using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// PR-A3 — anchorable ContentId → 등록 시점의 default <see cref="LayoutAnchorablePane"/> 매핑.
    /// ResetToDefaultLayout 가 anchorable 을 본 매핑의 pane 으로 이동 (인스턴스 재사용 → DataContext 보존).
    /// </summary>
    private readonly Dictionary<string, LayoutAnchorablePane> _defaultAnchorablePanes = new(StringComparer.Ordinal);

    /// <summary>
    /// PR-A4 — anchorable ContentId → 등록 시점의 <see cref="DockAnchorPosition"/>.
    /// ResetToDefaultLayout 가 layout 트리 재생성 후 본 position 으로 anchor 를 재배치.
    /// (사용자가 anchor 를 옮겨 default pane 이 garbage-collect 된 경우에도 position-based 재배치로 복원 가능.)
    /// </summary>
    private readonly Dictionary<string, DockAnchorPosition> _defaultPositions = new(StringComparer.Ordinal);

    /// <summary>
    /// PR-A3 — RegisterDocument 호출 순서대로 보관. ResetToDefaultLayout 가 동일 순서로 <see cref="_documentPane"/> 에
    /// 재배치. (anchorable 의 default pane 매핑은 <see cref="_defaultAnchorablePanes"/> 가 담당.)
    /// </summary>
    private readonly List<LayoutDocument> _defaultDocumentOrder = new();

    /// <summary>
    /// PR-A3 — <see cref="CaptureDefaultLayout"/> 호출 1회용 가드 (idempotent).
    /// </summary>
    private bool _defaultCaptured;

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

    // IDockManager 인터페이스 계약 유지용 이벤트 — 현 구현은 코드 측 단방향 sync (VM → DockHost) 만 사용하므로
    // 본 이벤트는 raise 되지 않는다 (CS0067 의도적 잔존). X 버튼 → VM 역방향 sync 가 다시 필요해질 때 wiring.
#pragma warning disable CS0067
    public event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;
#pragma warning restore CS0067

    /// <summary>
    /// anchor caption 의 Help 버튼(?) click event. 매개 = ContentId 문자열 (AvalonDock type 외부 노출 0).
    /// IDockManager 계약 유지용 — 현 AvalonDock caption template 에 Help 버튼 미부착이라 raise 되지 않는다.
    /// </summary>
#pragma warning disable CS0067
    public event EventHandler<string>? AnchorHelpRequested;
#pragma warning restore CS0067

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
            CanClose = false,  // X = 영구 제거 차단
            CanHide = false,   // X = 일시 hide 도 차단. visibility 는 코드 (SetAnchorVisible) 만.
            CanFloat = true,
            Content = anchor.Content,
        };

        pane.Children.Add(anchorable);
        _itemsByContentId[anchor.ContentId] = anchorable;
        _defaultAnchorablePanes[anchor.ContentId] = pane;
        _defaultPositions[anchor.ContentId] = anchor.DefaultPosition;
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
        _defaultDocumentOrder.Add(doc);
    }

    /// <summary>
    /// PR-A3 — anchorable/document 모두 parent pane.Children add/remove 로 visibility 토글.
    /// AvalonDock 의 Hide()/Show()/Close() 는 ctor 시점(layout 미측정) 문제 + document 의 Close() 가 destructive 한
    /// 비대칭 때문에 ResetToDefaultLayout 와 일관되게 단일 방식으로 통일.
    /// <list type="bullet">
    /// <item>visible=true: 현재 parent 가 null 이면 등록 시점 default pane 에 add.</item>
    /// <item>visible=false: 현재 parent 가 있으면 거기서 remove.</item>
    /// </list>
    /// </summary>
    public void SetAnchorVisible(string contentId, bool visible)
    {
        var item = FindLayoutItem(contentId);
        // "보임" = 라이브 트리에 붙어 있음. RestoreLayout 가 트리를 교체하면 등록 인스턴스가
        // 고아가 된 옛 트리에 Parent 를 가진 채 남을 수 있어 Parent != null 만으론 오판한다.
        bool wasVisible = IsInLiveLayout(item);
        if (wasVisible == visible) return;

        switch (item)
        {
            case LayoutAnchorable a:
                if (visible)
                {
                    // 고아 트리의 옛 parent 에 붙어 있으면 분리 후 라이브 pane 으로 (단일-parent 제약).
                    DetachFromParent(a);
                    // 라이브 트리에 없는 pane 이면 default position 에 pane 을 확보(검색/생성) 후 재매핑.
                    if (!_defaultAnchorablePanes.TryGetValue(contentId, out var pane) || !IsInLiveLayout(pane))
                    {
                        pane = EnsureLivePaneAt(_defaultPositions.TryGetValue(contentId, out var pos) ? pos : DockAnchorPosition.Left);
                        _defaultAnchorablePanes[contentId] = pane;
                    }
                    pane.Children.Add(a);
                }
                else
                {
                    if (a.Parent is LayoutAnchorablePane parent)
                        parent.Children.Remove(a);
                }
                break;

            case LayoutDocument d:
                if (visible)
                {
                    DetachFromParent(d);
                    LiveDocumentPane().Children.Add(d);
                }
                else
                {
                    if (d.Parent is LayoutDocumentPane parent)
                        parent.Children.Remove(d);
                }
                break;

            default:
                throw new InvalidOperationException($"Unsupported LayoutContent type for ContentId={contentId}: {item.GetType().Name}");
        }
    }

    /// <summary>요소가 현재 표시 중인 layout 트리(_dockManager.Layout)에 붙어 있는지.</summary>
    private bool IsInLiveLayout(ILayoutElement element) =>
        ReferenceEquals(element.Root, _dockManager.Layout);

    /// <summary>라이브 트리의 document pane — RestoreLayout 가 트리를 교체했으면 필드를 재캡처.</summary>
    private LayoutDocumentPane LiveDocumentPane()
    {
        if (!IsInLiveLayout(_documentPane))
        {
            var live = _dockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (live is not null)
                _documentPane = live;
        }
        return _documentPane;
    }

    /// <summary>
    /// 라이브 트리에서 해당 position 의 pane 을 확보. 같은 position 의 다른 anchorable 이 이미 라이브 pane 에
    /// 있으면 그 pane 을 재사용하고, 없으면 새 pane 을 만들어 default 자리(Left=루트 맨 앞, Right*=루트 맨 뒤
    /// 세로 그룹, Bottom=document 그룹이 속한 세로 패널 끝)에 삽입한다.
    /// </summary>
    private LayoutAnchorablePane EnsureLivePaneAt(DockAnchorPosition position)
    {
        // 1) 같은 position 의 형제가 이미 라이브 pane 에 있으면 재사용.
        foreach (var kv in _defaultPositions)
        {
            if (kv.Value != position) continue;
            if (_itemsByContentId.TryGetValue(kv.Key, out var sibling)
                && sibling.Parent is LayoutAnchorablePane siblingPane
                && IsInLiveLayout(siblingPane))
                return siblingPane;
        }

        var root = _dockManager.Layout;

        // 2) 새 pane 생성 후 default 위치에 삽입.
        switch (position)
        {
            case DockAnchorPosition.Left:
                {
                    var pane = new LayoutAnchorablePane { DockWidth = new GridLength(320) };
                    root.RootPanel.Children.Insert(0, pane);
                    _leftPane = pane;
                    return pane;
                }
            case DockAnchorPosition.Bottom:
                {
                    var pane = new LayoutAnchorablePane { DockHeight = new GridLength(200) };
                    // document 그룹이 속한 세로 패널 끝에 — XAML 박제 구조와 동일한 자리.
                    var docPane = LiveDocumentPane();
                    var centerPanel = docPane.FindParent<LayoutPanel>();
                    if (centerPanel is not null) centerPanel.Children.Add(pane);
                    else root.RootPanel.Children.Add(pane);
                    _bottomPane = pane;
                    return pane;
                }
            default:
                {
                    // Right* 3 단 — 라이브 트리에 우측 세로 그룹이 없으면 새로 만들어 루트 끝에 부착.
                    var group = root.RootPanel.Children.OfType<LayoutAnchorablePaneGroup>()
                        .LastOrDefault(g => g.Orientation == System.Windows.Controls.Orientation.Vertical);
                    if (group is null)
                    {
                        group = new LayoutAnchorablePaneGroup
                        {
                            Orientation = System.Windows.Controls.Orientation.Vertical,
                            DockWidth = new GridLength(280),
                        };
                        root.RootPanel.Children.Add(group);
                    }
                    var pane = new LayoutAnchorablePane();
                    if (position == DockAnchorPosition.RightMiddle) pane.DockHeight = new GridLength(220);
                    group.Children.Add(pane);
                    switch (position)
                    {
                        case DockAnchorPosition.RightTop: _rightTopPane = pane; break;
                        case DockAnchorPosition.RightMiddle: _rightMiddlePane = pane; break;
                        default: _rightBottomPane = pane; break;
                    }
                    return pane;
                }
        }
    }

    public bool IsAnchorVisible(string contentId) => IsInLiveLayout(FindLayoutItem(contentId));

    /// <summary>
    /// PR-A3 — default 상태 캡쳐. 본 구현은 XmlLayoutSerializer 사용 안 함 — serializer 가 deserialize 시 새
    /// LayoutAnchorable/LayoutDocument 인스턴스를 생성해 DataContext 흐름(MainViewModel binding)이 깨지는 문제 회피.
    /// 대신 RegisterAnchor / RegisterDocument 호출 시 이미 default pane / 순서를 dictionary/list 에 박제했으므로
    /// 본 메서드는 그 박제가 유효함을 표시하는 단순 가드.
    /// </summary>
    public void CaptureDefaultLayout()
    {
        _defaultCaptured = true;
    }

    /// <summary>
    /// PR-A4 — layout 트리 자체를 default 구조로 재생성. 사용자가 anchor 를 옮겨 빈 pane 이
    /// AvalonDock 에 의해 garbage-collect 된 경우에도 정상 복원.
    /// <list type="number">
    /// <item>모든 등록 anchorable/document 를 현재 parent 에서 분리.</item>
    /// <item>XAML 박제와 동일한 LayoutPanel/LayoutAnchorablePane(Group)/LayoutDocumentPane(Group) 트리 새로 생성.</item>
    /// <item>x:Name 필드 (_leftPane, _documentPane 등) + <see cref="_defaultAnchorablePanes"/> 매핑을 새 pane 으로 재설정.</item>
    /// <item><see cref="_dockManager"/>.Layout 을 새 LayoutRoot 로 교체.</item>
    /// <item>등록된 anchor/document 를 default position 의 새 pane 에 재배치 (인스턴스 재사용 → DataContext 보존).</item>
    /// </list>
    /// </summary>
    public void ResetToDefaultLayout()
    {
        if (!_defaultCaptured)
        {
            System.Diagnostics.Trace.TraceWarning("DockHost.ResetToDefaultLayout: default not captured (CaptureDefaultLayout 미호출).");
            return;
        }

        // 1단계 — 모든 등록 item 을 현재 parent 에서 분리 (단일-parent 제약 회피).
        foreach (var item in _itemsByContentId.Values)
            DetachFromParent(item);

        // 2단계 — XAML 박제와 동일한 layout 트리 재생성 (PR-A6: 하단 단일 pane).
        var newLeftPane         = new LayoutAnchorablePane { DockWidth = new GridLength(320) };
        var newDocumentPane     = new LayoutDocumentPane();
        var newBottomPane       = new LayoutAnchorablePane { DockHeight = new GridLength(200) };
        var newRightTopPane     = new LayoutAnchorablePane();
        var newRightMiddlePane  = new LayoutAnchorablePane { DockHeight = new GridLength(220) };
        var newRightBottomPane  = new LayoutAnchorablePane();

        var docGroup = new LayoutDocumentPaneGroup();
        docGroup.Children.Add(newDocumentPane);

        var centerPanel = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        centerPanel.Children.Add(docGroup);
        centerPanel.Children.Add(newBottomPane);

        var rightGroup = new LayoutAnchorablePaneGroup
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            DockWidth = new GridLength(280),
        };
        rightGroup.Children.Add(newRightTopPane);
        rightGroup.Children.Add(newRightMiddlePane);
        rightGroup.Children.Add(newRightBottomPane);

        var rootPanel = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        rootPanel.Children.Add(newLeftPane);
        rootPanel.Children.Add(centerPanel);
        rootPanel.Children.Add(rightGroup);

        // 3단계 — x:Name 필드 + default mapping 을 새 pane 으로 갱신.
        _leftPane         = newLeftPane;
        _documentPane     = newDocumentPane;
        _bottomPane       = newBottomPane;
        _rightTopPane     = newRightTopPane;
        _rightMiddlePane  = newRightMiddlePane;
        _rightBottomPane  = newRightBottomPane;

        foreach (var contentId in _defaultPositions.Keys.ToList())
            _defaultAnchorablePanes[contentId] = ResolveAnchorPane(_defaultPositions[contentId]);

        // 4단계 — DockingManager 에 새 LayoutRoot 설정 (visual 트리 즉시 갱신).
        _dockManager.Layout = new LayoutRoot { RootPanel = rootPanel };

        // 5단계 — 등록된 anchor/document 를 default 위치의 새 pane 에 재배치.
        foreach (var kvp in _defaultPositions)
        {
            if (_itemsByContentId.TryGetValue(kvp.Key, out var item) && item is LayoutAnchorable a)
                ResolveAnchorPane(kvp.Value).Children.Add(a);
        }
        foreach (var doc in _defaultDocumentOrder)
            _documentPane.Children.Add(doc);
    }

    /// <summary>
    /// PR-A4 — LayoutContent 를 현재 parent (LayoutAnchorablePane | LayoutDocumentPane) 에서 분리.
    /// parent 가 null 이거나 다른 type 이면 no-op (단일-parent 제약 회피 목적).
    /// </summary>
    private static void DetachFromParent(LayoutContent item)
    {
        switch (item)
        {
            case LayoutAnchorable a when a.Parent is LayoutAnchorablePane ap:
                ap.Children.Remove(a);
                break;
            case LayoutDocument d when d.Parent is LayoutDocumentPane dp:
                dp.Children.Remove(d);
                break;
        }
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

            // Deserialize 는 layout 트리를 **새 인스턴스들로 교체**한다 (callback 은 Content 만 재연결).
            // 내부 레지스트리가 교체 전 XAML 인스턴스(고아)를 계속 가리키면 이후 SetAnchorVisible 이
            // 화면에 안 보이는 객체만 조작하게 된다(Welcome 박제 버그) — 새 트리 기준으로 재캡처.
            RecaptureFromRestoredLayout();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"DockHost.RestoreLayout failed for '{filepath}': {ex.Message}");
        }
    }

    /// <summary>
    /// RestoreLayout 직후 — 복원된 라이브 트리에서 ContentId 로 항목을 다시 찾아
    /// <see cref="_itemsByContentId"/>/<see cref="_defaultAnchorablePanes"/>/pane 필드를 재캡처.
    /// XML 에 없던 항목(저장 당시 hidden)은 기존 인스턴스를 유지 — show 시
    /// <see cref="EnsureLivePaneAt"/> fallback 이 라이브 pane 을 확보한다.
    /// </summary>
    private void RecaptureFromRestoredLayout()
    {
        var root = _dockManager.Layout;

        var liveById = root.Descendents()
            .OfType<LayoutContent>()
            .Concat(root.Hidden)
            .Where(lc => !string.IsNullOrEmpty(lc.ContentId))
            .GroupBy(lc => lc.ContentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var contentId in _itemsByContentId.Keys.ToList())
        {
            if (!liveById.TryGetValue(contentId, out var live)) continue;

            var old = _itemsByContentId[contentId];
            _itemsByContentId[contentId] = live;

            // document 재배치 순서 보존용 리스트도 새 인스턴스로 교체.
            if (old is LayoutDocument oldDoc && live is LayoutDocument liveDoc)
            {
                var idx = _defaultDocumentOrder.IndexOf(oldDoc);
                if (idx >= 0) _defaultDocumentOrder[idx] = liveDoc;
            }

            // anchorable 이 라이브 pane 에 붙어 있으면 default pane 매핑 + 위치 필드 재캡처.
            if (live is LayoutAnchorable { Parent: LayoutAnchorablePane livePane })
            {
                _defaultAnchorablePanes[contentId] = livePane;
                if (_defaultPositions.TryGetValue(contentId, out var pos))
                {
                    switch (pos)
                    {
                        case DockAnchorPosition.Left: _leftPane = livePane; break;
                        case DockAnchorPosition.Bottom: _bottomPane = livePane; break;
                        case DockAnchorPosition.RightTop: _rightTopPane = livePane; break;
                        case DockAnchorPosition.RightMiddle: _rightMiddlePane = livePane; break;
                        case DockAnchorPosition.RightBottom: _rightBottomPane = livePane; break;
                    }
                }
            }
        }

        // document pane 필드도 라이브 트리 기준으로.
        var liveDocPane = root.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (liveDocPane is not null)
            _documentPane = liveDocPane;
    }

    /// <summary>
    /// <see cref="DockAnchorPosition"/> → XAML 박제 LayoutAnchorablePane 매핑.
    /// PR-A6 — Bottom 단일화 (Log / Gantt / StatusMonitor tabbed).
    ///   Left=Explorer / Bottom=Log·Gantt·StatusMonitor / RightTop=Properties / RightMiddle=History / RightBottom=LlmChat.
    /// </summary>
    private LayoutAnchorablePane ResolveAnchorPane(DockAnchorPosition position) => position switch
    {
        DockAnchorPosition.Left => _leftPane,
        DockAnchorPosition.Bottom => _bottomPane,
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

    // PR-A4 — Help 뱃지(?) 전면 제거. 단일 '도움말' 버튼이 리본 우상단에 추가되어 anchor 별 뱃지 불필요.
    // WrapWithHelp / HelpButton_Click / AnchorHelpRequested 발화 코드 모두 삭제. DockAnchor.HasHelp 는 호환성 위해
    // record 시그니처 유지하되 RegisterAnchor 가 무시.
}
