using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Windows;

public partial class NewCustomModelDialog : Window
{
    public class ApiDefRow : INotifyPropertyChanged
    {
        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));
    }

    private readonly ObservableCollection<ApiDefRow> _rows = new();
    private readonly HashSet<string> _existingNames;

    public string ResultSystemType { get; private set; } = "";
    public List<string> ResultApiDefs { get; private set; } = new();

    public NewCustomModelDialog(Window owner, IEnumerable<string> existingNames)
    {
        Owner = owner;
        _existingNames = new HashSet<string>(existingNames, System.StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        ApiDefList.ItemsSource = _rows;
        _rows.Add(new ApiDefRow());
        _rows.Add(new ApiDefRow());
    }

    private void AddApiDef_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new ApiDefRow());
        Validate();
    }

    private void RemoveApiDef_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ApiDefRow row)
        {
            _rows.Remove(row);
            Validate();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => Validate();

    private void ApiDefRow_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var name = NameInput.Text?.Trim() ?? "";
        var hasName = !string.IsNullOrWhiteSpace(name);
        var nameDup = hasName && _existingNames.Contains(name);
        OkButton.IsEnabled = hasName && !nameDup;
        OkButton.ToolTip = nameDup ? $"\"{name}\"은(는) 이미 존재하는 이름입니다." : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return;

        var apiDefs = _rows
            .Select(r => r.Name?.Trim() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        ResultSystemType = name;
        ResultApiDefs = apiDefs;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
