using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;
using Ds2.Core.Store;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    public ObservableCollection<SimWorkItem> TokenSourceWorks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    private SimWorkItem? _selectedTokenSource;

    [RelayCommand(CanExecute = nameof(CanSeedToken))]
    private void SeedToken()
    {
        if (_simEngine is null || SelectedTokenSource is null) return;
        var wg = SelectedTokenSource.Guid;
        if (_simEngine.GetWorkToken(wg) is not null) return; // 이미 토큰 있음

        var token = _simEngine.NextToken();
        _simEngine.SeedToken(wg, token);
        AddSimLog($"토큰 수동 투입: {SelectedTokenSource.Name} ← {FormatTokenDisplay(token)}");
    }

    private bool CanSeedToken() => IsSimulating && !IsSimPaused && !IsHomingPhase && SelectedTokenSource is not null
        && SelectedRuntimeMode is not (RuntimeMode.VirtualPlant or RuntimeMode.Monitoring);

    private void InitTokenSources()
    {
        TokenSourceWorks.Clear();
        if (_simEngine?.Index is not { } index) return;
        foreach (var srcGuid in index.TokenSourceGuids)
        {
            var name = index.WorkName.TryFind(srcGuid);
            if (name is not null)
                TokenSourceWorks.Add(new SimWorkItem(srcGuid, name.Value));
        }
        if (TokenSourceWorks.Count > 0)
            SelectedTokenSource = TokenSourceWorks[0];
    }

    private void WireTokenEvent(ISimulationEngine engine, long generation)
    {
        engine.TokenEvent += (_, args) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnTokenEvent(args);
            });
    }

    private void OnTokenEvent(TokenEventArgs args)
    {
        var label = FormatTokenDisplay(args.Token);
        var target = args.TargetWorkName is not null
            ? $" → {args.TargetWorkName.Value}"
            : "";
        AddSimLog($"[Token] {args.Kind}: {label} @ {args.WorkName}{target}");

        // BlockedOnHoming: 로그 경고만 (MessageBox는 디스패처를 차단하여 후속 토큰 업데이트 지연 유발)
        if (args.Kind.IsBlockedOnHoming)
        {
            SimLog.Warn($"토큰 BlockedOnHoming: {args.WorkName} — {label}");
        }

        // Conflict: 토큰 보유 Finish 노드 → Homing 진입하지만 토큰 때문에 Ready 불가
        if (args.Kind.IsConflict)
        {
            ShowPausedMessageBox(
                $"[Token Conflict]\n" +
                $"{args.WorkName}이(가) 토큰({label})을 보유한 채 Finish 상태이며,\n" +
                $"리셋 조건이 충족되어 Homing에 진입하지만\n" +
                $"토큰이 이동할 수 없어 Ready로 전환되지 않습니다.\n\n" +
                $"토큰을 수동으로 제거하거나 후속 Work를 리셋하여\n" +
                $"토큰 이동 경로를 확보해 주세요.",
                "Token Conflict",
                suppressKey: "TokenConflict");
        }

        // SimNodes에 토큰 정보 갱신
        UpdateSimNodeTokenGroup(args.WorkGuid);
        if (args.TargetWorkGuid is not null)
            UpdateSimNodeTokenGroup(args.TargetWorkGuid.Value);

        // 토큰 이동만으로도 다음 Work가 startable 상태가 될 수 있으므로
        // 토큰 이벤트도 step-mode UI 재평가 경로를 타야 한다.
        RefreshSimulationProgressUi();
    }

    private void UpdateSimNodeTokenGroup(Guid workGuid)
    {
        var groupGuids = Queries.referenceGroupOf(workGuid, Store).ToList();
        if (groupGuids.Count == 0)
            groupGuids.Add(workGuid);

        foreach (var groupGuid in groupGuids)
            UpdateSimNodeToken(groupGuid);
    }

    private void UpdateSimNodeToken(Guid workGuid)
    {
        if (_simEngine is null) return;
        var tokenOpt = _simEngine.GetWorkToken(workGuid);
        var display = tokenOpt is not null ? FormatTokenDisplay(tokenOpt.Value) : "";

        var row = SimNodes.FirstOrDefault(r => r.NodeGuid == workGuid);
        if (row is not null)
            row.TokenDisplay = display;

        foreach (var canvasNode in _allCanvasNodes())
        {
            if (canvasNode.Id == workGuid)
                canvasNode.SimTokenDisplay = display;
        }
    }

    /// <summary>토큰 표시: {이름}#{이름별순번} 형식</summary>
    private string FormatTokenDisplay(TokenValue token)
    {
        var origin = _simEngine?.GetTokenOrigin(token);
        if (origin is not null)
        {
            var (name, seq) = origin.Value;
            return $"{name}#{seq}";
        }
        return $"#{token.Item}";
    }
}
