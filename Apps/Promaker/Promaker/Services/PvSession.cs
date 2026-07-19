namespace Promaker.Services;

/// <summary>
/// PV(클라우드) 로그인 세션 — 앱 전역 공유. 업로드 흐름(Save)과 설정 다이얼로그의 계정 섹션이
/// 같은 세션을 본다. 토큰만 보관하고 서버 정보는 IPvClient(네이티브 PvClient.dll) 뒤에 있다.
/// </summary>
public static class PvSession
{
    /// <summary>단일 PV 통신 클라이언트 (네이티브 dll 래퍼).</summary>
    public static IPvClient Client { get; } = new PvClient();

    /// <summary>세션 토큰 (로그인 성공 시 설정, 로그아웃 시 null).</summary>
    public static string? Token { get; set; }

    /// <summary>표시용 로그인 아이디/이름.</summary>
    public static string DisplayName { get; set; } = "";

    public static bool IsLoggedIn => !string.IsNullOrEmpty(Token);

    public static void Logout()
    {
        Token = null;
        DisplayName = "";
    }
}
