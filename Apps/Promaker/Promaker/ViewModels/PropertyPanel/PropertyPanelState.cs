using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class PropertyPanelState : ObservableObject
{
    private readonly MainViewModel.PropertyPanelHost _host;

    public PropertyPanelState(MainViewModel.PropertyPanelHost host)
    {
        _host = host;
        CallApiCalls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CallApiCallsHeader));
        SystemApiDefs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SystemApiDefsHeader));
        EnsureConditionSectionsInitialized();
    }

    private DsStore Store => _host.Store;

    public ObservableCollection<CallApiCallItem> CallApiCalls { get; } = [];
    public ObservableCollection<DeviceApiDefOptionItem> DeviceApiDefOptions { get; } = [];
    public ObservableCollection<ApiDefPanelItem> SystemApiDefs { get; } = [];
    public ObservableCollection<ConditionSectionItem> ConditionSections { get; } = [];

    public string CallApiCallsHeader => $"ApiCalls [{CallApiCalls.Count}]";
    public string SystemApiDefsHeader => $"ApiDefs [{SystemApiDefs.Count}]";
    public bool IsDebugBuild => MainViewModel.IsDebugBuild;

    [ObservableProperty] private EntityNode? _selectedNode;
    [ObservableProperty] private bool _isWorkSelected;
    [ObservableProperty] private bool _isCallSelected;
    [ObservableProperty] private bool _isSystemSelected;
    [ObservableProperty] private int? _workPeriodMs;
    [ObservableProperty] private bool _isTokenSource;
    [ObservableProperty] private bool _isTokenIgnore;
    [ObservableProperty] private bool _hasLinkedTokenSpec;
    [ObservableProperty] private string _linkedTokenSpecLabel = "";
    [ObservableProperty] private int? _callTimeoutMs;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty] private string _nameEditorText = string.Empty;
    [ObservableProperty] private bool _isNameDirty;
    [ObservableProperty] private bool _isWorkPeriodDirty;
    [ObservableProperty] private bool _isCallTimeoutDirty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceDuration))]
    private string _deviceDurationHint = "";
    public bool HasDeviceDuration => !string.IsNullOrEmpty(DeviceDurationHint);

    private int? _originalWorkPeriodMs;
    private int? _deviceDurationMs;
    private int? _originalCallTimeoutMs;

    partial void OnSelectedNodeChanged(EntityNode? value) => Refresh();

    partial void OnNameEditorTextChanged(string value) =>
        IsNameDirty = !string.Equals(value.Trim(), SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);

    private TokenRole _originalWorkTokenRole;
    private bool _suppressTokenRoleSync;

    private TokenRole CurrentTokenRole =>
        (IsTokenSource ? TokenRole.Source : TokenRole.None) |
        (IsTokenIgnore ? TokenRole.Ignore : TokenRole.None);

    private void SyncTokenRoleToStore()
    {
        if (_suppressTokenRoleSync) return;
        var newRole = CurrentTokenRole;
        if (newRole != _originalWorkTokenRole && RequireSelectedAs(EntityKind.Work) is { } work)
        {
            if (_host.TryAction(() => Store.UpdateWorkTokenRole(work.Id, newRole)))
                _originalWorkTokenRole = newRole;
            else
            {
                _suppressTokenRoleSync = true;
                IsTokenSource = _originalWorkTokenRole.HasFlag(TokenRole.Source);
                IsTokenIgnore = _originalWorkTokenRole.HasFlag(TokenRole.Ignore);
                _suppressTokenRoleSync = false;
            }
        }
    }

    partial void OnIsTokenSourceChanged(bool value) => SyncTokenRoleToStore();
    partial void OnIsTokenIgnoreChanged(bool value) => SyncTokenRoleToStore();

    partial void OnWorkPeriodMsChanged(int? value) =>
        IsWorkPeriodDirty = value != _originalWorkPeriodMs;

    partial void OnCallTimeoutMsChanged(int? value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutMs;

    public void SyncSelectedNode(EntityNode? value)
    {
        SelectedNode = value;
    }

    public void Refresh()
    {
        var selected = SelectedNode;
        NameEditorText = selected?.Name ?? string.Empty;
        IsWorkSelected = selected?.EntityType == EntityKind.Work;
        IsCallSelected = selected?.EntityType == EntityKind.Call;
        IsSystemSelected = selected?.EntityType == EntityKind.System;

        if (IsWorkSelected && selected is not null)
        {
            _originalWorkPeriodMs = LoadOptionalMsFromStore(selected.Id, Store.GetWorkPeriodMsOrNull);
            WorkPeriodMs = _originalWorkPeriodMs;
            var devOpt = DsQuery.tryGetDeviceDurationMs(selected.Id, Store);
            _deviceDurationMs = devOpt != null ? (int?)devOpt.Value : null;
            DeviceDurationHint = _deviceDurationMs is { } ms ? $"예상 소요 시간: {ms}ms" : "";

            var workOpt = Ds2.Store.DsQuery.getWork(selected.Id, Store);
            _originalWorkTokenRole = workOpt != null ? workOpt.Value.TokenRole : TokenRole.None;
            _suppressTokenRoleSync = true;
            IsTokenSource = _originalWorkTokenRole.HasFlag(TokenRole.Source);
            IsTokenIgnore = _originalWorkTokenRole.HasFlag(TokenRole.Ignore);
            _suppressTokenRoleSync = false;

            // 연결된 TokenSpec 표시
            var linkedSpec = DsQuery.getTokenSpecs(Store)
                .FirstOrDefault(s => s.WorkId is { } wid && wid.Value == selected.Id);
            HasLinkedTokenSpec = linkedSpec is not null;
            LinkedTokenSpecLabel = linkedSpec is not null ? $"#{linkedSpec.Id} {linkedSpec.Label}" : "";
        }
        else
        {
            _originalWorkPeriodMs = null;
            WorkPeriodMs = null;
            _deviceDurationMs = null;
            DeviceDurationHint = "";
            _originalWorkTokenRole = TokenRole.None;
            _suppressTokenRoleSync = true;
            IsTokenSource = false;
            IsTokenIgnore = false;
            _suppressTokenRoleSync = false;
            HasLinkedTokenSpec = false;
            LinkedTokenSpecLabel = "";
        }

        if (IsCallSelected && selected is not null)
        {
            _originalCallTimeoutMs = LoadOptionalMsFromStore(selected.Id, Store.GetCallTimeoutMsOrNull);
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

    public void ApplyEntityRename(Guid entityId, string newName)
    {
        if (SelectedNode is { Id: var selectedId } && selectedId == entityId)
        {
            NameEditorText = newName;
            IsNameDirty = false;
        }
    }

    [RelayCommand]
    private void ApplyName()
    {
        if (SelectedNode is null) return;

        var newName = NameEditorText.Trim();
        if (!string.IsNullOrEmpty(newName))
            _host.RenameSelected(newName);
    }

    [RelayCommand]
    private void ApplyWorkPeriod()
    {
        if (RequireSelectedAs(EntityKind.Work) is not { } selectedWork) return;

        if (_deviceDurationMs is { } devMs)
        {
            var userMs = WorkPeriodMs ?? 0;
            var ruleText = userMs > devMs
                ? $"설정값({userMs}ms)이 예상 시간({devMs}ms)보다 크므로 설정값이 적용됩니다."
                : $"설정값({userMs}ms)이 예상 시간({devMs}ms)보다 작으므로 예상 시간이 우선됩니다.";
            var result = Dialogs.DialogHelpers.ShowThemedMessageBox(
                $"이 Work의 예상 소요 시간이 {devMs}ms로 산출되어 있습니다.\n" +
                $"{ruleText}\n\n계속하시겠습니까?",
                "Duration 안내",
                System.Windows.MessageBoxButton.YesNo, "ℹ");
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        if (!_host.TryAction(() => Store.UpdateWorkPeriodMs(selectedWork.Id, WorkPeriodMs)))
            return;

        _originalWorkPeriodMs = WorkPeriodMs;
        IsWorkPeriodDirty = false;
        _host.SetStatusText("Work period updated.");
    }

    private int? LoadOptionalMsFromStore(Guid entityId, Func<Guid, int?> getter)
    {
        if (_host.TryFunc(() => getter(entityId), out int? value, null))
            return value;
        return null;
    }

    private EntityNode? RequireSelectedAs(EntityKind entityType) =>
        SelectedNode is { } n && n.EntityType == entityType ? n : null;

    private bool TryGetSelectedNode(EntityKind entityType, [NotNullWhen(true)] out EntityNode? selectedNode)
    {
        selectedNode = RequireSelectedAs(entityType);
        return selectedNode is not null;
    }

    private bool ShowOwnedDialog(Window dialog) => _host.ShowOwnedDialog(dialog);

    private static void ReplaceAll<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
