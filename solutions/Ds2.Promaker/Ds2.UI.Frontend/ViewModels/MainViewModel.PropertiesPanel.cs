using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<CallApiCallItem> CallApiCalls { get; } = [];
    public ObservableCollection<DeviceApiDefOptionItem> DeviceApiDefOptions { get; } = [];
    public ObservableCollection<ApiDefPanelItem> SystemApiDefs { get; } = [];
    public ObservableCollection<ConditionSectionItem> ConditionSections { get; } = [];

    public string CallApiCallsHeader    => $"ApiCalls [{CallApiCalls.Count}]";
    public string SystemApiDefsHeader   => $"ApiDefs [{SystemApiDefs.Count}]";

    [ObservableProperty] private bool _isWorkSelected;
    [ObservableProperty] private bool _isCallSelected;
    [ObservableProperty] private bool _isSystemSelected;
    [ObservableProperty] private string _workDurationText = string.Empty;
    [ObservableProperty] private string _callTimeoutText = string.Empty;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty] private string _nameEditorText = string.Empty;
    [ObservableProperty] private bool _isNameDirty;
    [ObservableProperty] private bool _isWorkDurationDirty;
    [ObservableProperty] private bool _isCallTimeoutDirty;

    private string _originalWorkDurationText = string.Empty;
    private string _originalCallTimeoutText = string.Empty;

    partial void OnNameEditorTextChanged(string value) =>
        IsNameDirty = !string.Equals(value.Trim(), SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);

    partial void OnWorkDurationTextChanged(string value) =>
        IsWorkDurationDirty = value != _originalWorkDurationText;

    partial void OnCallTimeoutTextChanged(string value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutText;

    partial void OnSelectedNodeChanged(EntityNode? value) => RefreshPropertyPanel();

    private void InitializePropertyPanelState()
    {
        CallApiCalls.CollectionChanged    += (_, _) => OnPropertyChanged(nameof(CallApiCallsHeader));
        SystemApiDefs.CollectionChanged   += (_, _) => OnPropertyChanged(nameof(SystemApiDefsHeader));
        EnsureConditionSectionsInitialized();
    }

    [RelayCommand]
    private void ApplyWorkDuration()
    {
        if (RequireSelectedAs(EntityTypes.Work) is not { } selectedWork) return;

        if (!TryEditorFunc(
                "TryUpdateWorkDuration",
                () => _editor.TryUpdateWorkDuration(selectedWork.Id, WorkDurationText),
                out var updated,
                fallback: false))
            return;

        if (!updated)
        {
            StatusText = "Invalid duration. Use hh:mm:ss or leave empty.";
            return;
        }

        _originalWorkDurationText = WorkDurationText;
        IsWorkDurationDirty = false;
        StatusText = "Work duration updated.";
    }

    private void RefreshPropertyPanel()
    {
        var selected = SelectedNode;
        NameEditorText = selected?.Name ?? string.Empty;
        IsWorkSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.Work);
        IsCallSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.Call);
        IsSystemSelected = EntityTypes.Is(selected?.EntityType, EntityTypes.System);

        if (IsWorkSelected && selected is not null)
        {
            if (!TryEditorFunc(
                    "GetWorkDurationText",
                    () => _editor.GetWorkDurationText(selected.Id),
                    out _originalWorkDurationText,
                    fallback: string.Empty))
                _originalWorkDurationText = string.Empty;

            WorkDurationText = _originalWorkDurationText;
        }
        else
        {
            _originalWorkDurationText = string.Empty;
            WorkDurationText = string.Empty;
        }

        if (IsCallSelected && selected is not null)
        {
            if (!TryEditorFunc(
                    "GetCallTimeoutText",
                    () => _editor.GetCallTimeoutText(selected.Id),
                    out _originalCallTimeoutText,
                    fallback: string.Empty))
                _originalCallTimeoutText = string.Empty;

            CallTimeoutText = _originalCallTimeoutText;
            RefreshCallPanel(selected.Id);
        }
        else
        {
            _originalCallTimeoutText = string.Empty;
            CallTimeoutText = string.Empty;
            CallApiCalls.Clear();
            DeviceApiDefOptions.Clear();
            SelectedCallApiCall = null;
            ClearConditionSections();
        }

        if (IsSystemSelected && selected is not null)
            RefreshSystemPanel(selected.Id);
        else
            SystemApiDefs.Clear();
    }

    private EntityNode? RequireSelectedAs(string entityType) =>
        SelectedNode is { } n && EntityTypes.Is(n.EntityType, entityType) ? n : null;

    private static Window? GetOwnerWindow()
    {
        if (Application.Current is null)
            return null;

        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    private static bool ShowOwnedDialog(Window dialog)
    {
        if (GetOwnerWindow() is { } owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }
}
