using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Model;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    public ObservableCollection<SimWorkItem> TokenSourceWorks { get; } = [];
    public ObservableCollection<TokenSpecItem> TokenSpecItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    private SimWorkItem? _selectedTokenSource;

    [ObservableProperty] private TokenSpecItem? _selectedTokenSpec;
    [ObservableProperty] private bool _hasTokenSpecs;

    [RelayCommand(CanExecute = nameof(CanSeedToken))]
    private void SeedToken()
    {
        if (_simEngine is not { } engine || SelectedTokenSource is not { } source) return;

        TokenValue token;
        if (SelectedTokenSpec is { } spec)
            token = TokenValue.NewIntToken(spec.Id);
        else
            token = ((EventDrivenEngine)engine).NextToken();

        engine.SeedToken(source.Guid, token);
        var label = FormatTokenDisplay(token);
        AddSimLog($"Token {label} → {source.Name}");
    }

    private bool CanSeedToken() => IsSimulating && SelectedTokenSource is not null;

    [RelayCommand(CanExecute = nameof(CanDiscardToken))]
    private void DiscardToken()
    {
        if (_simEngine is not { } engine || SelectedSimWork is not { } work) return;

        engine.DiscardToken(work.Guid);
    }

    private bool CanDiscardToken() => IsSimulating && IsSimPaused && SelectedSimWork is not null;

    private void InitTokenSources()
    {
        TokenSourceWorks.Clear();
        TokenSpecItems.Clear();
        SelectedTokenSpec = null;

        if (_simEngine is null) return;

        foreach (var srcGuid in _simEngine.Index.TokenSourceGuids)
        {
            var name = _simEngine.Index.WorkName.TryFind(srcGuid);
            if (name is not null)
                TokenSourceWorks.Add(new SimWorkItem(srcGuid, name.Value));
        }

        if (TokenSourceWorks.Count > 0)
            SelectedTokenSource = TokenSourceWorks[0];

        // TokenSpec 로드
        var specs = Store.GetTokenSpecs();
        foreach (var spec in specs)
            TokenSpecItems.Add(new TokenSpecItem(spec.Id, spec.Label));

        HasTokenSpecs = TokenSpecItems.Count > 0;
        if (HasTokenSpecs)
            SelectedTokenSpec = TokenSpecItems[0];
    }

    private void WireTokenEvent()
    {
        if (_simEngine is null) return;

        _simEngine.TokenEvent += (_, args) =>
            _dispatcher.BeginInvoke(() => OnTokenEvent(args));
    }

    private void OnTokenEvent(TokenEventArgs args)
    {
        var label = FormatTokenDisplay(args.Token);
        var target = args.TargetWorkName is not null
            ? $" → {args.TargetWorkName.Value}"
            : "";
        AddSimLog($"[Token] {args.Kind}: {label} @ {args.WorkName}{target}");

        // SimNodes에 토큰 정보 갱신
        UpdateSimNodeToken(args.WorkGuid);
        if (args.TargetWorkGuid is not null)
            UpdateSimNodeToken(args.TargetWorkGuid.Value);
    }

    private void UpdateSimNodeToken(Guid workGuid)
    {
        if (_simEngine is null) return;
        var tokenOpt = _simEngine.GetWorkToken(workGuid);
        var display = tokenOpt is not null ? FormatTokenDisplay(tokenOpt.Value) : "";

        var row = SimNodes.FirstOrDefault(r => r.NodeGuid == workGuid);
        if (row is not null)
            row.TokenDisplay = display;

        var canvasNode = _canvasNodes.FirstOrDefault(n => n.Id == workGuid);
        if (canvasNode is not null)
            canvasNode.SimTokenDisplay = display;
    }

    private static int GetTokenId(TokenValue token) => token.Item;

    private string FormatTokenDisplay(TokenValue token)
    {
        var id = GetTokenId(token);
        var spec = TokenSpecItems.FirstOrDefault(s => s.Id == id);
        return spec is not null ? $"#{id} {spec.Label}" : $"#{id}";
    }
}

/// <summary>TokenSpec 드롭다운 항목입니다.</summary>
public record TokenSpecItem(int Id, string Label)
{
    public override string ToString() => $"#{Id} {Label}";
}
