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
    private List<SelectionKey> _selectedNodeKeys = [];

    internal MainViewModel.PropertyPanelHost Host => _host;

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
    public bool ShowDebugSelectionDetails => IsDebugBuild && IsSingleSelection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyNameCommand))]
    private EntityNode? _selectedNode;
    [ObservableProperty] private bool _isSingleSelection;
    [ObservableProperty] private bool _isMultiSelection;
    [ObservableProperty] private int _selectedNodeCount;
    [ObservableProperty] private bool _isSingleWorkSelected;
    [ObservableProperty] private bool _isSingleCallSelected;
    [ObservableProperty] private bool _isWorkSelected;
    [ObservableProperty] private bool _isCallSelected;
    [ObservableProperty] private bool _isSystemSelected;
    [ObservableProperty] private string _selectionTypeText = "선택 없음";
    [ObservableProperty] private string _selectionNameText = "";
    [ObservableProperty] private bool _showNameEditor;
    [ObservableProperty] private int? _workPeriodMs;
    [ObservableProperty] private bool? _isTokenSource;
    [ObservableProperty] private bool? _isTokenIgnore;
    [ObservableProperty] private bool? _isTokenSink;
    [ObservableProperty] private bool _hasLinkedTokenSpec;
    [ObservableProperty] private string _linkedTokenSpecLabel = "";
    [ObservableProperty] private int? _callTimeoutMs;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyNameCommand))]
    private string _nameEditorText = string.Empty;
    [ObservableProperty] private string _namePrefix = string.Empty;
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

    partial void OnNameEditorTextChanged(string value)
    {
        var currentFull = NamePrefix + value.Trim();
        IsNameDirty = !string.Equals(currentFull, SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);
    }
    private bool _suppressTokenRoleSync;

    partial void OnIsTokenSourceChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Source, value);
    partial void OnIsTokenIgnoreChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Ignore, value);
    partial void OnIsTokenSinkChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Sink, value);

    partial void OnWorkPeriodMsChanged(int? value) =>
        IsWorkPeriodDirty = value != _originalWorkPeriodMs;

    partial void OnCallTimeoutMsChanged(int? value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutMs;

    public void SyncSelection(EntityNode? value, IReadOnlyList<SelectionKey> orderedSelection)
    {
        _selectedNodeKeys = orderedSelection.Count > 0
            ? orderedSelection.ToList()
            : value is null
                ? []
                : [new SelectionKey(value.Id, value.EntityType)];
        SelectedNode = value;
        Refresh();
    }

    public void Refresh()
    {
        var selected = SelectedNode;
        var selectedKeys = _selectedNodeKeys;
        var uniformKind = selectedKeys.Count > 0 && selectedKeys.All(key => key.EntityKind == selectedKeys[0].EntityKind)
            ? selectedKeys[0].EntityKind
            : (EntityKind?)null;

        SelectedNodeCount = selectedKeys.Count;
        IsSingleSelection = selectedKeys.Count == 1;
        IsMultiSelection = selectedKeys.Count > 1;
        IsSingleWorkSelected = IsSingleSelection && uniformKind == EntityKind.Work;
        IsSingleCallSelected = IsSingleSelection && uniformKind == EntityKind.Call;
        IsWorkSelected = uniformKind == EntityKind.Work;
        IsCallSelected = uniformKind == EntityKind.Call;
        IsSystemSelected = IsSingleSelection && uniformKind == EntityKind.System;
        ShowNameEditor = IsSingleSelection && selected is not null;
        OnPropertyChanged(nameof(ShowDebugSelectionDetails));

        SelectionTypeText = selectedKeys.Count switch
        {
            0 => "선택 없음",
            1 => $"Type: {selected?.EntityType}",
            _ when uniformKind is { } kind => $"Type: {kind} ({selectedKeys.Count} selected)",
            _ => $"Type: Mixed ({selectedKeys.Count} selected)"
        };
        SelectionNameText = selectedKeys.Count switch
        {
            0 => "",
            1 => $"Name: {selected?.Name}",
            _ => $"{selectedKeys.Count} items selected"
        };

        var fullName = selected?.Name ?? string.Empty;
        // Work 이름: "FlowPrefix.LocalName" → prefix를 분리해서 텍스트박스에는 LocalName만
        if (IsSingleWorkSelected && fullName.IndexOf('.') is var dotIdx && dotIdx >= 0)
        {
            NamePrefix = fullName[..(dotIdx + 1)]; // "FlowName."
            NameEditorText = fullName[(dotIdx + 1)..];
        }
        else
        {
            NamePrefix = string.Empty;
            NameEditorText = fullName;
        }
        if (IsWorkSelected)
        {
            var selectedWorkIds = GetSelectedCanonicalWorkIds();
            var periodValues = selectedWorkIds.Select(workId => Store.GetWorkPeriodMsOrNull(workId)).Distinct().ToList();
            _originalWorkPeriodMs = periodValues.Count == 1 ? periodValues[0] : null;
            WorkPeriodMs = _originalWorkPeriodMs;

            if (IsSingleWorkSelected && selected is not null)
            {
                var resolvedWorkId = selected.ReferenceOfId ?? selected.Id;
                var devOpt = DsQuery.tryGetDeviceDurationMs(resolvedWorkId, Store);
                _deviceDurationMs = devOpt != null ? (int?)devOpt.Value : null;
                DeviceDurationHint = _deviceDurationMs is { } ms ? $"예상 소요 시간: {ms}ms" : "";

                var canonicalSelectedWorkId = DsQuery.resolveOriginalWorkId(selected.Id, Store);
                var linkedSpec = DsQuery.getTokenSpecs(Store)
                    .FirstOrDefault(s =>
                        s.WorkId is { } wid
                        && DsQuery.resolveOriginalWorkId(wid.Value, Store) == canonicalSelectedWorkId);
                HasLinkedTokenSpec = linkedSpec is not null;
                LinkedTokenSpecLabel = linkedSpec is not null ? $"#{linkedSpec.Id} {linkedSpec.Label}" : "";
            }
            else
            {
                _deviceDurationMs = null;
                DeviceDurationHint = "";
                HasLinkedTokenSpec = false;
                LinkedTokenSpecLabel = "";
            }

            var workRoles = selectedWorkIds
                .Select(workId => DsQuery.getWork(workId, Store)?.Value.TokenRole ?? TokenRole.None)
                .ToList();
            _suppressTokenRoleSync = true;
            IsTokenSource = ResolveTokenRoleFlagState(workRoles, TokenRole.Source);
            IsTokenIgnore = ResolveTokenRoleFlagState(workRoles, TokenRole.Ignore);
            IsTokenSink = ResolveTokenRoleFlagState(workRoles, TokenRole.Sink);
            _suppressTokenRoleSync = false;
        }
        else
        {
            _originalWorkPeriodMs = null;
            WorkPeriodMs = null;
            _deviceDurationMs = null;
            DeviceDurationHint = "";
            _suppressTokenRoleSync = true;
            IsTokenSource = false;
            IsTokenIgnore = false;
            IsTokenSink = false;
            _suppressTokenRoleSync = false;
            HasLinkedTokenSpec = false;
            LinkedTokenSpecLabel = "";
        }

        if (IsCallSelected)
        {
            var selectedCallIds = GetSelectedCallIds();
            var timeoutValues = selectedCallIds.Select(callId => Store.GetCallTimeoutMsOrNull(callId)).Distinct().ToList();
            _originalCallTimeoutMs = timeoutValues.Count == 1 ? timeoutValues[0] : null;
            CallTimeoutMs = _originalCallTimeoutMs;
            if (IsSingleCallSelected && selected is not null)
                RefreshCallPanel(selected.Id);
            else
            {
                CallApiCalls.Clear();
                DeviceApiDefOptions.Clear();
                SelectedCallApiCall = null;
                ClearConditionSections();
            }
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
            if (SelectedNode.EntityType == EntityKind.Work && newName.IndexOf('.') is var dotIdx && dotIdx >= 0)
            {
                NamePrefix = newName[..(dotIdx + 1)];
                NameEditorText = newName[(dotIdx + 1)..];
            }
            else
            {
                NamePrefix = string.Empty;
                NameEditorText = newName;
            }
            IsNameDirty = false;
        }
    }

    private bool CanApplyName() =>
        IsSingleSelection && SelectedNode is not null && !string.IsNullOrWhiteSpace(NameEditorText);

    [RelayCommand(CanExecute = nameof(CanApplyName))]
    private void ApplyName()
    {
        if (SelectedNode is null) return;

        var localName = NameEditorText.Trim();
        if (string.IsNullOrEmpty(localName))
        {
            _host.SetStatusText("Name cannot be empty.");
            return;
        }

        // prefix가 있으면 전체 이름으로 전달 (RenameEntity가 다시 분리함)
        var newName = string.IsNullOrEmpty(NamePrefix)
            ? localName
            : NamePrefix + localName;
        _host.RenameSelected(newName);
    }

    [RelayCommand]
    private void ApplyWorkPeriod()
    {
        var selectedWorkIds = GetSelectedCanonicalWorkIds();
        if (selectedWorkIds.Count == 0) return;

        if (IsSingleWorkSelected && _deviceDurationMs is { } devMs)
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

        var changeValue = WorkPeriodMs;
        var changes = selectedWorkIds.Select(workId => new ValueTuple<Guid, int?>(workId, changeValue)).ToList();

        if (!_host.TryAction(() => Store.UpdateWorkPeriodsBatch(changes)))
            return;

        _originalWorkPeriodMs = WorkPeriodMs;
        IsWorkPeriodDirty = false;
        _host.SetStatusText(selectedWorkIds.Count > 1
            ? $"Work period updated for {selectedWorkIds.Count} items."
            : "Work period updated.");
        Refresh();
    }

    private int? LoadOptionalMsFromStore(Guid entityId, Func<Guid, int?> getter)
    {
        if (_host.TryFunc(() => getter(entityId), out int? value, null))
            return value;
        return null;
    }

    private EntityNode? RequireSelectedAs(EntityKind entityType) =>
        IsSingleSelection && SelectedNode is { } n && n.EntityType == entityType ? n : null;

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

    private IReadOnlyList<Guid> GetSelectedCanonicalWorkIds() =>
        _selectedNodeKeys
            .Where(key => key.EntityKind == EntityKind.Work)
            .Select(key => DsQuery.resolveOriginalWorkId(key.Id, Store))
            .Distinct()
            .ToList();

    private IReadOnlyList<Guid> GetSelectedCallIds() =>
        _selectedNodeKeys
            .Where(key => key.EntityKind == EntityKind.Call)
            .Select(key => key.Id)
            .Distinct()
            .ToList();

    private static bool? ResolveTokenRoleFlagState(IReadOnlyList<TokenRole> roles, TokenRole flag)
    {
        if (roles.Count == 0)
            return false;

        var first = roles[0].HasFlag(flag);
        return roles.All(role => role.HasFlag(flag)) == roles.Any(role => role.HasFlag(flag))
            ? first
            : null;
    }

    private void SyncTokenRoleFlag(TokenRole flag, bool? value)
    {
        if (_suppressTokenRoleSync || value is null)
            return;

        var selectedWorkIds = GetSelectedCanonicalWorkIds();
        if (selectedWorkIds.Count == 0)
            return;

        var changes = selectedWorkIds
            .Select(workId =>
            {
                var currentRole = DsQuery.getWork(workId, Store)?.Value.TokenRole ?? TokenRole.None;
                var nextRole = value.Value ? currentRole | flag : currentRole & ~flag;
                return new ValueTuple<Guid, TokenRole>(workId, nextRole);
            })
            .ToList();

        if (!_host.TryAction(() => Store.UpdateWorkTokenRolesBatch(changes)))
        {
            Refresh();
            return;
        }

        _host.SetStatusText(selectedWorkIds.Count > 1
            ? $"Token role updated for {selectedWorkIds.Count} items."
            : "Work token role updated.");
        Refresh();
    }
}
