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

        // PropertyPanel 의 모든 panel 슬롯(summary/name parts/work batch/call batch/system type)을
        // 단일 F# projection 호출로 산출. 향후 ApiDef/ApiCall v10 슬롯 추가 시 이 한 줄에 자동 합류.
        var projection = EditorSelectionProjection.Build(
            Store,
            selectedKeys,
            selected?.Id,
            selected?.EntityType,
            selected?.Name ?? string.Empty,
            selected?.ReferenceOfId);

        var summary = projection.Summary;
        EntityKind? uniformKind = summary.UniformKind;

        SelectedNodeCount = summary.Count;
        IsSingleSelection = summary.IsSingleSelection;
        IsMultiSelection = summary.IsMultiSelection;
        IsSingleWorkSelected = summary.IsSingleWorkSelected;
        IsSingleCallSelected = summary.IsSingleCallSelected;
        IsWorkSelected = summary.IsWorkSelected;
        IsCallSelected = summary.IsCallSelected;
        IsSystemSelected = summary.IsSingleSystemSelected;
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
            1 when IsSingleWorkSelected
                => $"Name: {projection.NameParts.Prefix}{projection.NameParts.Editable}{projection.NameParts.Suffix}",
            1 => $"Name: {selected?.Name}",
            _ => $"{selectedKeys.Count} items selected"
        };

        NamePrefix = projection.NameParts.Prefix;
        NameEditorText = projection.NameParts.Editable;
        NameSuffix = projection.NameParts.Suffix;

        // ── Work 영역 ─────────────────────────────────────────────
        var ws = projection.WorkState;
        _originalWorkPeriodMs = ws.PeriodMs;
        WorkPeriodMs = _originalWorkPeriodMs;
        _deviceDurationMs = ws.DeviceDurationMs;
        DeviceDurationHint = ws.DeviceDurationHint;
        HasLinkedTokenSpec = ws.HasLinkedTokenSpec;
        LinkedTokenSpecLabel = ws.LinkedTokenSpecLabel;
        _suppressPropertySync = true;
        IsWorkFinished = ws.IsWorkFinished;
        IsTokenSource = ws.TokenSourceState;
        IsTokenIgnore = ws.TokenIgnoreState;
        IsTokenSink = ws.TokenSinkState;
        _suppressPropertySync = false;

        // ── Call 영역 ─────────────────────────────────────────────
        var cs = projection.CallState;
        _originalCallTimeoutMs = cs.TimeoutMs;
        CallTimeoutMs = _originalCallTimeoutMs;
        _suppressPropertySync = true;
        SelectedCallType = cs.CallType;
        _suppressPropertySync = false;
        if (IsCallSelected && IsSingleCallSelected && selected is not null)
        {
            RefreshCallPanel(selected.Id);
        }
        else
        {
            CallApiCalls.Clear();
            DeviceApiDefOptions.Clear();
            SelectedCallApiCall = null;
            ClearConditionSections();
        }

        // ── System 영역 ───────────────────────────────────────────
        if (IsSystemSelected && selected is not null)
        {
            RefreshSystemPanel(selected.Id);
            RefreshUserTagsPanel(selected.Id);
            _originalSystemType = projection.SystemType;
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
