using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class RobotMetadataDialog : Window
{
    private readonly DsStore _store;
    private readonly Dictionary<string, RobotMetadataDraft> _drafts = new();
    private string? _currentAlias;

    public RobotMetadataDialog(DsStore store)
    {
        InitializeComponent();
        _store = store;
        LoadDevices();
    }

    private void LoadDevices()
    {
        var aliases = _store.Calls.Values
            .Select(c => c.DevicesAlias)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .OrderBy(a => a)
            .ToList();
        DeviceCombo.ItemsSource = aliases;

        var cpOpt = Queries.getOrCreatePrimaryControlProps(_store);
        var cp = Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(cpOpt) ? cpOpt.Value : null;
        if (cp != null)
        {
            foreach (var alias in aliases)
            {
                var meta = cp.RobotMetadata.TryGetValue(alias, out var m) ? m : null;
                _drafts[alias] = RobotMetadataDraft.From(meta);
            }
        }

        if (aliases.Count > 0)
            DeviceCombo.SelectedIndex = 0;
    }

    private void DeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        FlushCurrent();

        _currentAlias = DeviceCombo.SelectedItem as string;
        if (_currentAlias == null) return;

        var draft = _drafts[_currentAlias];
        ProgNoGrid.ItemsSource = draft.ProgNoBranches;
        AggGrid.ItemsSource    = draft.Aggregations;
        MutualGrid.ItemsSource = draft.Mutuals;
        AuxGrid.ItemsSource    = draft.Aux;
    }

    private void FlushCurrent()
    {
        // ObservableCollection 은 binding 으로 자동 반영되므로 별도 작업 불필요.
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FlushCurrent();
        var cpOpt = Queries.getOrCreatePrimaryControlProps(_store);
        var cp = Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(cpOpt) ? cpOpt.Value : null;
        if (cp == null) { DialogResult = false; Close(); return; }

        foreach (var (alias, draft) in _drafts)
        {
            var meta = draft.ToCore();
            if (meta == null)
                cp.RobotMetadata.Remove(alias);
            else
                cp.RobotMetadata[alias] = meta;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

internal sealed class RobotMetadataDraft
{
    public ObservableCollection<ProgNoRow>  ProgNoBranches { get; } = new();
    public ObservableCollection<AggRow>     Aggregations   { get; } = new();
    public ObservableCollection<MutualRow>  Mutuals        { get; } = new();
    public ObservableCollection<AuxRow>     Aux            { get; } = new();

    public static RobotMetadataDraft From(RobotMetadata? meta)
    {
        var d = new RobotMetadataDraft();
        if (meta == null) return d;

        foreach (var b in meta.ProgNoBranches)
            d.ProgNoBranches.Add(new ProgNoRow {
                Conditions = string.Join(";", b.ConditionTags),
                ProgNo     = b.ProgNo,
            });
        foreach (var g in meta.AggregationGroups)
            d.Aggregations.Add(new AggRow {
                Kind       = g.Kind,
                Aggregated = g.Aggregated,
                Sources    = string.Join(";", g.Sources),
            });
        foreach (var m in meta.MutualInterlocks)
            d.Mutuals.Add(new MutualRow {
                SourceSignal = m.SourceSignal,
                TargetPort   = m.TargetPort,
            });
        foreach (var kv in meta.AuxCoils)
            d.Aux.Add(new AuxRow {
                CoilName = kv.Key,
                Sources  = string.Join(";", kv.Value),
            });
        return d;
    }

    public RobotMetadata? ToCore()
    {
        bool empty =
            ProgNoBranches.All(r => string.IsNullOrWhiteSpace(r.Conditions) && r.ProgNo == 0) &&
            Aggregations.All(r => string.IsNullOrWhiteSpace(r.Kind) && string.IsNullOrWhiteSpace(r.Aggregated)) &&
            Mutuals.All(r => string.IsNullOrWhiteSpace(r.SourceSignal) && string.IsNullOrWhiteSpace(r.TargetPort)) &&
            Aux.All(r => string.IsNullOrWhiteSpace(r.CoilName));
        if (empty) return null;

        var meta = new RobotMetadata();
        foreach (var r in ProgNoBranches)
        {
            if (string.IsNullOrWhiteSpace(r.Conditions)) continue;
            var b = new RobotProgNoBranch { ProgNo = r.ProgNo };
            foreach (var t in SplitTags(r.Conditions))
                b.ConditionTags.Add(t);
            meta.ProgNoBranches.Add(b);
        }
        foreach (var r in Aggregations)
        {
            if (string.IsNullOrWhiteSpace(r.Aggregated)) continue;
            var g = new RobotAggregationGroup { Kind = r.Kind ?? "", Aggregated = r.Aggregated };
            foreach (var t in SplitTags(r.Sources ?? ""))
                g.Sources.Add(t);
            meta.AggregationGroups.Add(g);
        }
        foreach (var r in Mutuals)
        {
            if (string.IsNullOrWhiteSpace(r.SourceSignal) || string.IsNullOrWhiteSpace(r.TargetPort)) continue;
            meta.MutualInterlocks.Add(new RobotMutualInterlock {
                SourceSignal = r.SourceSignal!,
                TargetPort   = r.TargetPort!,
            });
        }
        foreach (var r in Aux)
        {
            if (string.IsNullOrWhiteSpace(r.CoilName)) continue;
            var ra = new List<string>();
            foreach (var t in SplitTags(r.Sources ?? "")) ra.Add(t);
            meta.AuxCoils[r.CoilName!] = ra;
        }
        return meta;
    }

    private static IEnumerable<string> SplitTags(string s) =>
        s.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
         .Where(t => t.Length > 0);
}

internal sealed class ProgNoRow
{
    public string? Conditions { get; set; } = "";
    public int     ProgNo     { get; set; } = 0;
}
internal sealed class AggRow
{
    public string? Kind       { get; set; } = "";
    public string? Aggregated { get; set; } = "";
    public string? Sources    { get; set; } = "";
}
internal sealed class MutualRow
{
    public string? SourceSignal { get; set; } = "";
    public string? TargetPort   { get; set; } = "";
}
internal sealed class AuxRow
{
    public string? CoilName { get; set; } = "";
    public string? Sources  { get; set; } = "";
}

