using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.UI.Core;

namespace Ds2.UI.Frontend.ViewModels;

public sealed class CallApiCallItem : ObservableObject
{
    private readonly Guid? _originalApiDefId;
    private readonly string _originalName;
    private readonly string _originalOutputAddress;
    private readonly string _originalInputAddress;
    private readonly string _originalValueSpecText;
    private readonly string _originalInputValueSpecText;

    private Guid? _apiDefId;
    private string _name;
    private string _outputAddress;
    private string _inputAddress;
    private string _valueSpecText;
    private string _inputValueSpecText;
    private bool _isDirty;

    public CallApiCallItem(
        Guid apiCallId,
        string name,
        Guid apiDefId,
        bool hasApiDef,
        string apiDefDisplayName,
        string outputAddress,
        string inputAddress,
        string valueSpecText,
        string inputValueSpecText,
        int outputSpecTypeIndex,
        int inputSpecTypeIndex)
    {
        ApiCallId = apiCallId;
        ApiDefDisplayName = apiDefDisplayName;
        OutputSpecTypeIndex = outputSpecTypeIndex;
        InputSpecTypeIndex  = inputSpecTypeIndex;

        _apiDefId = hasApiDef && apiDefId != Guid.Empty ? apiDefId : null;
        _name = name;
        _outputAddress = outputAddress;
        _inputAddress = inputAddress;
        _valueSpecText = valueSpecText;
        _inputValueSpecText = inputValueSpecText;

        _originalApiDefId = _apiDefId;
        _originalName = _name;
        _originalOutputAddress = _outputAddress;
        _originalInputAddress = _inputAddress;
        _originalValueSpecText = _valueSpecText;
        _originalInputValueSpecText = _inputValueSpecText;
    }

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
    public string OutputAddress   { get => _outputAddress;   set => SetStr(ref _outputAddress, value); }
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
            !String.Equals(_originalOutputAddress, _outputAddress, StringComparison.Ordinal) ||
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
    public CallConditionItem(CallConditionPanelItem panel)
    {
        ConditionId   = panel.ConditionId;
        ConditionType = panel.ConditionType;
        IsOR          = panel.IsOR;
        IsRising      = panel.IsRising;
        Items = panel.Items
            .Select(x => new ConditionApiCallRow(panel.ConditionId, x))
            .ToList();
    }

    public Guid               ConditionId   { get; }
    public CallConditionType  ConditionType  { get; }
    public bool               IsOR          { get; }
    public bool               IsRising      { get; }
    public IReadOnlyList<ConditionApiCallRow> Items { get; }
}

public sealed class ConditionApiCallRow
{
    public ConditionApiCallRow(Guid conditionId, CallConditionApiCallItem item)
    {
        ConditionId          = conditionId;
        ApiCallId            = item.ApiCallId;
        ApiCallName          = item.ApiCallName;
        ApiDefDisplayName    = item.ApiDefDisplayName;
        OutputSpecText       = item.OutputSpecText;
        OutputSpecTypeIndex  = item.OutputSpecTypeIndex;
    }

    public Guid   ConditionId          { get; }
    public Guid   ApiCallId            { get; }
    public string ApiCallName          { get; }
    public string ApiDefDisplayName    { get; }
    public string OutputSpecText       { get; }
    public int    OutputSpecTypeIndex  { get; }
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
}
