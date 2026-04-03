using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

public partial class ConditionEditDialog : Window
{
    private readonly DsStore _store;
    private readonly MainViewModel.PropertyPanelHost _host;
    private readonly Guid _callId;
    private readonly CallConditionType _condType;

    public ConditionEditDialog(
        DsStore store,
        MainViewModel.PropertyPanelHost host,
        Guid callId,
        CallConditionType condType)
    {
        InitializeComponent();
        _store = store;
        _host = host;
        _callId = callId;
        _condType = condType;
        SectionTitle.Text = $"{condType} 조건 편집";
        ReloadList();
    }

    private void ReloadList()
    {
        if (!_host.TryRef(
                () => _store.GetCallConditionsForPanel(_callId),
                out var allConditions))
            return;

        var filtered = allConditions
            .Where(c => c.ConditionType == _condType)
            .Select(c => new CallConditionItem(_callId, c))
            .ToList();

        ConditionList.ItemsSource = filtered;
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Add / Remove conditions ──

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.TryAction(() => _store.AddCallCondition(_callId, _condType)))
            return;
        ReloadList();
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallConditionItem item }) return;
        if (!_host.TryAction(() => _store.RemoveCallCondition(_callId, item.ConditionId)))
            return;
        ReloadList();
    }

    // ── Toggle OR / Rising ──

    private void ToggleOR_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallConditionItem item }) return;
        _host.TryAction(() =>
            _store.UpdateCallConditionSettings(_callId, item.ConditionId, !item.IsOR, item.IsRising));
        ReloadList();
    }

    private void ToggleRising_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallConditionItem item }) return;
        _host.TryAction(() =>
            _store.UpdateCallConditionSettings(_callId, item.ConditionId, item.IsOR, !item.IsRising));
        ReloadList();
    }

    // ── Child conditions ──

    private void AddChildCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallConditionItem item }) return;
        _host.TryAction(() => _store.AddChildCondition(_callId, item.ConditionId, false));
        ReloadList();
    }

    // ── ApiCall management ──

    private void AddApiCallToCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallConditionItem item }) return;
        if (!_store.Calls.TryGetValue(_callId, out var call)) return;

        var available = call.ApiCalls
            .Select(ac => new { ac.Id, ac.Name })
            .ToList();

        // 현재 Flow의 모든 ApiCalls
        var flowApiCalls = new List<ApiCallPickItem>();
        if (_store.Works.TryGetValue(call.ParentId, out var parentWork)
            && _store.Flows.TryGetValue(parentWork.ParentId, out var parentFlow))
        {
            foreach (var w in _store.Works.Values.Where(w => w.ParentId == parentFlow.Id))
                foreach (var c in _store.Calls.Values.Where(c => c.ParentId == w.Id))
                    foreach (var ac in c.ApiCalls)
                        flowApiCalls.Add(new ApiCallPickItem(ac.Id, ac.Name, $"{w.LocalName}/{c.Name}"));
        }

        // 전체 System의 ApiCalls (Flow에 없는 것만)
        var flowIds = flowApiCalls.Select(x => x.Id).ToHashSet();
        var systemApiCalls = new List<ApiCallPickItem>();
        foreach (var ac in _store.ApiCalls.Values)
            if (!flowIds.Contains(ac.Id))
                systemApiCalls.Add(new ApiCallPickItem(ac.Id, ac.Name, ""));

        ShowApiCallPicker(item.ConditionId, flowApiCalls, systemApiCalls);
    }

    private void RemoveApiCall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConditionApiCallRow row }) return;
        _host.TryAction(() => _store.RemoveApiCallFromCondition(_callId, row.ConditionId, row.ApiCallId));
        ReloadList();
    }

    private void EditApiCallSpec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConditionApiCallRow row }) return;
        var dialog = new ApiCallSpecDialog(
            row.ApiCallName,
            row.OutputSpecText, row.OutputSpecTypeIndex,
            row.InputSpecText, row.InputSpecTypeIndex);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        _host.TryAction(() =>
            _store.UpdateConditionApiCallOutputSpec(
                _callId, row.ConditionId, row.ApiCallId,
                dialog.OutSpecTypeIndex, dialog.OutSpecText));
        _host.TryAction(() =>
            _store.UpdateConditionApiCallInputSpec(
                _callId, row.ConditionId, row.ApiCallId,
                dialog.InSpecTypeIndex, dialog.InSpecText));
        ReloadList();
    }

    // ── Recursive children template (code-behind because WPF can't self-reference DataTemplate) ──

    private void ChildrenHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl host) return;
        host.ItemTemplate = BuildChildItemTemplate();
    }

    private DataTemplate BuildChildItemTemplate()
    {
        var template = new DataTemplate();
        template.VisualTree = new FrameworkElementFactory(typeof(ContentControl));
        template.VisualTree.SetValue(ContentControl.ContentProperty, new System.Windows.Data.Binding());
        template.VisualTree.AddHandler(ContentControl.LoadedEvent, new RoutedEventHandler(ChildItem_Loaded));
        return template;
    }

    private void ChildItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl cc || cc.Content is not CallConditionItem item) return;
        cc.Content = null;
        cc.Content = item;
        cc.ContentTemplate = null;

        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

        var removeBtn = CreateButton("x", "조건 삭제", item, RemoveCondition_Click);
        headerPanel.Children.Add(removeBtn);

        var orCheck = new CheckBox
        {
            Content = "OR", IsChecked = item.IsOR, Tag = item,
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11
        };
        orCheck.Click += ToggleOR_Click;
        headerPanel.Children.Add(orCheck);

        var risingCheck = new CheckBox
        {
            Content = "Rising", IsChecked = item.IsRising, Tag = item,
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11
        };
        risingCheck.Click += ToggleRising_Click;
        headerPanel.Children.Add(risingCheck);

        var addChildBtn = CreateButton("+ 하위그룹", null, item, AddChildCondition_Click);
        headerPanel.Children.Add(addChildBtn);

        var addApiBtn = CreateButton("+", "ApiCall 추가", item, AddApiCallToCondition_Click);
        addApiBtn.FontWeight = FontWeights.Bold;
        headerPanel.Children.Add(addApiBtn);

        panel.Children.Add(headerPanel);

        foreach (var apiRow in item.Items)
        {
            var rowGrid = new Grid { Margin = new Thickness(12, 2, 0, 0) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var delBtn = CreateButton("x", null, apiRow, RemoveApiCall_Click);
            Grid.SetColumn(delBtn, 0);
            rowGrid.Children.Add(delBtn);

            var nameText = new TextBlock
            {
                Text = apiRow.ApiDefDisplayName, FontSize = 11,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(nameText, 1);
            rowGrid.Children.Add(nameText);

            var specText = new TextBlock
            {
                Text = apiRow.OutputSpecText, FontSize = 11,
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(specText, 2);
            rowGrid.Children.Add(specText);

            var editBtn = CreateButton("편집", null, apiRow, EditApiCallSpec_Click);
            Grid.SetColumn(editBtn, 3);
            rowGrid.Children.Add(editBtn);

            panel.Children.Add(rowGrid);
        }

        if (item.Children.Count > 0)
        {
            var childHost = new ItemsControl
            {
                ItemsSource = item.Children,
                Margin = new Thickness(20, 4, 0, 0)
            };
            childHost.Loaded += ChildrenHost_Loaded;
            panel.Children.Add(childHost);
        }

        var childBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Padding = new Thickness(6),
            Margin = new Thickness(2),
            Child = panel
        };
        cc.Content = childBorder;
    }

    private Button CreateButton(string content, string? toolTip, object tag, RoutedEventHandler handler)
    {
        var btn = new Button
        {
            Content = content, Tag = tag, Padding = new Thickness(6, 2, 6, 2), FontSize = 10,
            Margin = new Thickness(0, 0, 4, 0)
        };
        if (Application.Current.TryFindResource("DarkButton") is Style s) btn.Style = s;
        if (toolTip is not null) btn.ToolTip = toolTip;
        btn.Click += handler;
        return btn;
    }

    private void ShowApiCallPicker(
        Guid conditionId,
        List<ApiCallPickItem> flowItems,
        List<ApiCallPickItem> systemItems)
    {
        var bg = Application.Current.TryFindResource("SecondaryBackgroundBrush") as Brush;
        var fg = Application.Current.TryFindResource("PrimaryTextBrush") as Brush;
        var borderBrush = Application.Current.TryFindResource("BorderBrush") as Brush;

        var picker = new Window
        {
            Title = "ApiCall 선택",
            Width = 400, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };
        if (bg is not null) picker.Background = bg;

        var mainPanel = new StackPanel { Margin = new Thickness(10) };

        // 검색
        var searchBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12
        };
        if (bg is not null) searchBox.Background = bg;
        if (fg is not null) searchBox.Foreground = fg;
        if (borderBrush is not null) searchBox.BorderBrush = borderBrush;
        mainPanel.Children.Add(searchBox);

        // 탭
        var tabControl = new TabControl { Height = 320 };
        if (bg is not null) tabControl.Background = bg;

        var flowTab = CreatePickerTab("현재 Flow", flowItems, bg, fg);
        var systemTab = CreatePickerTab("전체 System", systemItems, bg, fg);
        tabControl.Items.Add(flowTab.tab);
        tabControl.Items.Add(systemTab.tab);
        mainPanel.Children.Add(tabControl);

        // 검색 필터
        searchBox.TextChanged += (_, _) =>
        {
            var filter = searchBox.Text.Trim();
            ApplyPickerFilter(flowTab.listBox, flowItems, filter);
            ApplyPickerFilter(systemTab.listBox, systemItems, filter);
        };

        // 추가 버튼
        var okBtn = new Button
        {
            Content = "추가", Padding = new Thickness(20, 6, 20, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        if (Application.Current.TryFindResource("DarkButton") is Style s)
            okBtn.Style = s;
        okBtn.Click += (_, _) => { picker.DialogResult = true; picker.Close(); };
        mainPanel.Children.Add(okBtn);

        picker.Content = mainPanel;
        if (picker.ShowDialog() != true) return;

        // 모든 탭에서 선택된 항목 수집
        var selectedIds = new List<Guid>();
        CollectSelected(flowTab.listBox, selectedIds);
        CollectSelected(systemTab.listBox, selectedIds);

        if (selectedIds.Count == 0) return;
        _host.TryAction(() => _store.AddApiCallsToConditionBatch(_callId, conditionId, selectedIds));
        ReloadList();
    }

    private static (TabItem tab, ListBox listBox) CreatePickerTab(string header, List<ApiCallPickItem> items, Brush? bg, Brush? fg)
    {
        var listBox = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            DisplayMemberPath = "Display",
            FontSize = 12
        };
        if (bg is not null) listBox.Background = bg;
        if (fg is not null) listBox.Foreground = fg;
        listBox.ItemsSource = items;

        var tab = new TabItem { Header = $"{header} ({items.Count})", Content = listBox };
        return (tab, listBox);
    }

    private static void ApplyPickerFilter(ListBox listBox, List<ApiCallPickItem> source, string filter)
    {
        listBox.ItemsSource = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(a => a.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void CollectSelected(ListBox listBox, List<Guid> ids)
    {
        foreach (var item in listBox.SelectedItems)
            if (item is ApiCallPickItem pick)
                ids.Add(pick.Id);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed record ApiCallPickItem(Guid Id, string Name, string Context)
{
    public string Display => string.IsNullOrEmpty(Context) ? Name : $"{Name}  ({Context})";
}
