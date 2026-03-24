using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Promaker.Dialogs;

public sealed class TokenSpecRow : INotifyPropertyChanged
{
    private int _id;
    private string _label;
    private string _fieldsText;
    private Microsoft.FSharp.Core.FSharpOption<Guid>? _workId;
    private string _workName = "";

    public TokenSpecRow(int id, string label, string fieldsText, Microsoft.FSharp.Core.FSharpOption<Guid>? workId = null)
    {
        _id = id;
        _label = label;
        _fieldsText = fieldsText;
        _workId = workId;
    }

    public int Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); }
    }

    public string FieldsText
    {
        get => _fieldsText;
        set { _fieldsText = value; OnPropertyChanged(); }
    }

    public Microsoft.FSharp.Core.FSharpOption<Guid>? WorkId
    {
        get => _workId;
        set { _workId = value; OnPropertyChanged(); OnPropertyChanged(nameof(WorkName)); }
    }

    public string WorkName
    {
        get => _workName;
        set { _workName = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record WorkOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}
