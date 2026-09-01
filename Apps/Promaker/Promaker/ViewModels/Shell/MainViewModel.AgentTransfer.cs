using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs.Pv;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// Agent 보내기/가져오기 대상 선택 (◎로컬 ○네트워크[IP] ○클라우드) + 'Agent에서 가져오기'.
/// 클라우드 로그인 세션은 앱 전역 <see cref="PvSession"/>(휘발성 — 앱 재시작 시 재로그인)이 보관하고
/// 설정 다이얼로그의 계정 섹션과 공유한다. 여기선 대상 모드만 관리한다.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    private AgentTransferTargetKind _agentTransferMode =
        ParseMode(AppSettingStore.LoadStringOrDefault(SettingsPaths.AgentTransferMode, "Local"));

    [ObservableProperty]
    private string _agentTransferIp =
        AppSettingStore.LoadStringOrDefault(SettingsPaths.AgentTransferIp, "");

    private static AgentTransferTargetKind ParseMode(string s) =>
        System.Enum.TryParse<AgentTransferTargetKind>(s, out var m) ? m : AgentTransferTargetKind.Local;

    // 라디오 바인딩 (상호배타 3개).
    public bool AgentTransferIsLocal
    {
        get => AgentTransferMode == AgentTransferTargetKind.Local;
        set { if (value) AgentTransferMode = AgentTransferTargetKind.Local; }
    }

    public bool AgentTransferIsNetwork
    {
        get => AgentTransferMode == AgentTransferTargetKind.Network;
        set { if (value) AgentTransferMode = AgentTransferTargetKind.Network; }
    }

    public bool AgentTransferIsCloud
    {
        get => AgentTransferMode == AgentTransferTargetKind.Cloud;
        set { if (value) AgentTransferMode = AgentTransferTargetKind.Cloud; }
    }

    partial void OnAgentTransferModeChanged(AgentTransferTargetKind value)
    {
        OnPropertyChanged(nameof(AgentTransferIsLocal));
        OnPropertyChanged(nameof(AgentTransferIsNetwork));
        OnPropertyChanged(nameof(AgentTransferIsCloud));
        AppSettingStore.SaveString(SettingsPaths.AgentTransferMode, value.ToString());
    }

    partial void OnAgentTransferIpChanged(string value) =>
        AppSettingStore.SaveString(SettingsPaths.AgentTransferIp, value);

    private AgentTransferTarget CurrentAgentTransferTarget => AgentTransferMode switch
    {
        AgentTransferTargetKind.Network => AgentTransferTarget.Network(AgentTransferIp),
        AgentTransferTargetKind.Cloud => AgentTransferTarget.Cloud,
        _ => AgentTransferTarget.Local,
    };

    /// <summary>'Agent에서 가져오기' — 로컬은 공유폴더 AASX, 네트워크는 원격 Agent(5050),
    /// 클라우드는 PV 사이트/단말 선택 후 그 단말 인스턴스의 Agent(5050)에서 다운로드해 연다.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task OpenFromAgent()
    {
        string path;
        if (AgentTransferMode == AgentTransferTargetKind.Cloud)
        {
            // 클라우드: 미로그인이면 로그인창부터 → 사이트/단말 선택 → 인스턴스 IP 로 다운로드.
            // Save.cs 클라우드 업로드와 대칭 — 같은 인스턴스 Agent(5050) 경로를 GET 으로 탄다.
            if (!PvSession.IsLoggedIn)
            {
                var login = PvLoginDialog.Show(PvSession.Client);
                if (login is not { Ok: true })
                    return;
                PvSession.Token = login.Token;
            }
            var target = PvTargetDialog.Show(PvSession.Client, PvSession.Token ?? "", PvTransferIntent.Download);
            if (target is null)
                return;
            var instanceIp = target.Value.Edge.PublicIp;
            if (string.IsNullOrWhiteSpace(instanceIp))
            {
                _dialogService.ShowWarning("선택한 단말에 인스턴스 IP 가 없습니다 (인스턴스가 아직 생성 중일 수 있습니다).");
                return;
            }
            StatusText = $"클라우드 모델 가져오는 중 — {target.Value.Site.DisplayName} / {target.Value.Edge.DisplayName} ({instanceIp})...";
            var (cloudOk, cloudPath, cloudMsg) = await AgentUploadClient.DownloadAsync(instanceIp);
            StatusText = cloudMsg;
            if (!cloudOk)
            {
                _dialogService.ShowWarning(cloudMsg);
                return;
            }
            path = cloudPath;
        }
        else if (CurrentAgentTransferTarget.Kind == AgentTransferTargetKind.Network)
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
                _dialogService.ShowWarning($"Agent 공유 모델이 없습니다:\n{localPath}\n\n'업로드 ▸ Agent에 업로드' 로 먼저 업로드하세요.");
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
