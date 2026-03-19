using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Ds2.Core;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public partial class TokenSpecDialog : Window
{
    private readonly ObservableCollection<TokenSpecRow> _rows;

    public TokenSpecDialog(IReadOnlyList<TokenSpec> specs)
    {
        InitializeComponent();

        _rows = new ObservableCollection<TokenSpecRow>(
            specs.Select(s => new TokenSpecRow(s.Id, s.Label, FormatFields(s.Fields))));

        _rows.CollectionChanged += (_, _) => UpdateCount();
        SpecGrid.ItemsSource = _rows;
        UpdateCount();
    }

    public IReadOnlyList<TokenSpec> Result =>
        _rows.Select(r => new TokenSpec(r.Id, r.Label.Trim(), ParseFields(r.FieldsText))).ToList();

    private void UpdateCount() => CountRun.Text = _rows.Count.ToString();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var nextId = _rows.Count > 0 ? _rows.Max(r => r.Id) + 1 : 1;
        _rows.Add(new TokenSpecRow(nextId, "", ""));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SpecGrid.SelectedItems.Cast<TokenSpecRow>().ToList();
        foreach (var row in selected)
            _rows.Remove(row);
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "CSV 파일 불러오기",
            Filter = "CSV Files|*.csv|All Files|*.*",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName);
            var startId = _rows.Count > 0 ? _rows.Max(r => r.Id) + 1 : 1;
            var id = startId;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',', 2);
                var label = parts[0].Trim().Trim('"');
                var fields = parts.Length > 1 ? parts[1].Trim().Trim('"') : "";
                _rows.Add(new TokenSpecRow(id++, label, fields));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"CSV 불러오기 실패: {ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        // 빈 Label 검증
        var emptyLabel = _rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Label));
        if (emptyLabel is not null)
        {
            MessageBox.Show(this, $"ID {emptyLabel.Id}의 Label이 비어있습니다.", "검증 오류",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ID 중복 검증
        var duplicateId = _rows.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
        {
            MessageBox.Show(this, $"ID {duplicateId.Key}이(가) {duplicateId.Count()}건 중복됩니다.", "검증 오류",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static string FormatFields(Microsoft.FSharp.Collections.FSharpMap<string, string> fields) =>
        string.Join(", ", fields.Select(kv => $"{kv.Key}={kv.Value}"));

    private static Microsoft.FSharp.Collections.FSharpMap<string, string> ParseFields(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Microsoft.FSharp.Collections.MapModule.Empty<string, string>();

        var pairs = text.Split(',')
            .Select(p => p.Trim().Split('=', 2))
            .Where(p => p.Length == 2 && !string.IsNullOrWhiteSpace(p[0]))
            .Select(p => Tuple.Create(p[0].Trim(), p[1].Trim()));

        return new Microsoft.FSharp.Collections.FSharpMap<string, string>(pairs);
    }
}

public sealed class TokenSpecRow : INotifyPropertyChanged
{
    private int _id;
    private string _label;
    private string _fieldsText;

    public TokenSpecRow(int id, string label, string fieldsText)
    {
        _id = id;
        _label = label;
        _fieldsText = fieldsText;
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
