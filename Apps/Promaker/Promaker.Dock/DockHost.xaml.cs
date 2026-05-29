using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Docking.Base;

namespace Promaker.Dock;

/// <summary>
/// DevExpress DockLayoutManager 을 캡슐화하는 UserControl. 외부 노출 API = <see cref="IDockManager"/>.
/// DX type 은 본 클래스 안에서만 사용 — Promaker.csproj 의 PrivateAssets="all" ProjectReference 와 함께
/// DX 의 System.Windows.Forms / System.Drawing transitive 가 Promaker 본체에 유입되지 않도록 격리.
///
/// PR-D3~D6 단계:
///   - done-dock-devexpress.md §3 PR-D3 의 layout 설계 (Welcome/Canvas 통합 DocumentGroup) 그대로 LayoutGroup 트리 구성 (XAML).
///   - IDockManager 의 6 메서드 (RegisterAnchor / RegisterDocument / SetAnchorVisible / IsAnchorVisible /
///     SaveLayout / RestoreLayout) + 1 event (AnchorVisibilityChanged) 구현. dispatch 는
///     <see cref="DockAnchorPosition"/> switch.
///   - size 보존 / drag-drop / floating 은 DX native 처리 — 별도 보정 코드 없음 (작업 의도 verbatim).
/// </summary>
public partial class DockHost : UserControl, IDockManager
{
    /// <summary>
    /// ContentId → 등록된 BaseLayoutItem (LayoutPanel 또는 DocumentPanel) 매핑.
    /// LayoutGroup.Items[string] 은 직접 자식만 lookup — 트리 전역은 자체 dictionary 가 더 단순/안정.
    /// </summary>
    private readonly Dictionary<string, BaseLayoutItem> _itemsByContentId = new(StringComparer.Ordinal);

    /// <summary>
    /// PR-D7.3 — DX skin 정합. App startup 시점 (Application 인스턴스 생성 직후, MainWindow 생성 전) 1회 호출.
    /// 격리 원칙 (§7 #4): DX type 외부 노출 차단 — Promaker 본체가 DX API 를 직접 호출하지 않도록
    /// 본 정적 helper 가 단일 진입점. 매개변수 없음 — skin 이름은 deploy 된 Themes assembly 와 정합되는
    /// "Office2019Black" 으로 하드코딩 (PR-D7.3 fix: Promaker dark theme 색차 시각 검수 결과 Office2019Colorful
    /// (light) 부적합 → Office2019Black 채택. DevExpress.Xpf.Themes.Office2019Black.v24.1.dll 1종만 deploy).
    /// 다른 dark skin (Office2019DarkGray / VS2019Dark / Win11Dark 등) 은 별도 Themes assembly 추가 deploy 필요.
    /// </summary>
    public static void InitializeTheme()
    {
        ApplicationThemeHelper.ApplicationThemeName = "Office2019Black";
    }

