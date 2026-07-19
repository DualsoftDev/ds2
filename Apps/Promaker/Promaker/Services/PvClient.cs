using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Promaker.Services;

/// <summary>
/// <see cref="IPvClient"/> 의 네이티브 구현 — <c>ExternalDlls/PvClient.dll</c>(C++, win-x64, git 제외)을
/// P/Invoke 로 호출한다. 서버 URL·프로토콜은 그 네이티브 DLL + pv.conf(로컬)에 있어 이 파일(public)에는
/// 서버 정보가 없다. 여기 있는 건 "C ABI 함수 시그니처" 와 조회 결과 파싱뿐이다.
///
/// ── PvClient.dll 이 export 해야 하는 C ABI 계약 (CloudWorks private 프로젝트가 구현) ──
///   int pv_login   (login_id, password, token_out, token_cap, msg_out, msg_cap)
///   int pv_register(login_id, password, display_name, company, msg_out, msg_cap)
///   int pv_find    (login_id_or_email, msg_out, msg_cap)
///   int pv_overview(token, json_out, json_cap, msg_out, msg_cap)   // GET /account/overview 원본 JSON
///   공통 반환: 1=성공, 0=실패(msg_out 사유). login→token_out, overview→json_out.
/// </summary>
public sealed class PvClient : IPvClient
{
    private const string Dll = "PvClient";

    [DllImport(Dll, EntryPoint = "pv_login", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PvLogin(string loginId, string password,
        StringBuilder tokenOut, int tokenCap, StringBuilder msgOut, int msgCap);

    [DllImport(Dll, EntryPoint = "pv_register", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PvRegister(string loginId, string password, string displayName, string company,
        StringBuilder msgOut, int msgCap);

    [DllImport(Dll, EntryPoint = "pv_find", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PvFind(string loginIdOrEmail, StringBuilder msgOut, int msgCap);

    [DllImport(Dll, EntryPoint = "pv_overview", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PvOverview(string token, StringBuilder jsonOut, int jsonCap,
        StringBuilder msgOut, int msgCap);

    public PvLoginResult Login(string loginId, string password)
    {
        var token = new StringBuilder(1024);
        var msg = new StringBuilder(512);
        try
        {
            var rc = PvLogin(loginId ?? "", password ?? "", token, token.Capacity, msg, msg.Capacity);
            return rc == 1
                ? PvLoginResult.Success(token.ToString())
                : PvLoginResult.Fail(msg.Length > 0 ? msg.ToString() : "로그인에 실패했습니다.");
        }
        catch (Exception ex) { return PvLoginResult.Fail(Describe(ex)); }
    }

    public PvResult Register(PvRegisterRequest r)
    {
        var msg = new StringBuilder(512);
        try
        {
            var rc = PvRegister(r.LoginId ?? "", r.Password ?? "", r.DisplayName ?? "", r.CompanyName ?? "",
                                msg, msg.Capacity);
            return rc == 1
                ? PvResult.Success(msg.Length > 0 ? msg.ToString() : "회원가입이 완료되었습니다.")
                : PvResult.Fail(msg.Length > 0 ? msg.ToString() : "회원가입에 실패했습니다.");
        }
        catch (Exception ex) { return PvResult.Fail(Describe(ex)); }
    }

    public PvResult FindCredentials(string loginIdOrEmail)
    {
        var msg = new StringBuilder(512);
        try
        {
            var rc = PvFind(loginIdOrEmail ?? "", msg, msg.Capacity);
            return rc == 1
                ? PvResult.Success(msg.Length > 0 ? msg.ToString() : "복구 안내를 발송했습니다.")
                : PvResult.Fail(msg.Length > 0 ? msg.ToString() : "요청을 처리하지 못했습니다.");
        }
        catch (Exception ex) { return PvResult.Fail(Describe(ex)); }
    }

    public PvOverviewResult Overview(string token)
    {
        var json = new StringBuilder(65536); // 사이트/단말 트리 — 넉넉히 64KB
        var msg = new StringBuilder(512);
        try
        {
            var rc = PvOverview(token ?? "", json, json.Capacity, msg, msg.Capacity);
            if (rc != 1)
                return PvOverviewResult.Fail(msg.Length > 0 ? msg.ToString() : "사이트/단말 조회에 실패했습니다.");
            return ParseOverview(json.ToString());
        }
        catch (Exception ex) { return PvOverviewResult.Fail(Describe(ex)); }
    }

    // /account/overview 원본 JSON → 표시용 트리. 파싱 실패해도 예외 대신 Fail 반환.
    private static PvOverviewResult ParseOverview(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sites = new List<PvSite>();
            if (doc.RootElement.TryGetProperty("sites", out var sitesEl) && sitesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sitesEl.EnumerateArray())
                {
                    var siteId = Str(s, "site_id");
                    var name = Str(s, "display_name");
                    if (string.IsNullOrEmpty(name)) name = siteId;

                    var edges = new List<PvEdge>();
                    if (s.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in edgesEl.EnumerateArray())
                        {
                            var edgeId = Str(e, "edge_id");
                            var status = Str(e, "status");
                            string? instStatus = null, ip = null;
                            if (e.TryGetProperty("instance", out var inst) && inst.ValueKind == JsonValueKind.Object)
                            {
                                instStatus = NullableStr(inst, "status");
                                ip = NullableStr(inst, "public_ip");
                            }
                            edges.Add(new PvEdge(edgeId, edgeId, status, instStatus, ip));
                        }
                    }
                    sites.Add(new PvSite(siteId, name, edges));
                }
            }
            return new PvOverviewResult(true, sites, null);
        }
        catch (Exception ex) { return PvOverviewResult.Fail($"응답 파싱 오류: {ex.Message}"); }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string? NullableStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Describe(Exception ex) => ex switch
    {
        DllNotFoundException => "PvClient.dll 을 찾을 수 없습니다 (ExternalDlls). private 빌드 산출물을 배치하세요.",
        EntryPointNotFoundException => "PvClient.dll 진입점을 찾을 수 없습니다 (ABI 불일치).",
        _ => $"PV 통신 오류: {ex.Message}"
    };
}
