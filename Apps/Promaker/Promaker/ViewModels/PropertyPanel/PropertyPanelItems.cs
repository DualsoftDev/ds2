using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public sealed class CallApiCallItem : ObservableObject
{
    private readonly Guid? _originalApiDefId;
    private readonly string _originalName;
    private readonly string _originalOutputTagName;
    private readonly string _originalOutputAddress;
    private readonly string _originalInputTagName;
    private readonly string _originalInputAddress;
    private readonly string _originalValueSpecText;
    private readonly string _originalInputValueSpecText;

    private Guid? _apiDefId;
    private string _name;
    private string _outputTagName;
    private string _outputAddress;
    private string _inputTagName;
    private string _inputAddress;
    private string _valueSpecText;
    private string _inputValueSpecText;
    private bool _isDirty;

    public CallApiCallItem(
        Guid apiCallId,
        string name,
        Guid? apiDefId,
        string apiDefDisplayName,
        string outputTagName,
        string outputAddress,
        string inputTagName,
        string inputAddress,
        string valueSpecText,
        string inputValueSpecText,
        int outputSpecTypeIndex,
        int inputSpecTypeIndex)
    {
        ApiCallId                   = apiCallId;
        ApiDefDisplayName           = apiDefDisplayName;
        OutputSpecTypeIndex         = outputSpecTypeIndex;
        InputSpecTypeIndex          = inputSpecTypeIndex;

        _apiDefId                   = apiDefId;
        _name                       = name;
        _outputTagName              = outputTagName;
        _outputAddress              = outputAddress;
        _inputTagName               = inputTagName;
        _inputAddress               = inputAddress;
        _valueSpecText              = valueSpecText;
        _inputValueSpecText         = inputValueSpecText;

        _originalApiDefId           = _apiDefId;
        _originalName               = _name;
        _originalOutputTagName      = _outputTagName;
        _originalOutputAddress      = _outputAddress;
        _originalInputTagName       = _inputTagName;
        _originalInputAddress       = _inputAddress;
        _originalValueSpecText      = _valueSpecText;
        _originalInputValueSpecText = _inputValueSpecText;
    }

    public static CallApiCallItem FromPanel(CallApiCallPanelItem row) =>
        new(row.ApiCallId, row.Name, row.ApiDefIdOrNull,
            row.ApiDefDisplayName,
            row.OutputTagName, row.OutputAddress,
            row.InputTagName, row.InputAddress,
            row.ValueSpecText, row.InputValueSpecText,
            row.OutputSpecTypeIndex, row.InputSpecTypeIndex);

    public Guid ApiCallId { get; }
    public string ApiDefDisplayName { get; }
    public int OutputSpecTypeIndex { get; }
    public int InputSpecTypeIndex  { get; }

    public Guid? ApiDefId
    {
        get => _apiDefId;
        set { if (SetProperty(ref _apiDefId, value)) RefreshDirtyState(); }
    }

    public string Name            { get => _name;            set => SetStr(ref _name, value); }
    public string OutputTagName   { get => _outputTagName;   set => SetStr(ref _outputTagName, value); }
    public string OutputAddress   { get => _outputAddress;   set => SetStr(ref _outputAddress, value); }
    public string InputTagName    { get => _inputTagName;    set => SetStr(ref _inputTagName, value); }
    public string InputAddress    { get => _inputAddress;    set => SetStr(ref _inputAddress, value); }
    public string ValueSpecText   { get => _valueSpecText;   set => SetStr(ref _valueSpecText, value); }
    public string InputValueSpecText { get => _inputValueSpecText; set => SetStr(ref _inputValueSpecText, value); }

    private void SetStr(ref string field, string? value)
    {
        if (SetProperty(ref field, value ?? string.Empty))
            RefreshDirtyState();
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private void RefreshDirtyState()
    {
        IsDirty =
            _originalApiDefId != _apiDefId ||
            !String.Equals(_originalName, _name, StringComparison.Ordinal) ||
            !String.Equals(_originalOutputTagName, _outputTagName, StringComparison.Ordinal) ||
            !String.Equals(_originalOutputAddress, _outputAddress, StringComparison.Ordinal) ||
            !String.Equals(_originalInputTagName, _inputTagName, StringComparison.Ordinal) ||
            !String.Equals(_originalInputAddress, _inputAddress, StringComparison.Ordinal) ||
            !String.Equals(_originalValueSpecText, _valueSpecText, StringComparison.Ordinal) ||
            !String.Equals(_originalInputValueSpecText, _inputValueSpecText, StringComparison.Ordinal);
    }
}

public sealed class DeviceApiDefOptionItem(
    Guid id,
    string deviceName,
    string apiDefName,
    string displayName)
{
    public Guid Id { get; } = id;
    public string DeviceName { get; } = deviceName;
    public string ApiDefName { get; } = apiDefName;
    public string DisplayName { get; } = displayName;
}

public sealed class CallConditionItem
{
    public CallConditionItem(Guid callId, CallConditionPanelItem panel)
    {
        CallId        = callId;
        ConditionId   = panel.ConditionId;
        ConditionType = panel.ConditionType;
        IsOR          = panel.IsOR;
        IsRising      = panel.IsRising;
        FormulaText   = panel.FormulaText();
        Items = panel.Items
            .Select(x => new ConditionApiCallRow(callId, panel.ConditionId, x))
            .ToList();
        Children = panel.Children
            .Select(c => new CallConditionItem(callId, c))
            .ToList();
    }

    public Guid               CallId        { get; }
    public Guid               ConditionId   { get; }
    public CallConditionType ConditionType  { get; }
    public bool               IsOR          { get; }
    public bool               IsRising      { get; }
    public string             FormulaText   { get; }
    public IReadOnlyList<ConditionApiCallRow> Items { get; }
    public IReadOnlyList<CallConditionItem> Children { get; }
}

public sealed class ConditionApiCallRow
{
    public ConditionApiCallRow(Guid callId, Guid conditionId, CallConditionApiCallItem item)
    {
        CallId               = callId;
        ConditionId          = conditionId;
        ApiCallId            = item.ApiCallId;
        ApiCallName          = item.ApiCallName;
        ApiDefDisplayName    = item.ApiDefDisplayName;
        OutputSpecText       = item.OutputSpecText;
        OutputSpecTypeIndex  = item.OutputSpecTypeIndex;
        InputSpecText        = item.InputSpecText;
        InputSpecTypeIndex   = item.InputSpecTypeIndex;
    }

    public Guid   CallId               { get; }
    public Guid   ConditionId          { get; }
    public Guid   ApiCallId            { get; }
    public string ApiCallName          { get; }
    public string ApiDefDisplayName    { get; }
    public string OutputSpecText       { get; }
    public int    OutputSpecTypeIndex  { get; }
    public string InputSpecText        { get; }
    public int    InputSpecTypeIndex   { get; }
}

public sealed class ConditionSectionItem : ObservableObject
{
    public ConditionSectionItem(CallConditionType conditionType, string title, string addToolTip)
    {
        ConditionType = conditionType;
        Title = title;
        AddToolTip = addToolTip;
        Conditions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Header));
    }

    public CallConditionType ConditionType { get; }
    public string Title { get; }
    public string AddToolTip { get; }
    public ObservableCollection<CallConditionItem> Conditions { get; } = [];
    public string Header => $"{Title} [{Conditions.Count}]";
    public string HelpTopic => ConditionType switch
    {
        CallConditionType.AutoAux     => "condition-auto-aux",
        CallConditionType.ComAux      => "condition-com-aux",
        CallConditionType.SkipUnmatch => "condition-skip-unmatch",
        _                             => "condition"
    };
}

public sealed class ConditionDropInfo(CallConditionType conditionType, Guid droppedCallId)
{
    public CallConditionType ConditionType { get; } = conditionType;
    public Guid DroppedCallId { get; } = droppedCallId;
}

public sealed class ConditionItemDropInfo(Guid conditionId, Guid droppedCallId)
{
    public Guid ConditionId { get; } = conditionId;
    public Guid DroppedCallId { get; } = droppedCallId;
}
