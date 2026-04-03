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

    public static readonly DependencyProperty AddToolTipProperty =
        DependencyProperty.Register(nameof(AddToolTip), typeof(string), typeof(ConditionSectionControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ConditionSectionControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty AddCommandProperty =
        DependencyProperty.Register(nameof(AddCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AddCommandParameterProperty =
        DependencyProperty.Register(nameof(AddCommandParameter), typeof(object), typeof(ConditionSectionControl),
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

    public static readonly DependencyProperty AddChildGroupCommandProperty =
        DependencyProperty.Register(nameof(AddChildGroupCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DropCallCommandProperty =
        DependencyProperty.Register(nameof(DropCallCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DropCallToConditionItemCommandProperty =
        DependencyProperty.Register(nameof(DropCallToConditionItemCommand), typeof(ICommand), typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public ConditionSectionControl()
    {
        InitializeComponent();
    }

    public string HeaderText { get => (string)GetValue(HeaderTextProperty); set => SetValue(HeaderTextProperty, value); }
    public string AddToolTip { get => (string)GetValue(AddToolTipProperty); set => SetValue(AddToolTipProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public ICommand? AddCommand { get => (ICommand?)GetValue(AddCommandProperty); set => SetValue(AddCommandProperty, value); }
    public object? AddCommandParameter { get => GetValue(AddCommandParameterProperty); set => SetValue(AddCommandParameterProperty, value); }
    public ICommand? RemoveConditionCommand { get => (ICommand?)GetValue(RemoveConditionCommandProperty); set => SetValue(RemoveConditionCommandProperty, value); }
    public string HelpTopic { get => (string)GetValue(HelpTopicProperty); set => SetValue(HelpTopicProperty, value); }
    public ICommand? EditConditionsCommand { get => (ICommand?)GetValue(EditConditionsCommandProperty); set => SetValue(EditConditionsCommandProperty, value); }
    public object? EditConditionsParameter { get => GetValue(EditConditionsParameterProperty); set => SetValue(EditConditionsParameterProperty, value); }
    public ICommand? AddChildGroupCommand { get => (ICommand?)GetValue(AddChildGroupCommandProperty); set => SetValue(AddChildGroupCommandProperty, value); }
    public ICommand? DropCallCommand { get => (ICommand?)GetValue(DropCallCommandProperty); set => SetValue(DropCallCommandProperty, value); }
    public ICommand? DropCallToConditionItemCommand { get => (ICommand?)GetValue(DropCallToConditionItemCommandProperty); set => SetValue(DropCallToConditionItemCommandProperty, value); }

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
            AddCommandParameter is Ds2.Core.CallConditionType ct ? ct : Ds2.Core.CallConditionType.ComAux,
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
        if (sender is not Border { Tag: ViewModels.CallConditionItem item }) return;

        DropCallToConditionItemCommand?.Execute(
            new ViewModels.ConditionItemDropInfo(item.ConditionId, callNode.Id));
        e.Handled = true;
    }

    // ── Formula syntax highlighting (VSCode dark theme style) ──

    private void FormulaBlock_Loaded(object sender, RoutedEventArgs e) =>
        ColorizeFormula(sender as TextBlock);

    private void FormulaBlock_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        ColorizeFormula(sender as TextBlock);

    private static void ColorizeFormula(TextBlock? tb)
    {
        if (tb is null) return;
        tb.Inlines.Clear();
        if (tb.DataContext is not CallConditionItem item) return;
        FormulaColorizer.BuildInlines(item, tb.Inlines);
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

    static FormulaColorizer()
    {
        NameBrush.Freeze(); OperatorBrush.Freeze(); ParenBrush.Freeze();
        ValueBrush.Freeze(); RisingBrush.Freeze(); EmptyBrush.Freeze();
    }

    public static void BuildInlines(CallConditionItem cond, InlineCollection inlines)
    {
        if (cond.Items.Count == 0 && cond.Children.Count == 0)
        {
            inlines.Add(new Run("(empty)") { Foreground = EmptyBrush, FontStyle = FontStyles.Italic });
            return;
        }

        var op = cond.IsOR ? "|" : "&";
        var parts = new List<System.Action>();

        foreach (var item in cond.Items)
            parts.Add(() => AddApiCallRuns(item, inlines));

        foreach (var child in cond.Children)
            parts.Add(() => AddChildRuns(child, inlines));

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                inlines.Add(new Run(op) { Foreground = OperatorBrush, FontWeight = FontWeights.Bold });
            parts[i]();
        }

        if (cond.IsRising)
            inlines.Add(new Run(" ↑") { Foreground = RisingBrush, FontWeight = FontWeights.Bold });
    }

    private static void AddApiCallRuns(ConditionApiCallRow item, InlineCollection inlines)
    {
        inlines.Add(new Run(item.ApiDefDisplayName) { Foreground = NameBrush });
        var spec = item.OutputSpecText;
        if (!string.IsNullOrEmpty(spec) && spec != ValueSpecEditorControl.UndefinedText)
        {
            inlines.Add(new Run("=") { Foreground = OperatorBrush });
            inlines.Add(new Run(spec) { Foreground = ValueBrush });
        }
    }

    private static void AddChildRuns(CallConditionItem child, InlineCollection inlines)
    {
        inlines.Add(new Run("(") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
        BuildInlines(child, inlines);
        inlines.Add(new Run(")") { Foreground = ParenBrush, FontWeight = FontWeights.Bold });
    }
}
