using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
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
    [ObservableProperty] private string _systemType = string.Empty;
    [ObservableProperty] private bool _isSystemTypeDirty;
    [ObservableProperty] private string _selectionTypeText = "선택 없음";
    [ObservableProperty] private string _selectionNameText = "";
    [ObservableProperty] private bool _showNameEditor;
    [ObservableProperty] private int? _workPeriodMs;
    [ObservableProperty] private bool? _isWorkFinished;
    [ObservableProperty] private bool? _isTokenSource;
    [ObservableProperty] private bool? _isTokenIgnore;
    [ObservableProperty] private bool? _isTokenSink;
    [ObservableProperty] private bool _hasLinkedTokenSpec;
    [ObservableProperty] private string _linkedTokenSpecLabel = "";
    [ObservableProperty] private int? _callTimeoutMs;
    [ObservableProperty] private CallType _selectedCallType = CallType.WaitForCompletion;
    [ObservableProperty] private CallApiCallItem? _selectedCallApiCall;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyNameCommand))]
    private string _nameEditorText = string.Empty;
    [ObservableProperty] private string _namePrefix = string.Empty;
    [ObservableProperty] private string _nameSuffix = string.Empty;
    [ObservableProperty] private bool _isNameDirty;
    [ObservableProperty] private bool _isNameEditHighlighted;
    [ObservableProperty] private bool _isWorkPeriodDirty;
    [ObservableProperty] private bool _isCallTimeoutDirty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceDuration))]
    private string _deviceDurationHint = "";
    public bool HasDeviceDuration => !string.IsNullOrEmpty(DeviceDurationHint);

    private int? _originalWorkPeriodMs;
    private int? _deviceDurationMs;
    private int? _originalCallTimeoutMs;
    private string _originalSystemType = string.Empty;

    partial void OnNameEditorTextChanged(string value)
    {
        var currentFull = NamePrefix + value.Trim() + NameSuffix;
        IsNameDirty = !string.Equals(currentFull, SelectedNode?.Name ?? string.Empty, StringComparison.Ordinal);
    }
    private bool _suppressPropertySync;

    partial void OnIsWorkFinishedChanged(bool? value) => SyncIsFinishedFlag(value);
    partial void OnIsTokenSourceChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Source, value);
    partial void OnIsTokenIgnoreChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Ignore, value);
    partial void OnIsTokenSinkChanged(bool? value) => SyncTokenRoleFlag(TokenRole.Sink, value);

    partial void OnWorkPeriodMsChanged(int? value) =>
        IsWorkPeriodDirty = value != _originalWorkPeriodMs;

    partial void OnCallTimeoutMsChanged(int? value) =>
        IsCallTimeoutDirty = value != _originalCallTimeoutMs;

    partial void OnSelectedCallTypeChanged(CallType value) => SyncCallType(value);

    partial void OnSystemTypeChanged(string value) =>
        IsSystemTypeDirty = !string.Equals(value, _originalSystemType, StringComparison.Ordinal);

    public void SyncSelection(EntityNode? value, IReadOnlyList<SelectionKey> orderedSelection)
    {
        IsNameEditHighlighted = false;
        _selectedNodeKeys = orderedSelection.Count > 0
            ? orderedSelection.ToList()
            : value is null
                ? []
                : [new SelectionKey(value.Id, value.EntityType)];
        SelectedNode = value;
        Refresh();
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
            .Select(key => Queries.resolveOriginalWorkId(key.Id, Store))
            .Distinct()
            .ToList();

    private IReadOnlyList<Guid> GetSelectedCallIds() =>
        _selectedNodeKeys
            .Where(key => key.EntityKind == EntityKind.Call)
            .Select(key => Queries.resolveOriginalCallId(key.Id, Store))
            .Distinct()
            .ToList();

    private void SyncIsFinishedFlag(bool? value)
    {
        if (_suppressPropertySync)
            return;
        if (value is null)
            value = false;

        var selectedWorkIds = GetSelectedCanonicalWorkIds();
        if (selectedWorkIds.Count == 0)
            return;

        if (!GuardSimulationSemanticEdit("Work IsFinished 변경"))
        {
            Refresh();
            return;
        }

        var changes = selectedWorkIds
            .Select(workId => new ValueTuple<Guid, bool>(workId, value.Value))
            .ToList();

        if (!_host.TryAction(() => Store.UpdateWorkIsFinishedBatch(changes)))
        {
            Refresh();
            return;
        }

        _host.SetStatusText(selectedWorkIds.Count > 1
            ? $"IsFinished updated for {selectedWorkIds.Count} items."
            : "Work IsFinished updated.");
    }

    private void SyncTokenRoleFlag(TokenRole flag, bool? value)
    {
        if (_suppressPropertySync)
            return;
        // IsThreeState 체크박스: true→null→false 순환에서 null은 "해제" 의도
        if (value is null)
            value = false;

        var selectedWorkIds = GetSelectedCanonicalWorkIds();
        if (selectedWorkIds.Count == 0)
            return;

        if (!GuardSimulationSemanticEdit("Work token role 변경"))
        {
            Refresh();
            return;
        }

        var changes = selectedWorkIds
            .Select(workId =>
            {
                var currentRole = Queries.getWork(workId, Store)?.Value.TokenRole ?? TokenRole.None;
                var nextRole = TokenRoleOps.computeNextTokenRole(currentRole, flag, value.Value);
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

    private void SyncCallType(CallType value)
    {
        if (_suppressPropertySync)
            return;

        var selectedCallIds = GetSelectedCallIds();
        if (selectedCallIds.Count == 0)
            return;

        if (!GuardSimulationSemanticEdit("Call type 변경"))
        {
            Refresh();
            return;
        }

        if (!_host.TryAction(() =>
        {
            foreach (var callId in selectedCallIds)
            {
                var call = Queries.getCall(callId, Store)?.Value;
                if (call != null && (call.GetSimulationProperties()?.Value.CallType ?? CallType.WaitForCompletion) != value)
                    Store.UpdateCallType(callId, value);
            }
        }))
        {
            Refresh();
            return;
        }

        _host.SetStatusText(selectedCallIds.Count > 1
            ? $"CallType updated for {selectedCallIds.Count} items."
            : "Call type updated.");
    }
}
