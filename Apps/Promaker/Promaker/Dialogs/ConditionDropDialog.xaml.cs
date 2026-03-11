using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

public partial class ConditionDropDialog : Window
{
    private Brush? _originalBorderBrush;
    private Func<Guid, IReadOnlyList<ConditionApiCallChoice>>? _apiCallProvider;
    private Action<IReadOnlyList<Guid>>? _onConfirmed;

    public ConditionDropDialog()
    {
        InitializeComponent();
    }

    public ObservableCollection<ConditionApiCallChoice> ApiCallChoices { get; } = [];

    /// 비모달로 열기 — Call 드롭 시 ApiCall 목록 조회 + 확인 시 콜백
    public void ShowNonModal(
        Func<Guid, IReadOnlyList<ConditionApiCallChoice>> apiCallProvider,
        Action<IReadOnlyList<Guid>> onConfirmed)
    {
        _apiCallProvider = apiCallProvider;
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

        LoadApiCallsForCall(callNode);
        e.Handled = true;
    }

    private void RestoreDropBorder()
    {
        DropTargetBorder.BorderBrush = _originalBorderBrush ?? (Brush)FindResource("BorderBrush");
        DropTargetBorder.BorderThickness = new Thickness(1);
    }

    // ── ApiCall 리스트 표시 ──

    private void LoadApiCallsForCall(EntityNode callNode)
    {
        if (_apiCallProvider is null) return;

        var choices = _apiCallProvider(callNode.Id);
        if (choices.Count == 0)
        {
            DroppedCallLabel.Text = $"Call: {callNode.Name} — ApiCall 없음";
            ApiCallListPanel.Visibility = Visibility.Visible;
            EmptyHintPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ApiCallChoices.Clear();
        foreach (var choice in choices)
        {
            choice.IsSelected = true;
            ApiCallChoices.Add(choice);
        }

        DroppedCallLabel.Text = $"Call: {callNode.Name} ({choices.Count}개 ApiCall)";
        ApiCallItemsControl.ItemsSource = ApiCallChoices;

        EmptyHintPanel.Visibility = Visibility.Collapsed;
        ApiCallListPanel.Visibility = Visibility.Visible;
        SelectAllButton.Visibility = Visibility.Visible;
        UpdateOkEnabled();
    }

    private void ApiCallCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateOkEnabled();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var allSelected = ApiCallChoices.All(c => c.IsSelected);
        foreach (var choice in ApiCallChoices)
            choice.IsSelected = !allSelected;

        // rebind to refresh CheckBox state
        ApiCallItemsControl.ItemsSource = null;
        ApiCallItemsControl.ItemsSource = ApiCallChoices;
        UpdateOkEnabled();
    }

    private void UpdateOkEnabled()
    {
        OkButton.IsEnabled = ApiCallChoices.Any(c => c.IsSelected);
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = ApiCallChoices
            .Where(c => c.IsSelected)
            .Select(c => c.ApiCallId)
            .ToList();
        if (selectedIds.Count == 0) return;
        _onConfirmed?.Invoke(selectedIds);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class ConditionApiCallChoice(Guid apiCallId, string displayName)
{
    public Guid ApiCallId { get; } = apiCallId;
    public string DisplayName { get; } = displayName;
    public bool IsSelected { get; set; }
}
