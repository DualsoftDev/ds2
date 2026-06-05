using CommunityToolkit.Mvvm.ComponentModel;
using Llm.Shared.Instructions;

namespace Promaker.Dialogs;

internal sealed partial class InstructionSettingItem : ObservableObject
{
    public InstructionSettingItem(InstructionCatalogEntry entry, bool isSelected, bool canToggle)
    {
        Key = entry.Key.Value;
        Id = entry.Id;
        DisplayName = entry.DisplayName;
        Description = entry.Description ?? "";
        Content = entry.Content;
        SourceKind = entry.SourceKind;
        DefaultEnabled = entry.DefaultEnabled;
        Order = entry.Order;
        _isSelected = isSelected;
        _canToggle = canToggle;
    }

    public string Key { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Content { get; }
    public InstructionSourceKind SourceKind { get; }
    public bool DefaultEnabled { get; }
    public int Order { get; }

    public bool IsBuiltIn => SourceKind == InstructionSourceKind.BuiltIn;
    public bool IsCustom => SourceKind == InstructionSourceKind.Custom;

    public string SourceLabel => SourceKind switch
    {
        InstructionSourceKind.BuiltIn => "built-in",
        InstructionSourceKind.Custom => "custom",
        InstructionSourceKind.Operator => "operator",
        _ => SourceKind.ToString(),
    };

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(DisplayName) || DisplayName == Id
            ? $"{DisplayName} ({Key})"
            : $"{DisplayName} ({Key})";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _canToggle;
}
