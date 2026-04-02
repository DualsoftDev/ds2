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
            "", 0);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        _host.TryAction(() =>
            _store.UpdateConditionApiCallOutputSpec(
                _callId, row.ConditionId, row.ApiCallId,
                dialog.OutSpecTypeIndex, dialog.OutSpecText));
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
