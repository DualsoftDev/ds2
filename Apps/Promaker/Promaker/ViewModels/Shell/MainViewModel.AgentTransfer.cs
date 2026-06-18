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

    /// <summary>'Agent에서 가져오기' — 로컬은 공유폴더 AASX 를, 네트워크는 원격 Agent(5050)에서 다운로드해 연다.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task OpenFromAgent()
    {
        string path;
        if (CurrentAgentTransferTarget.Kind == AgentTransferTargetKind.Network)
        {
            // 원격 Agent 에서 project.aasx 를 GET 으로 받아 임시 파일로 — 그 경로를 연다.
            StatusText = $"원격 Agent({AgentTransferIp}) 모델 가져오는 중...";
            var (ok, downloaded, msg) = await AgentUploadClient.DownloadAsync(AgentTransferIp);
            StatusText = msg;
            if (!ok)
            {
                _dialogService.ShowWarning(msg);
                return;
            }
            path = downloaded;
        }
        else
        {
            if (!AgentModelTransfer.TryResolveAasxPath(CurrentAgentTransferTarget, out var localPath, out var error))
            {
                _dialogService.ShowWarning(error);
                return;
            }
            if (!File.Exists(localPath))
            {
                _dialogService.ShowWarning($"Agent 공유 모델이 없습니다:\n{localPath}\n\n'저장 ▸ Agent에 업로드' 로 먼저 업로드하세요.");
                return;
            }
            path = localPath;
        }

        if (!GuardSimulationSemanticEdit("Agent에서 가져오기"))
            return;
        if (!ConfirmDiscardChanges())
            return;

        OpenFilePath(path);
    }
}
