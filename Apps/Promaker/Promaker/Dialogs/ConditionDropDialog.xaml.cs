using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

public partial class ConditionDropDialog : Window
{
    private Brush? _originalBorderBrush;
    private Action<IReadOnlyList<ConditionDropResult>>? _onConfirmed;

    public ConditionDropDialog()
    {
        InitializeComponent();
        ConditionListBox.ItemsSource = AddedItems;
    }

    public ObservableCollection<ConditionDropItem> AddedItems { get; } = [];

    /// 비모달로 열기 — 확인 시 콜백 호출
    public void ShowNonModal(Action<IReadOnlyList<ConditionDropResult>> onConfirmed)
    {
        _onConfirmed = onConfirmed;
        Topmost = true;
        Show();
    }

    // ── Drag & Drop ──

    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ConditionCallNode"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        _originalBorderBrush = DropTargetBorder.BorderBrush;
        DropTargetBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        DropTargetBorder.BorderThickness = new Thickness(2);
        e.Handled = true;
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        RestoreDropBorder();
        e.Handled = true;
    }

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ConditionCallNode")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropTarget_Drop(object sender, DragEventArgs e)
    {
        RestoreDropBorder();

        if (!e.Data.GetDataPresent("ConditionCallNode")) return;
        if (e.Data.GetData("ConditionCallNode") is not EntityNode { EntityType: EntityKind.Call } callNode) return;

        ShowValueSpecPanel(callNode);
        e.Handled = true;
    }

    private void RestoreDropBorder()
    {
        DropTargetBorder.BorderBrush = _originalBorderBrush ?? (Brush)FindResource("BorderBrush");
        DropTargetBorder.BorderThickness = new Thickness(1);
    }

    // ── ValueSpec 편집 ──

    private EntityNode? _pendingCallNode;

    private void ShowValueSpecPanel(EntityNode callNode)
    {
        _pendingCallNode = callNode;
        SelectedApiCallText.Text = $"Call: {callNode.Name}";
        SpecEditor.LoadFrom("", ValueSpecTypeIndex.Undefined);
        ValueSpecPanel.Visibility = Visibility.Visible;
    }

    private void ConfirmAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingCallNode is null) return;

        var specText = SpecEditor.GetText();
        var typeIndex = SpecEditor.GetTypeIndex();

        AddedItems.Add(new ConditionDropItem(
            _pendingCallNode.Id,
            _pendingCallNode.Name,
            specText,
            typeIndex));

        _pendingCallNode = null;
        ValueSpecPanel.Visibility = Visibility.Collapsed;
        UpdateListVisibility();
        OkButton.IsEnabled = AddedItems.Count > 0;
    }

    private void CancelAdd_Click(object sender, RoutedEventArgs e)
    {
        _pendingCallNode = null;
        ValueSpecPanel.Visibility = Visibility.Collapsed;
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConditionDropItem item })
        {
            AddedItems.Remove(item);
            UpdateListVisibility();
            OkButton.IsEnabled = AddedItems.Count > 0;
        }
    }

    private void UpdateListVisibility()
    {
        var hasItems = AddedItems.Count > 0;
        ConditionListBox.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (AddedItems.Count == 0) return;
        var results = AddedItems
            .Select(x => new ConditionDropResult(x.ApiCallId, x.SpecTypeIndex, x.SpecText))
            .ToList();
        _onConfirmed?.Invoke(results);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class ConditionDropItem(Guid apiCallId, string displayName, string specText, int specTypeIndex)
{
    public Guid ApiCallId { get; } = apiCallId;
    public string DisplayName { get; } = displayName;
    public string SpecText { get; } = specText;
    public int SpecTypeIndex { get; } = specTypeIndex;
}

public sealed class ConditionDropResult(Guid callId, int specTypeIndex, string specText)
{
    public Guid CallId { get; } = callId;
    public int SpecTypeIndex { get; } = specTypeIndex;
    public string SpecText { get; } = specText;
}
