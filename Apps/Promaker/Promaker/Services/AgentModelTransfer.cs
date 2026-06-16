using Promaker.Shared;

namespace Promaker.Services;

/// <summary>Agent 보내기/가져오기 대상 종류.</summary>
public enum AgentTransferTargetKind
{
    /// <summary>이 머신의 공유폴더 (%ProgramData%\DualSoft\Shared) — 현재 유일하게 동작하는 경로.</summary>
    Local,

    /// <summary>특정 IP 의 원격 Agent 공유폴더 — Ubuntu 등 원격 Agent 대비 구조만 마련 (전송 계층 미구현).</summary>
    Network,
}

/// <summary>업로드/가져오기 대상 — Kind=Network 면 Ip 가 원격 Agent 주소.</summary>
public readonly record struct AgentTransferTarget(AgentTransferTargetKind Kind, string Ip)
{
    public static AgentTransferTarget Local => new(AgentTransferTargetKind.Local, "");
    public static AgentTransferTarget Network(string ip) => new(AgentTransferTargetKind.Network, ip);
}

/// <summary>
/// Agent 모델 전송 경로 결정. 'Agent에 업로드'/'Agent에서 가져오기' 가 대상 AASX 경로를 여기서 얻는다.
/// Local = 이 머신의 공유폴더 직접 IO (Agent/DSPilot 이 파일 변경 감지로 동기화 — 기존 동작).
/// Network = 특정 IP 의 원격 Agent 공유폴더로 전송. 전송 계층(UNC/HTTP)은 아직 미정이라
/// 구조만 두고 미구현 — 이 메서드 하나만 채우면 업로드/가져오기 양쪽이 그대로 동작한다.
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
                // 네트워크 전송도 일단 로컬 공유폴더에 모델/설정/세션을 만든 뒤(이 경로) zip 으로 원격 Agent
                // (AgentUploadClient → http://ip:5050/upload)에 POST 한다. 그래서 경로는 Local 과 동일.
                if (string.IsNullOrWhiteSpace(target.Ip))
                {
                    path = "";
                    error = "네트워크 대상 IP 주소를 입력하세요.";
                    return false;
                }
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
