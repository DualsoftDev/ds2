using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace Promaker.Spike;

/// <summary>
/// PR-1b spike. Dock layout todo 의 8 검증 항목을 한 곳에서 확인하기 위한 임시 Window.
/// 본 코드 무영향. PR-2a 진입 시 제거 예정.
/// 실행: <c>Promaker.exe --dock-spike</c>
/// </summary>
public partial class DockSpikeWindow : Window
{
    private readonly StringBuilder _log = new();
    private string? _serializedSnapshot;

    public DockSpikeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RunAllChecks();
    }

    private void RunAllChecks()
    {
        _log.Clear();
        Section("Promaker Dock Spike — 자동 검증");
        Line($"AvalonDock asm: {typeof(DockingManager).Assembly.GetName().Name} {typeof(DockingManager).Assembly.GetName().Version}");
        Line($"AvalonDock asm full: {typeof(DockingManager).Assembly.FullName}");

        Check3_AnchorableEvents();
        Check5_DeserializeOverloads();
        Check7_ThemeProperty();
        Check8_CloseInputGestures();
        Check337_IsVisibleChangedRaiseOrder();
        Check4and6_SerializeRoundTrip();
        Check338_UnknownContentIdDefault();
        Check_ThemeBrushKeys();
        FlushLog();
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 3 — LayoutAnchorable 이벤트 enumerate
    // ────────────────────────────────────────────────────────────────
    private void Check3_AnchorableEvents()
    {
        Section("[3] LayoutAnchorable events (Hiding / Hidden / Closed / IsVisibleChanged)");
        var events = typeof(LayoutAnchorable)
            .GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .OrderBy(e => e.Name)
            .ToList();
        foreach (var ev in events)
            Line($"  • {ev.Name}  ({ev.EventHandlerType?.Name})  declared in {ev.DeclaringType?.Name}");

        var hiding = events.FirstOrDefault(e => e.Name == "Hiding");
        Line($"  → Hiding 이벤트 존재: {(hiding != null ? "YES" : "NO")}");
        if (hiding != null)
        {
            var argsType = hiding.EventHandlerType?.GetMethod("Invoke")?.GetParameters()[1].ParameterType;
            var cancelProp = argsType?.GetProperty("Cancel");
            Line($"  → Hiding args: {argsType?.Name} / e.Cancel 가능: {(cancelProp != null ? "YES" : "NO")}");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 5 — XmlLayoutSerializer.Deserialize 오버로드 enumerate
    // ────────────────────────────────────────────────────────────────
    private void Check5_DeserializeOverloads()
    {
        Section("[5] XmlLayoutSerializer.Deserialize 오버로드");
        var methods = typeof(XmlLayoutSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Deserialize")
            .ToList();
        foreach (var m in methods)
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Line($"  • Deserialize({ps})");
        }
        Line($"  → 오버로드 수: {methods.Count}");
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 7 — DockingManager.Theme property + namespace 충돌
    // ────────────────────────────────────────────────────────────────
    private void Check7_ThemeProperty()
    {
        Section("[7] DockingManager.Theme property + Promaker ThemeManager namespace");
        var themeProp = typeof(DockingManager).GetProperty("Theme");
        Line($"  • DockingManager.Theme: {(themeProp != null ? themeProp.PropertyType.FullName : "NOT FOUND")}");
        Line($"  • Promaker.Presentation.ThemeManager: {typeof(Promaker.Presentation.ThemeManager).FullName}");
        Line("  → 둘 다 다른 namespace + 다른 의미. using alias 불필요. dockManager.Theme = new Vs2013Theme(); 식으로 직접 지정 가능.");
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 8 — LayoutItem.CloseCommand.InputGestures (Ctrl+F4 / Ctrl+W)
    // ────────────────────────────────────────────────────────────────
    private void Check8_CloseInputGestures()
    {
        Section("[8] LayoutItem.CloseCommand / Ctrl+F4 / Ctrl+W");
        var liType = typeof(AvalonDock.Controls.LayoutItem);
        var closeProp = liType.GetProperty("CloseCommand");
        Line($"  • LayoutItem.CloseCommand 존재: {(closeProp != null ? "YES" : "NO")}");
        Line($"  • LayoutItem.CloseCommand type: {closeProp?.PropertyType.FullName ?? "n/a"}");
        Line("  → InputGestures 는 LayoutItem 인스턴스 별 RoutedUICommand 가 아닌 ICommand. RoutedUICommand 가 아니면 InputGestures 변경 대상 X.");
        Line("  → Ctrl+F4 / Ctrl+W 의 기본 처리: spike Window 에서 anchor 활성화 후 Ctrl+F4 눌러 수동 검증.");
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 4 + 6 — Serialize → Deserialize round-trip 자동
    //   • IsVisible="False" round-trip
    //   • x:Name field reference 보존
    // ────────────────────────────────────────────────────────────────
    private void Check4and6_SerializeRoundTrip()
    {
        Section("[4][6] IsVisible round-trip + x:Name field reference 보존");

        // 사전 상태: LlmChat=Hidden, Explorer=Visible 로 setup
        if (llmChatAnchor.IsVisible) llmChatAnchor.Hide();
        if (!explorerAnchor.IsVisible) explorerAnchor.Show();

        var llmRef0 = llmChatAnchor;
        var expRef0 = explorerAnchor;
        Line($"  pre-serialize: llmChat.IsVisible={llmChatAnchor.IsVisible}, explorer.IsVisible={explorerAnchor.IsVisible}");

        var ser = new XmlLayoutSerializer(dockManager);
        using var sw = new StringWriter();
        ser.Serialize(sw);
        var xml = sw.ToString();  // 로컬 — 수동 Serialize 버튼이 보존한 _serializedSnapshot 덮어쓰기 금지 (review m3)
        Line($"  serialized xml length: {xml.Length}");

        using var sr = new StringReader(xml);
        ser.Deserialize(sr);

        var llmReDocked = dockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == "llmchat");
        var expReDocked = dockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == "explorer");

        if (llmReDocked == null && expReDocked == null)
            Line("  ※ 두 anchor 모두 descendents 에 없음 → LayoutSerializationCallback 미등록 / Layout 통째 교체 가능성.");

        Line($"  post-deserialize: llmChat.IsVisible={llmReDocked?.IsVisible}, explorer.IsVisible={expReDocked?.IsVisible}");
        Line($"  [4] IsVisible round-trip (LlmChat Hidden): {(llmReDocked?.IsVisible == false ? "PASS" : "FAIL — 깨짐, SSOT reconcile 필요")}");
        Line($"  [6] x:Name field 보존 — llmChat: ReferenceEquals(field, descendent)={ReferenceEquals(llmRef0, llmReDocked)} / explorer: {ReferenceEquals(expRef0, expReDocked)}");
        Line($"     → false 면 ReconcileAnchors() 필수.");
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 337 (todo line 337) — IsVisible ↔ IsVisibleChanged raise 순서 trace
    // ────────────────────────────────────────────────────────────────
    private void Check337_IsVisibleChangedRaiseOrder()
    {
        Section("[337] LayoutAnchorable.IsVisibleChanged raise 순서 (Hide / Show)");
        var trace = new List<string>();
        EventHandler handler = (s, e) =>
        {
            var a = s as LayoutAnchorable;
            trace.Add($"    raised: anchor={a?.ContentId}, IsVisible={a?.IsVisible}, IsActive={a?.IsActive}, IsSelected={a?.IsSelected}");
        };
        explorerAnchor.IsVisibleChanged += handler;
        try
        {
            trace.Add("  · Hide() →");
            explorerAnchor.Hide();
            trace.Add("  · Show() →");
            explorerAnchor.Show();
            trace.Add("  · IsActive=true / IsSelected=true →");
            explorerAnchor.IsActive = true;
            explorerAnchor.IsSelected = true;
        }
        finally
        {
            explorerAnchor.IsVisibleChanged -= handler;
        }
        foreach (var t in trace) Line(t);
    }

    // ────────────────────────────────────────────────────────────────
    // 항목 338 — LayoutSerializationCallback 의 e.Cancel 미설정 시 4.74.1 기본 동작
    // ────────────────────────────────────────────────────────────────
    private void Check338_UnknownContentIdDefault()
    {
        Section("[338] LayoutSerializationCallback 미매핑 ContentId — e.Cancel 미설정 동작");
        // 의도적으로 unknown ContentId 를 포함한 XML 을 만들어서 (현재 layout xml 의 explorer 를 unknownX 로 치환)
        // callback 에서 e.Cancel 미설정 시 4.74.1 가 어떻게 처리하는지 확인.
        var ser0 = new XmlLayoutSerializer(dockManager);
        using var sw = new StringWriter();
        ser0.Serialize(sw);
        var xml = sw.ToString().Replace("ContentId=\"explorer\"", "ContentId=\"unknownX\"");

        var seen = new List<string>();
        var ser1 = new XmlLayoutSerializer(dockManager);
        ser1.LayoutSerializationCallback += (s, a) =>
        {
            seen.Add(a.Model.ContentId);
            // 의도적으로 e.Cancel 손대지 않음 — 기본값(false) 으로 진행
        };
        using var sr = new StringReader(xml);
        ser1.Deserialize(sr);
        Line($"  callback 로 통과한 ContentId 목록: [{string.Join(", ", seen)}]");
        var unknown = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "unknownX");
        Line($"  → unknownX anchor 존재 여부 after deserialize: {(unknown != null ? "EXISTS (e.Cancel 기본 false 면 placeholder 로 보존)" : "없음 (4.74.1 가 자동 drop)")}");
    }

    private void Check_ThemeBrushKeys()
    {
        Section("[테마] Promaker 자체 brush key 5개 도달성 (Application.Current.Resources)");
        var keys = new[] { "PrimaryBackgroundBrush", "SecondaryBackgroundBrush", "BorderBrush", "AccentBrush", "PanelHeaderBrush" };
        var res = Application.Current.Resources;
        foreach (var k in keys)
        {
            var v = res.Contains(k) ? res[k] : null;
            Line($"  • {k}: {(v != null ? v.GetType().Name : "NOT FOUND")}");
        }
        Line("  → spike 모드는 ThemeManager.ApplySavedTheme() 통과한 App.OnStartup 이후 띄움. NOT FOUND 항목 있으면 PR-4 의 DockResources 머지 시 별도 정의 필요.");
    }

    // ────────────────────────────────────────────────────────────────
    // 버튼 핸들러
    // ────────────────────────────────────────────────────────────────
    private void RunAllChecks_Click(object sender, RoutedEventArgs e) => RunAllChecks();

    private void Serialize_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        var ser = new XmlLayoutSerializer(dockManager);
        using var sw = new StringWriter();
        ser.Serialize(sw);
        _serializedSnapshot = sw.ToString();
        Section("Serialize snapshot 저장됨");
        Line($"length={_serializedSnapshot.Length}");
        Line(_serializedSnapshot);
        FlushLog();
    }

    private void Deserialize_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        if (_serializedSnapshot is null)
        {
            Section("Deserialize");
            Line("  snapshot 없음 — 먼저 Serialize 클릭.");
            FlushLog();
            return;
        }
        Section("Deserialize 시작");
        var llmRef0 = llmChatAnchor;
        var expRef0 = explorerAnchor;
        var ser = new XmlLayoutSerializer(dockManager);
        ser.LayoutSerializationCallback += (s, a) =>
        {
            // ContentId 기반 매핑 — 미매핑은 e.Cancel
            // spike 에서는 모두 알려진 ID 라 매핑 통과. 미지의 ID 가 들어오면 Cancel.
            var known = new HashSet<string> { "explorer", "canvas", "simulation", "properties", "history", "llmchat" };
            if (!known.Contains(a.Model.ContentId))
            {
                a.Cancel = true;
                Line($"  callback: unknown contentId='{a.Model.ContentId}' → Cancel");
            }
        };
        using var sr = new StringReader(_serializedSnapshot);
        ser.Deserialize(sr);
        var llmRef1 = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "llmchat");
        var expRef1 = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "explorer");
        Line($"  llmChat ref equal: {ReferenceEquals(llmRef0, llmRef1)}");
        Line($"  explorer ref equal: {ReferenceEquals(expRef0, expRef1)}");
        Line($"  llmChat IsVisible after deserialize: {llmRef1?.IsVisible}");
        Line($"  explorer IsVisible after deserialize: {expRef1?.IsVisible}");
        FlushLog();
    }

    private void ToggleLlmChat_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        Section("LlmChat IsVisible toggle");
        if (llmChatAnchor.IsVisible)
        {
            llmChatAnchor.Hide();
            Line("  Hide() 호출 → IsVisible=" + llmChatAnchor.IsVisible);
        }
        else
        {
            llmChatAnchor.Show();
            llmChatAnchor.IsActive = true;
            llmChatAnchor.IsSelected = true;
            Line("  Show()+IsActive=true+IsSelected=true → IsVisible=" + llmChatAnchor.IsVisible + ", IsActive=" + llmChatAnchor.IsActive);
        }
        FlushLog();
    }

    private void FloatCycle_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        Section("Float ↔ Hide ↔ Show 5회 cycle — anchor lifecycle leak 측정 (#859/#1033)");
        Line("  ※ AvalonDock 정석 \"Float() 후 직전 docked 위치로 복원\" API 가 spike 범위 외라");
        Line("    Float→Hide→Show 사이클로 anchor lifecycle leak 만 측정. AutoHide leak 은 별도 수동 검증.");
        var p = Process.GetCurrentProcess();
        p.Refresh();
        var ws0 = p.WorkingSet64;
        Line($"  WorkingSet baseline: {ws0:N0} B");

        for (int i = 0; i < 5; i++)
        {
            explorerAnchor.Float();
            Line($"  cycle {i + 1}: Float → IsFloating={explorerAnchor.IsFloating}");
            explorerAnchor.Hide();
            Line($"            Hide  → IsVisible={explorerAnchor.IsVisible}");
            explorerAnchor.Show();
            Line($"            Show  → IsVisible={explorerAnchor.IsVisible}, IsFloating={explorerAnchor.IsFloating}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        p.Refresh();
        var ws1 = p.WorkingSet64;
        Line($"  WorkingSet after 5 cycles + 2x GC: {ws1:N0} B (delta {ws1 - ws0:+#,#;-#,#;0})");
        Line("  ※ 절대값보다 delta 추세가 중요. 정밀 leak 측정은 PerfView 별도.");
        FlushLog();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(logBox.Text);
    }

    // ────────────────────────────────────────────────────────────────
    private void Section(string title)
    {
        _log.AppendLine();
        _log.AppendLine($"── {title} ──");
    }

    private void Line(string s) => _log.AppendLine(s);

    private void FlushLog() => logBox.Text = _log.ToString();
}
