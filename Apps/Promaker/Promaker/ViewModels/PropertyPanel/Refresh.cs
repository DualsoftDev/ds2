using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
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
        ShowNameEditor = IsSingleSelection && selected is not null && selected.IsReference == false;
        OnPropertyChanged(nameof(ShowDebugSelectionDetails));

        SelectionTypeText = selectedKeys.Count switch
        {
            0 => "선택 없음",
            1 when selected?.IsReference == true => $"Type: {selected.EntityType} (Reference)",
            1 => $"Type: {selected?.EntityType}",
            _ when uniformKind is { } kind => $"Type: {kind} ({selectedKeys.Count} selected)",
            _ => $"Type: Mixed ({selectedKeys.Count} selected)"
        };
        SelectionNameText = selectedKeys.Count switch
        {
            0 => "",
            1 when IsSingleWorkSelected && selected is not null
                => $"Name: {Queries.tryGetWorkFullName(selected.Id, Store)?.Value ?? selected.Name}",
            1 => $"Name: {selected?.Name}",
            _ => $"{selectedKeys.Count} items selected"
        };

        var fullName = selected?.Name ?? string.Empty;
        if (IsSingleWorkSelected)
        {
            if (selected is not null)
            {
                var workName = Queries.tryGetWorkFullName(selected.Id, Store);
                if (workName != null) fullName = workName.Value;
            }
            var (prefix, localName) = TokenRoleOps.parseWorkNameParts(fullName);
            NamePrefix = prefix;
            NameEditorText = localName;
            NameSuffix = string.Empty;
        }
        else if (IsSingleCallSelected && fullName.LastIndexOf('.') is var dotIdx && dotIdx >= 0)
        {
            // Call.Name = "DevicesAlias.ApiName" — alias 만 편집, .ApiName 는 read-only
            NamePrefix = string.Empty;
            NameEditorText = fullName[..dotIdx];
            NameSuffix = fullName[dotIdx..];
        }
        else
        {
            NamePrefix = string.Empty;
            NameEditorText = fullName;
            NameSuffix = string.Empty;
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
                var devOpt = Queries.tryGetDeviceDurationMs(resolvedWorkId, Store);
                _deviceDurationMs = devOpt != null ? (int?)devOpt.Value : null;
                DeviceDurationHint = _deviceDurationMs is { } ms ? $"예상 소요 시간: {ms}ms" : "";

                var canonicalSelectedWorkId = Queries.resolveOriginalWorkId(selected.Id, Store);
                var linkedSpec = Queries.getTokenSpecs(Store)
                    .FirstOrDefault(s =>
                        s.WorkId is { } wid
                        && Queries.resolveOriginalWorkId(wid.Value, Store) == canonicalSelectedWorkId);
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

            var isFinishedValues = selectedWorkIds
                .Select(workId => Store.GetWorkIsFinished(workId))
                .Distinct().ToList();

            var workRoles = selectedWorkIds
                .Select(workId => Queries.getWork(workId, Store)?.Value.TokenRole ?? TokenRole.None)
                .ToList();
            _suppressPropertySync = true;
            IsWorkFinished = isFinishedValues.Count == 1 ? isFinishedValues[0] : null;
            IsTokenSource = TokenRoleOps.resolveTokenRoleFlagState(workRoles, TokenRole.Source);
            IsTokenIgnore = TokenRoleOps.resolveTokenRoleFlagState(workRoles, TokenRole.Ignore);
            IsTokenSink = TokenRoleOps.resolveTokenRoleFlagState(workRoles, TokenRole.Sink);
            _suppressPropertySync = false;
        }
        else
        {
            _originalWorkPeriodMs = null;
            WorkPeriodMs = null;
            _deviceDurationMs = null;
            DeviceDurationHint = "";
            _suppressPropertySync = true;
            IsWorkFinished = false;
            IsTokenSource = false;
            IsTokenIgnore = false;
            IsTokenSink = false;
            _suppressPropertySync = false;
            HasLinkedTokenSpec = false;
            LinkedTokenSpecLabel = "";
        }

        if (IsCallSelected)
        {
            var selectedCallIds = GetSelectedCallIds();
            var timeoutValues = selectedCallIds.Select(callId => Store.GetCallTimeoutMsOrNull(callId)).Distinct().ToList();
            _originalCallTimeoutMs = timeoutValues.Count == 1 ? timeoutValues[0] : null;
            CallTimeoutMs = _originalCallTimeoutMs;

            var callTypeValues = selectedCallIds
                .Select(callId => Queries.getCall(callId, Store)?.Value.GetSimulationProperties()?.Value.CallType ?? CallType.WaitForCompletion)
                .Distinct().ToList();
            _suppressPropertySync = true;
            SelectedCallType = callTypeValues.Count == 1 ? callTypeValues[0] : CallType.WaitForCompletion;
            _suppressPropertySync = false;
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
        {
            RefreshSystemPanel(selected.Id);
            RefreshUserTagsPanel(selected.Id);

            // Load SystemType
            var systemOpt = Queries.getSystem(selected.Id, Store);
            if (systemOpt != null && Microsoft.FSharp.Core.FSharpOption<DsSystem>.get_IsSome(systemOpt))
            {
                var systemTypeOpt = systemOpt.Value.SystemType;
                _originalSystemType = systemTypeOpt != null && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(systemTypeOpt)
                    ? systemTypeOpt.Value
                    : string.Empty;
            }
            else
            {
                _originalSystemType = string.Empty;
            }
            SystemType = _originalSystemType;
            IsSystemTypeDirty = false;
        }
        else
        {
            SystemApiDefs.Clear();
            UserTags.Clear();
            OnPropertyChanged(nameof(UserTagsHeader));
            _originalSystemType = string.Empty;
            SystemType = string.Empty;
            IsSystemTypeDirty = false;
        }
    }
}
