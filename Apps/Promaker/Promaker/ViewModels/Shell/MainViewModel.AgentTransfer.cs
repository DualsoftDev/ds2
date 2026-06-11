using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// Agent 보내기/가져오기 대상 선택 (◎로컬 ○네트워크[IP]) + 'Agent에서 가져오기' 명령.
/// 대상 상태는 저장(▼)/불러오기(▼) 두 팝업이 공유하고 세션 간 영속화된다.
/// 실제 경로 결정은 <see cref="AgentModelTransfer"/> — 네트워크는 구조만 (미구현 안내).
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    private bool _agentTransferIsNetwork =
        AppSettingStore.LoadStringOrDefault(SettingsPaths.AgentTransferUseNetwork, "false") == "true";

    [ObservableProperty]
    private string _agentTransferIp =
        AppSettingStore.LoadStringOrDefault(SettingsPaths.AgentTransferIp, "");

    /// <summary>로컬 라디오 바인딩용 — IsNetwork 의 반대. 라디오 둘이 같은 상태를 양방향으로 본다.</summary>
    public bool AgentTransferIsLocal
    {
        get => !AgentTransferIsNetwork;
        set => AgentTransferIsNetwork = !value;
    }

    partial void OnAgentTransferIsNetworkChanged(bool value)
    {
        OnPropertyChanged(nameof(AgentTransferIsLocal));
        AppSettingStore.SaveString(SettingsPaths.AgentTransferUseNetwork, value ? "true" : "false");
    }

    partial void OnAgentTransferIpChanged(string value) =>
        AppSettingStore.SaveString(SettingsPaths.AgentTransferIp, value);

    private AgentTransferTarget CurrentAgentTransferTarget =>
        AgentTransferIsNetwork
            ? AgentTransferTarget.Network(AgentTransferIp)
            : AgentTransferTarget.Local;

    /// <summary>'Agent에서 가져오기' — 대상(로컬/네트워크) 공유폴더의 AASX 모델을 연다.</summary>
    [RelayCommand]
    private void OpenFromAgent()
    {
        if (!AgentModelTransfer.TryResolveAasxPath(CurrentAgentTransferTarget, out var path, out var error))
        {
            _dialogService.ShowWarning(error);
            return;
        }

        if (!File.Exists(path))
        {
            _dialogService.ShowWarning($"Agent 공유 모델이 없습니다:\n{path}\n\n'저장 ▸ Agent에 업로드' 로 먼저 업로드하세요.");
            return;
        }

        if (!GuardSimulationSemanticEdit("Agent에서 가져오기"))
            return;
        if (!ConfirmDiscardChanges())
            return;

        OpenFilePath(path);
    }
}
