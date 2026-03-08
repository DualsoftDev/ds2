using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;
using Promaker;

namespace Promaker.ViewModels;

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
    [ObservableProperty] private int? _workPeriodMs;
    [ObservableProperty] private int? _callTimeoutMs;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty] private string _nameEditorText = string.Empty;
    [ObservableProperty] private bool _isNameDirty;
    [ObservableProperty] private bool _isWorkPeriodDirty;
    [ObservableProperty] private bool _isCallTimeoutDirty;

    private int? _originalWorkPeriodMs;
    private int? _originalCallTimeoutMs;

    partial void OnNameEditorTextChanged(string value) =>
        IsNameDirty = !string.Equals(value.Trim(), SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);

    partial void OnWorkPeriodMsChanged(int? value) =>
        IsWorkPeriodDirty = value != _originalWorkPeriodMs;

    partial void OnCallTimeoutMsChanged(int? value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutMs;

    partial void OnSelectedNodeChanged(EntityNode? value) => RefreshPropertyPanel();

    private void InitializePropertyPanelState()
    {
        CallApiCalls.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(CallApiCallsHeader));
        SystemApiDefs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SystemApiDefsHeader));
        EnsureConditionSectionsInitialized();
    }

    [RelayCommand]
    private void ApplyWorkPeriod()
    {
        if (RequireSelectedAs(EntityTypes.Work) is not { } selectedWork) return;

        if (!TryEditorAction(
                () => _store.UpdateWorkPeriodMs(selectedWork.Id, ToOption(WorkPeriodMs))))
            return;

        _originalWorkPeriodMs = WorkPeriodMs;
        IsWorkPeriodDirty = false;
        StatusText = "Work period updated.";
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
            _originalWorkPeriodMs = LoadOptionalMsFromStore(selected.Id, _store.GetWorkPeriodMs);
            WorkPeriodMs = _originalWorkPeriodMs;
        }
        else
        {
            _originalWorkPeriodMs = null;
            WorkPeriodMs = null;
        }

        if (IsCallSelected && selected is not null)
        {
            _originalCallTimeoutMs = LoadOptionalMsFromStore(selected.Id, _store.GetCallTimeoutMs);
            CallTimeoutMs = _originalCallTimeoutMs;
            RefreshCallPanel(selected.Id);
        }
        else
        {
            _originalCallTimeoutMs = null;
            CallTimeoutMs = null;
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

    private int? LoadOptionalMsFromStore(Guid entityId, Func<Guid, FSharpOption<int>> getter)
    {
        if (TryEditorFunc(() => getter(entityId), out var opt, fallback: null))
            return opt?.Value;
        return null;
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
