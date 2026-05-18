using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.Win32;
using Promaker.Presentation;

namespace Promaker.Dialogs;

public partial class TokenSpecDialog : Window
{
    private readonly ObservableCollection<TokenSpecRow> _rows;
    private readonly IReadOnlyList<WorkOption> _allWorks;

    public TokenSpecDialog(IReadOnlyList<TokenSpec> specs, IReadOnlyList<WorkOption> allWorks)
    {
        InitializeComponent();

        _allWorks = allWorks;
        DataContext = this;

        _rows = CreateRows(specs, allWorks);
        _rows.CollectionChanged += (_, _) => UpdateCount();
        SpecGrid.ItemsSource = _rows;
        UpdateCount();
    }

    public IReadOnlyList<TokenSpec> Result =>
        _rows.Select(CreateTokenSpec).ToList();

    /// <summary>적용 시점에 Source Role 자동 등록이 필요한 Work Guid 목록.</summary>
    public IReadOnlyList<Guid> WorksRequiringSourceRole =>
        _rows
            .Where(r => r.WorkId is { } opt && Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(opt))
            .Select(r => r.WorkId!.Value)
            .Distinct()
            .Where(id => _allWorks.FirstOrDefault(w => w.Id == id) is { IsSource: false })
            .ToList();

    private void UpdateCount() => CountRun.Text = _rows.Count.ToString();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new WorkPickerDialog(_allWorks) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedWork is not { } picked)
            return;

        var nextId = _rows.Count > 0 ? _rows.Max(r => r.Id) + 1 : 1;
        var row = new TokenSpecRow(
            nextId,
            "",
            "",
            Microsoft.FSharp.Core.FSharpOption<Guid>.Some(picked.Id))
        {
            WorkName = picked.Name
        };
        _rows.Add(row);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SpecGrid.SelectedItems.Cast<TokenSpecRow>().ToList();
        foreach (var row in selected)
            _rows.Remove(row);
    }

    private void SpecGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete
            && SpecGrid.SelectedItems.Count > 0
            && e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            Remove_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "CSV 파일 불러오기",
            Filter = "CSV Files|*.csv|All Files|*.*",
            DefaultExt = FileExtensions.Csv
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var lines = CsvFileHelper.ReadAllLinesShared(dlg.FileName);
            if (lines.Length < 2)
            {
                CsvFileHelper.ShowImportError("CSV 파일이 비어있거나 헤더만 있습니다.");
                return;
            }
            var startId = _rows.Count > 0 ? _rows.Max(r => r.Id) + 1 : 1;

            foreach (var row in CreateImportedRows(lines, startId))
                _rows.Add(row);
        }
        catch (Exception ex)
        {
            CsvFileHelper.ShowImportError(ex.Message);
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        // 빈 Label 검증
        var emptyLabel = _rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Label));
        if (emptyLabel is not null)
        {
            DialogHelpers.Warn(this, $"ID {emptyLabel.Id}의 Label이 비어있습니다.", "검증 오류");
            return;
        }

        // ID 중복 검증
        var duplicateId = _rows.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
        {
            DialogHelpers.Warn(this, $"ID {duplicateId.Key}이(가) {duplicateId.Count()}건 중복됩니다.", "검증 오류");
            return;
        }

        DialogResult = true;
    }

    private static ObservableCollection<TokenSpecRow> CreateRows(
        IReadOnlyList<TokenSpec> specs,
        IReadOnlyList<WorkOption> allWorks)
    {
        return new ObservableCollection<TokenSpecRow>(
            specs.Select(spec =>
            {
                var row = new TokenSpecRow(spec.Id, spec.Label, FormatFields(spec.Fields), spec.WorkId);
                if (spec.WorkId is { } workId)
                    row.WorkName = allWorks.FirstOrDefault(work => work.Id == workId.Value)?.Name ?? "";
                return row;
            }));
    }

    private static TokenSpec CreateTokenSpec(TokenSpecRow row) =>
        new(row.Id, row.Label.Trim(), ParseFields(row.FieldsText), row.WorkId);

    private static IEnumerable<TokenSpecRow> CreateImportedRows(IEnumerable<string> lines, int startId)
    {
        var id = startId;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',', 2);
            var label = parts[0].Trim().Trim('"');
            var fields = parts.Length > 1 ? parts[1].Trim().Trim('"') : "";
            yield return new TokenSpecRow(id++, label, fields);
        }
    }

    private static string FormatFields(Microsoft.FSharp.Collections.FSharpMap<string, string> fields) =>
        Format.formatTokenSpecFields(fields);

    private static Microsoft.FSharp.Collections.FSharpMap<string, string> ParseFields(string text) =>
        Format.parseTokenSpecFields(text ?? "");
}
