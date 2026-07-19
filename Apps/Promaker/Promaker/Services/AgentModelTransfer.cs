using Promaker.Shared;

namespace Promaker.Services;

/// <summary>Agent 보내기/가져오기 대상 종류.</summary>
public enum AgentTransferTargetKind
{
    /// <summary>이 머신의 공유폴더 (%ProgramData%\DualSoft\Shared).</summary>
    Local,

    /// <summary>같은 로컬망의 특정 IP 원격 Agent 공유폴더 (AgentUploadClient → http://ip:5050).</summary>
    Network,

    /// <summary>클라우드(PV 서버) — 로그인 후 사이트/단말 선택해 전송. 조회·전송 계층은 후속 단계.</summary>
    Cloud,
}

/// <summary>업로드/가져오기 대상 — Kind=Network 면 Ip 가 원격 Agent 주소.</summary>
public readonly record struct AgentTransferTarget(AgentTransferTargetKind Kind, string Ip)
{
    public static AgentTransferTarget Local => new(AgentTransferTargetKind.Local, "");
    public static AgentTransferTarget Network(string ip) => new(AgentTransferTargetKind.Network, ip);
    public static AgentTransferTarget Cloud => new(AgentTransferTargetKind.Cloud, "");
}

/// <summary>
/// Agent 모델 전송 경로 결정. Local/Network 는 로컬 공유폴더에 모델을 준비하는 경로가 같고
/// (Network 는 그 뒤 AgentUploadClient 가 원격 전송), Cloud 도 업로드할 모델을 같은 경로에 준비한다.
/// 실제 클라우드 전송(PV 서버로 올리기)은 3단계(IPvClient.Upload) 에서 붙인다.
/// </summary>
public static class AgentModelTransfer
{
    public static bool TryResolveAasxPath(AgentTransferTarget target, out string path, out string error)
    {
        switch (target.Kind)
        {
            case AgentTransferTargetKind.Local:
                path = SharedPaths.AasxFilePath;
                error = "";
                return true;

            case AgentTransferTargetKind.Network:
                if (string.IsNullOrWhiteSpace(target.Ip))
                {
                    path = "";
                    error = "네트워크 대상 IP 주소를 입력하세요.";
                    return false;
                }
                path = SharedPaths.AasxFilePath;
                error = "";
                return true;

            case AgentTransferTargetKind.Cloud:
                path = SharedPaths.AasxFilePath;
                error = "";
                return true;

            default:
                path = "";
                error = "알 수 없는 Agent 전송 대상입니다.";
                return false;
        }
    }
}
