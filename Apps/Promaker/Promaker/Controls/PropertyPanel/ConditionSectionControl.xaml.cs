using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ConditionSectionControl : UserControl
{
    public static readonly DependencyProperty HeaderTextProperty =
        DependencyProperty.Register(nameof(HeaderText), typeof(string), typeof(ConditionSectionControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ConditionSectionControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ConditionTypeProperty =
        DependencyProperty.Register(nameof(ConditionType), typeof(object), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveConditionCommandProperty =
        DependencyProperty.Register(nameof(RemoveConditionCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HelpTopicProperty =
        DependencyProperty.Register(nameof(HelpTopic), typeof(string), typeof(ConditionSectionControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EditConditionsCommandProperty =
        DependencyProperty.Register(nameof(EditConditionsCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty EditConditionsParameterProperty =
        DependencyProperty.Register(nameof(EditConditionsParameter), typeof(object), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DropCallCommandProperty =
        DependencyProperty.Register(nameof(DropCallCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DropCallToConditionItemCommandProperty =
        DependencyProperty.Register(nameof(DropCallToConditionItemCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NavigateConditionApiCallCommandProperty =
        DependencyProperty.Register(nameof(NavigateConditionApiCallCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null, OnNavigateCommandChanged));

    public static readonly DependencyProperty RemoveConditionApiCallCommandProperty =
        DependencyProperty.Register(nameof(RemoveConditionApiCallCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty EditConditionApiCallSpecCommandProperty =
        DependencyProperty.Register(nameof(EditConditionApiCallSpecCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public ConditionSectionControl()
    {
        InitializeComponent();
    }

    public string HeaderText { get => (string)GetValue(HeaderTextProperty); set => SetValue(HeaderTextProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? ConditionType { get => GetValue(ConditionTypeProperty); set => SetValue(ConditionTypeProperty, value); }
    public ICommand? RemoveConditionCommand { get => (ICommand?)GetValue(RemoveConditionCommandProperty); set => SetValue(RemoveConditionCommandProperty, value); }
    public string HelpTopic { get => (string)GetValue(HelpTopicProperty); set => SetValue(HelpTopicProperty, value); }
    public ICommand? EditConditionsCommand { get => (ICommand?)GetValue(EditConditionsCommandProperty); set => SetValue(EditConditionsCommandProperty, value); }
    public object? EditConditionsParameter { get => GetValue(EditConditionsParameterProperty); set => SetValue(EditConditionsParameterProperty, value); }
    public ICommand? DropCallCommand { get => (ICommand?)GetValue(DropCallCommandProperty); set => SetValue(DropCallCommandProperty, value); }
    public ICommand? DropCallToConditionItemCommand { get => (ICommand?)GetValue(DropCallToConditionItemCommandProperty); set => SetValue(DropCallToConditionItemCommandProperty, value); }
    public ICommand? NavigateConditionApiCallCommand { get => (ICommand?)GetValue(NavigateConditionApiCallCommandProperty); set => SetValue(NavigateConditionApiCallCommandProperty, value); }
    public ICommand? RemoveConditionApiCallCommand { get => (ICommand?)GetValue(RemoveConditionApiCallCommandProperty); set => SetValue(RemoveConditionApiCallCommandProperty, value); }
    public ICommand? EditConditionApiCallSpecCommand { get => (ICommand?)GetValue(EditConditionApiCallSpecCommandProperty); set => SetValue(EditConditionApiCallSpecCommandProperty, value); }

    // ── Drop hint visibility ──

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConditionSectionControl ctrl)
            ctrl.UpdateDropHint();
    }

    private void UpdateDropHint()
    {
        var hasItems = false;
        if (ItemsSource is ICollection c) hasItems = c.Count > 0;
        else if (ItemsSource is not null)
        {
            var en = ItemsSource.GetEnumerator();
            hasItems = en.MoveNext();
        }
        DropHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Drag & Drop (delegates to ConditionDropHelper) ──

    private Brush? _savedBrush;
    private Brush? _savedItemBrush;

    private void Border_DragEnter(object sender, DragEventArgs e) =>
        ConditionDropHelper.HandleDragEnter(e, sender as Border, ref _savedBrush, this);

    private void Border_DragLeave(object sender, DragEventArgs e)
    {
        ConditionDropHelper.RestoreBorder(sender as Border, ref _savedBrush, this);
        e.Handled = true;
    }

    private void Border_DragOver(object sender, DragEventArgs e) =>
        ConditionDropHelper.HandleDragOver(e);

    private void Border_Drop(object sender, DragEventArgs e)
    {
        ConditionDropHelper.RestoreBorder(sender as Border, ref _savedBrush, this);
        if (ConditionDropHelper.GetDroppedCallNode(e) is not { } callNode) return;

        DropCallCommand?.Execute(new ConditionDropInfo(
            ConditionType is Ds2.Core.ConditionType ct ? ct : Ds2.Core.ConditionType.ComAux,
            callNode.Id));
        e.Handled = true;
    }

    // ── Drag & Drop: individual condition item ──

    private void ConditionItem_DragEnter(object sender, DragEventArgs e)
    {
        ConditionDropHelper.HandleDragEnter(e, sender as Border, ref _savedItemBrush, this);
        e.Handled = true;
    }

    private void ConditionItem_DragLeave(object sender, DragEventArgs e)
    {
        ConditionDropHelper.RestoreBorder(sender as Border, ref _savedItemBrush, this);
        e.Handled = true;
    }

    private void ConditionItem_DragOver(object sender, DragEventArgs e)
    {
        ConditionDropHelper.HandleDragOver(e);
        e.Handled = true;
    }

    private void ConditionItem_Drop(object sender, DragEventArgs e)
    {
        ConditionDropHelper.RestoreBorder(sender as Border, ref _savedItemBrush, this);
        if (ConditionDropHelper.GetDroppedCallNode(e) is not { } callNode) return;
        if (sender is not Border { Tag: ViewModels.ConditionItem item }) return;

        DropCallToConditionItemCommand?.Execute(
            new ViewModels.ConditionItemDropInfo(item.ConditionId, callNode.Id));
        e.Handled = true;
    }

    // ── Formula syntax highlighting (VSCode dark theme style) ──

    private static void OnNavigateCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConditionSectionControl ctrl)
            ctrl.RebuildAllFormulaInlines();
    }

    private void RebuildAllFormulaInlines()
    {
        // 명령이 늦게 바인딩되는 경우 기존 TextBlock 들의 hyperlink command를 갱신.
        foreach (var tb in FindAllFormulaTextBlocks(this))
            ColorizeFormula(tb);
    }

    private static IEnumerable<TextBlock> FindAllFormulaTextBlocks(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { DataContext: ConditionItem } tb)
                yield return tb;
            foreach (var nested in FindAllFormulaTextBlocks(child))
                yield return nested;
        }
    }

    private void FormulaBlock_Loaded(object sender, RoutedEventArgs e) =>
        ColorizeFormula(sender as TextBlock);

    private void FormulaBlock_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        ColorizeFormula(sender as TextBlock);

    private void ColorizeFormula(TextBlock? tb)
    {
        if (tb is null) return;
        tb.Inlines.Clear();
        if (tb.DataContext is not ConditionItem item) return;
        FormulaColorizer.BuildInlines(item, tb.Inlines, NavigateConditionApiCallCommand);
    }
}

internal static class FormulaColorizer
{
    // VSCode Dark+ palette
    private static readonly Brush NameBrush     = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)); // teal — type/identifier
    private static readonly Brush OperatorBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)); // purple — keyword/operator
    private static readonly Brush ParenBrush    = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); // gold — bracket
    private static readonly Brush ValueBrush    = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)); // orange — string/value
    private static readonly Brush RisingBrush   = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); // blue — keyword
    private static readonly Brush EmptyBrush    = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)); // gray
    private static readonly Brush MatchedBrush  = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)); // green — runtime match ✓
    private static readonly Brush MismatchBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)); // red — runtime mismatch ✗
    private static readonly Brush NeutralBrush  = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)); // gray — runtime info

    static FormulaColorizer()
    {
        NameBrush.Freeze(); OperatorBrush.Freeze(); ParenBrush.Freeze();
        ValueBrush.Freeze(); RisingBrush.Freeze(); EmptyBrush.Freeze();
        MatchedBrush.Freeze(); MismatchBrush.Freeze(); NeutralBrush.Freeze();
    }

    /// 빈 condition 의 Runtime 의미. 빈 And = true, 빈 Or = false.
    /// (F# ConditionFormulaProjection.emptyText 와 동일 규약 — SSOT 박제 결정.)
    private static string EmptyText(bool isOR) => isOR ? "false" : "true";

    private static bool IsIdentityEmptyChild(ConditionItem parent, ConditionItem child) =>
        !child.IsInverted
        && child.IsOR == parent.IsOR
        && child.Items.Count == 0
        && child.Children.Count == 0;

    /// F# ConditionFormulaProjection.formatCondition 과 같은 표시 규약으로 inline 생성.
    /// IsInverted -> `not (...)`, op join 공백(` & ` / ` | `), ContactKind/빈 condition 표기 일치.
    public static void BuildInlines(ConditionItem cond, InlineCollection inlines, ICommand? navigateCommand)
    {
        // IsInverted 는 NOT 으로 감싼다. (F# formatCondition: isInverted -> $"not ({inner})")
        if (cond.IsInverted)
        {
            inlines.Add(new Run("not ") { Foreground = OperatorBrush, FontWeight = FontWeights.Bold });
            inlines.Add(new Run("(") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
            AddItems(cond, inlines, navigateCommand);
            inlines.Add(new Run(")") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
        }
        else
        {
            AddItems(cond, inlines, navigateCommand);
        }
    }

    /// F# ConditionFormulaProjection.formatItems 대응 — items + (항등원 아닌) children 을 op 로 join.
    private static void AddItems(ConditionItem cond, InlineCollection inlines, ICommand? navigateCommand)
    {
        var op = cond.IsOR ? "|" : "&";
        var parts = new List<System.Action>();

        foreach (var item in cond.Items)
            parts.Add(() => AddLeaf(item, inlines, navigateCommand));

        foreach (var child in cond.Children)
        {
            // 부모 op 의 항등원(And->true, Or->false)인 빈 자식은 의미 변화 없이 생략.
            // (F# formatItems 와 동일한 구조 predicate.)
            if (IsIdentityEmptyChild(cond, child)) continue;
            parts.Add(() => AddChildGroup(child, inlines, navigateCommand));
        }

        if (parts.Count == 0)
        {
            // 빈 condition -> Runtime 의미 그대로 (빈 And=true, 빈 Or=false).
            inlines.Add(new Run(EmptyText(cond.IsOR)) { Foreground = EmptyBrush, FontStyle = FontStyles.Italic });
            return;
        }

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                inlines.Add(new Run($" {op} ") { Foreground = OperatorBrush, FontWeight = FontWeights.Bold });
            parts[i]();
        }
    }

    /// F# ConditionFormulaProjection.formatApiCallItem 대응 — ContactKind 표기 + 기대값(=spec) 표기.
    private static void AddLeaf(ConditionApiCallRow item, InlineCollection inlines, ICommand? navigateCommand)
    {
        // Inverter 는 placeholder leaf (ApiCallId 무시) -> `*` 만 표기.
        if (item.ContactKind == Ds2.Core.ContactKind.Inverter)
        {
            inlines.Add(new Run("*") { Foreground = OperatorBrush, FontWeight = FontWeights.Bold });
            return;
        }

        // NcContact(B접) 은 leaf 앞에 `/` 전위.
        if (item.ContactKind == Ds2.Core.ContactKind.NcContact)
            inlines.Add(new Run("/") { Foreground = OperatorBrush, FontWeight = FontWeights.Bold });

        // ApiCall identifier 토큰을 Hyperlink로 만들어 클릭 시 소유 Call로 이동.
        var nameRun = new Run(item.ApiDefDisplayName) { Foreground = NameBrush };
        var hyperlink = new Hyperlink(nameRun)
        {
            Foreground = NameBrush,
            TextDecorations = null, // 기본 underline 끄고 hover 시에만 표시
            ToolTip = $"클릭: '{item.ApiDefDisplayName}'의 원래 위치로 이동",
            Cursor = System.Windows.Input.Cursors.Hand,
            Command = navigateCommand,
            CommandParameter = item
        };
        inlines.Add(hyperlink);

        // 기대값(=spec) — UndefinedValue 는 생략. (F# baseText: spec 빈값/Undefined 면 name 만.)
        // condition leaf 기대값 = InputSpec (Runtime 평가 대상). F# formatApiCallItem 과 동일하게 InputSpecText 사용.
        var spec = item.InputSpecText;
        if (!string.IsNullOrEmpty(spec) && spec != ValueSpecEditorControl.UndefinedText)
        {
            inlines.Add(new Run("=") { Foreground = OperatorBrush });
            inlines.Add(new Run(spec) { Foreground = ValueBrush });
        }

        // RisingPulse/FallingPulse 는 leaf 뒤에 `(R)`/`(F)` 후위.
        if (item.ContactKind == Ds2.Core.ContactKind.RisingPulse)
            inlines.Add(new Run("(R)") { Foreground = RisingBrush, FontWeight = FontWeights.Bold });
        else if (item.ContactKind == Ds2.Core.ContactKind.FallingPulse)
            inlines.Add(new Run("(F)") { Foreground = RisingBrush, FontWeight = FontWeights.Bold });
    }

    private static void AddChildGroup(ConditionItem child, InlineCollection inlines, ICommand? navigateCommand)
    {
        inlines.Add(new Run("(") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
        BuildInlines(child, inlines, navigateCommand);
        inlines.Add(new Run(")") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
    }
}