    /// <summary>
    /// PR-D7.3 fix — mechanism D (Reference HintPath) 의 NetCore dll 이 .NET 9 AssemblyLoadContext 에
    /// strong-named entry 로 등록되지 않아 DX 의 동적 Themes assembly load (Themes.Office2019Colorful 등)
    /// 가 FileNotFoundException 으로 실패. AppDomain.AssemblyResolve hook 으로 bin 폴더의 DevExpress.*.dll
    /// 을 명시 로드. 본 메서드는 App startup 의 가장 이른 시점 (모든 DX type 참조 이전) 1회 호출 필수.
    /// 본 메서드 body 에 DX type 참조 0건 — 안전한 정적 helper.
    /// </summary>
    public static void RegisterAssemblyResolve()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            if (name == null || !name.StartsWith("DevExpress.", StringComparison.Ordinal)) return null;
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }

    public DockHost()
    {
        InitializeComponent();
        ApplyDocumentGroupCaptionButtons();

        // PR-D5 — PR-D3 검열 M1 박제 처리: hook 시점을 Loaded 로 지연.
        // 사유: XAML 의 `_llmChatPanel Closed="True"` 초기 박제 + DX 의 ItemIsVisibleChanged 가
        // layout 첫 평가 (InitializeComponent 직후 또는 첫 arrange 시점) 에 false-IsVisible raise 를
        // 일으킬 가능성 있음. ctor 안에서 hook 등록 시 그 초기 raise 가 외부 (SSOT) 로 누설되어
        // VM 의 IsLlmChatVisible 가 false 로 강제 되거나 무한 loop 진입 위험.
        // Loaded 는 layout 첫 arrange 완료 시점이므로, 그 안에서 hook 등록하면 초기 raise 는
        // 모두 hook 이전에 처리되어 외부로 누설되지 않음 (정적 분석 기반 보수적 default).
        Loaded += DockHost_Loaded;
    }

    private void DockHost_Loaded(object sender, RoutedEventArgs e)
    {
        // 1회만 hook (Loaded 다회 발생 — UserControl 재parent 등 — 시 중복 등록 방지).
        Loaded -= DockHost_Loaded;
        _dockLayout.ItemIsVisibleChanged += OnItemIsVisibleChanged;
    }

    public event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;

    /// <summary>
    /// PR-D9 (MJ2 복구) — anchor caption 의 Help 버튼 click event.
    /// 매개 = ContentId 문자열 (DX type 외부 노출 0). MainWindow 가
    /// <c>Promaker.Help.HelpNavigator.NavigateCommand.Execute(contentId)</c> 로 hook.
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

        var panel = ResolveAnchorPanel(anchor.DefaultPosition);
        ApplyAnchorMetadata(panel, anchor);
        // caption 폰트/높이 통일 (DockHost.xaml 의 PaneCaptionFontSize SSOT) — DX LayoutPanel caption 기본 폰트가
        // DocumentPanel(Workspace) 보다 커지는 문제를 모든 anchor 에 동일 template 으로 차단.
        //   - HasHelp(explorer/properties/history): Help 뱃지 포함 AnchorCaptionWithHelp (caption TextBlock 에 동일 FontSize).
        //   - 그 외(simulation/log/llmchat): 폰트만 통일하는 PaneCaption.
        panel.CaptionTemplate = (DataTemplate)FindResource(anchor.HasHelp ? "AnchorCaptionWithHelp" : "PaneCaption");
        panel.Content = anchor.Content;
        _itemsByContentId[anchor.ContentId] = panel;
    }

    /// <summary>
    /// PR-D9 — caption Help 뱃지 click 핸들러. <c>PreviewMouseLeftButtonDown</c> 사용 — DX caption chrome 이
    /// 자체 drag-drop 을 위해 mouse event 를 캡처하므로 일반 Button.Click 으로는 동작 안 함. preview 단계에서
    /// e.Handled=true 로 chrome 의 mouse capture 차단 + AnchorHelpRequested 발화. Border.Tag = anchor.ContentId
    /// (Binding Name). 외부 (MainWindow) 가 Promaker.Help.HelpNavigator.NavigateCommand 호출.
    /// </summary>
    private void HelpBadge_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string contentId && !string.IsNullOrEmpty(contentId))
        {
            AnchorHelpRequested?.Invoke(this, contentId);
            e.Handled = true;
        }
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

        // DocumentGroup 도 LayoutGroup 상속 — Items 컬렉션에 DocumentPanel 추가.
        var docPanel = new DocumentPanel();
        ApplyAnchorMetadata(docPanel, document);
        docPanel.Content = document.Content;
        _documentGroup.Items.Add(docPanel);
        _itemsByContentId[document.ContentId] = docPanel;
    }

    public void SetAnchorVisible(string contentId, bool visible)
    {
        // BaseLayoutItem.Closed : bool (DependencyProperty). true 면 hidden.
        FindLayoutItem(contentId).Closed = !visible;
    }

    public bool IsAnchorVisible(string contentId) => !FindLayoutItem(contentId).Closed;

    /// <summary>
    /// PR-D6 — DX <see cref="DockLayoutManager.SaveLayoutToXml(string)"/> wrapping.
    /// 상위 디렉토리 미존재 시 생성. 호출 측 경로는 <c>%LOCALAPPDATA%\Promaker\dock-layout.xml</c> 박제.
    /// </summary>
    public void SaveLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        var dir = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _dockLayout.SaveLayoutToXml(filepath);
    }

    /// <summary>
    /// PR-D6 — DX <see cref="DockLayoutManager.RestoreLayoutFromXml(string)"/> wrapping.
    /// 파일 미존재 시 default layout 유지. parse / restore 실패 시에도 default 유지 (외부 환경 예외 — fail-safe).
    /// CLAUDE.md `## 예외 처리`: "프로그램 동작 중에 외부 환경 등의 어쩔 수 없는 요소에 의해서 예상되는 예외는 log 메시지" —
    /// Promaker.Dock 에는 log4net 미연결이므로 swallow (호출측에서 default 진행).
    /// </summary>
    public void RestoreLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        if (!File.Exists(filepath)) return;
        try { _dockLayout.RestoreLayoutFromXml(filepath); }
        catch (Exception ex)
        {
            // Promaker.Dock 에 log4net 미연결 — 최소 진단 흔적 (사용자 철학 "외부 예외는 log 남김").
            System.Diagnostics.Trace.TraceWarning($"DockHost.RestoreLayout failed for '{filepath}': {ex.Message}");
        }
        // RestoreLayoutFromXml 이 _documentGroup 의 caption 버튼 숨김 설정을 기본값으로 되돌리므로 복원 후 재적용.
        ApplyDocumentGroupCaptionButtons();
    }

    /// <summary>
    /// Workspace 문서 영역(<see cref="_documentGroup"/>) caption 우측 기본 버튼 표시 숨김:
    /// ▼ 문서목록 드롭다운(<see cref="DocumentGroup.ShowDropDownButton"/>) / ✕ 활성문서 닫기
    /// (<see cref="DocumentGroup.ClosePageButtonShowMode"/>). **동작은 보존하고 표시만 차단** — Close 는 API 로 여전히 가능.
    /// <para>
    /// ctor + <see cref="RestoreLayout"/> 직후 양쪽에서 호출 필수. RestoreLayoutFromXml 은 이 두 속성을 XML 에
    /// 직렬화하지 않아 복원 시 기본값(버튼 노출)으로 되돌리므로, 복원 후 재적용해야 설정이 유지됨
    /// (XAML 선언으로는 복원에 덮어써져 무효 — 코드로 일원화).
    /// </para>
    /// </summary>
    private void ApplyDocumentGroupCaptionButtons()
    {
        _documentGroup.ShowDropDownButton = false;
        _documentGroup.ClosePageButtonShowMode = ClosePageButtonShowMode.NoWhere;
    }

    /// <summary>
    /// DockAnchorPosition → 미리 만든 LayoutPanel 매핑. enum 6 anchor 위치 + Document (별도 경로).
    /// post-D8 fix (Log 독립 anchor 승격) 매핑:
    ///   Left=explorer / BottomLeft=simulation / BottomRight=log / RightTop=property / RightMiddle=history / RightBottom=llmchat.
    /// </summary>
    private LayoutPanel ResolveAnchorPanel(DockAnchorPosition position) => position switch
    {
        DockAnchorPosition.Left => _explorerPanel,
        DockAnchorPosition.BottomLeft => _simulationPanel,
        DockAnchorPosition.BottomRight => _logPanel,
        DockAnchorPosition.RightTop => _propertyPanel,
        DockAnchorPosition.RightMiddle => _historyPanel,
        DockAnchorPosition.RightBottom => _llmChatPanel,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unsupported anchor position."),
    };

    /// <summary>
    /// LayoutPanel / DocumentPanel 공통 메타 (Caption / Name / BindableName) 적용.
    /// Name = ContentId — visibility 통보 event 에서 contentId 식별용.
    /// BindableName 도 동일 — DX layout serializer (PR-D6) 용.
    /// </summary>
    private static void ApplyAnchorMetadata(BaseLayoutItem item, DockAnchor anchor)
    {
        item.Caption = anchor.Title;
        item.Name = anchor.ContentId;
        item.BindableName = anchor.ContentId;
    }

    private BaseLayoutItem FindLayoutItem(string contentId)
    {
        if (string.IsNullOrEmpty(contentId)) throw new ArgumentException("contentId is required.", nameof(contentId));
        if (!_itemsByContentId.TryGetValue(contentId, out var item))
            throw new InvalidOperationException($"DockAnchor not registered: ContentId={contentId}");
        return item;
    }

    private void OnItemIsVisibleChanged(object? sender, ItemIsVisibleChangedEventArgs e)
    {
        // e.Item.Name = anchor.ContentId (RegisterAnchor 에서 set).
        var contentId = e.Item?.Name;
        if (string.IsNullOrEmpty(contentId)) return;
        AnchorVisibilityChanged?.Invoke(this, new DockAnchorVisibilityChangedEventArgs(contentId, e.IsVisible));
    }
}

/// <summary>
/// AnchorCaptionWithHelp 의 Help(?) 뱃지를 'docked caption bar 일 때만' 표시하기 위한 converter.
/// DX <see cref="BaseLayoutItem.CaptionTemplate"/> 은 docked caption bar 와 tab caption 양쪽 모두에 적용되는데,
/// tab caption 으로 렌더될 때는 LayoutPanel visual 조상이 없음(tab strip 에서 그려짐). XAML 에서
/// <c>{Binding RelativeSource={RelativeSource AncestorType=LayoutPanel}}</c> 를 source 로 주면:
///   - caption bar: 조상(LayoutPanel) 발견 → value=LayoutPanel(non-null) → Visible.
///   - tab caption: 조상 없음 → binding 미해결 → 호출 측에서 FallbackValue=Collapsed 적용(converter 미호출).
/// converter 자체는 non-null → Visible / null → Collapsed 의 단순 매핑(자기문서화 목적).
/// </summary>
public sealed class AncestorPresenceToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
