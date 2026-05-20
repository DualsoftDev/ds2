namespace Ds2.Backend.Common

open System

/// <summary>SignalR Hub / PLC 게이트웨이 연결 실패 사유 분류.
/// "F# decides, C# applies" — UI 측은 `diagnosticLabel` 만 사용해 한국어 표시.</summary>
module HubConnectionDiagnostic =

    type Diagnostic =
        | NetworkUnreachable
        | DnsResolutionFailed of host: string
        | ConnectionRefused
        | Timeout of ms: int
        | AuthenticationFailed
        | ProtocolMismatch of detail: string
        | PlcNoResponse
        | InternalError of message: string
        | Reconnecting of attempt: int * nextRetryMs: int

    /// 예외 → 진단 사유 분류. 호출자가 catch 한 Exception 을 본 함수에 넘기면
    /// 분류된 Diagnostic 반환. UI 가 받은 분류를 그대로 사용자 표시.
    [<CompiledName("ClassifyException")>]
    let classifyException (ex: Exception) : Diagnostic =
        let msg = if isNull ex then "" else ex.Message
        let typeName = if isNull ex then "" else ex.GetType().Name
        let lower = (if isNull msg then "" else msg.ToLowerInvariant())
        match typeName, lower with
        | "TimeoutException", _ -> Timeout 0
        | _, s when s.Contains "no connection could be made" -> ConnectionRefused
        | _, s when s.Contains "connection refused" -> ConnectionRefused
        | _, s when s.Contains "name or service not known"
                 || s.Contains "no such host" -> DnsResolutionFailed ""
        | _, s when s.Contains "network is unreachable"
                 || s.Contains "host is unreachable" -> NetworkUnreachable
        | _, s when s.Contains "401"
                 || s.Contains "403"
                 || s.Contains "unauthorized" -> AuthenticationFailed
        | _, s when s.Contains "timed out"
                 || s.Contains "timeout" -> Timeout 0
        | _, s when s.Contains "negotiate"
                 || s.Contains "protocol" -> ProtocolMismatch msg
        | _ -> InternalError msg

    /// 진단 → 사용자 표시 한국어 라벨 (StatusText / SimLog 표시용).
    [<CompiledName("DiagnosticLabel")>]
    let diagnosticLabel (d: Diagnostic) : string =
        match d with
        | NetworkUnreachable           -> "네트워크 unreachable — 케이블 / IP / 라우팅 확인"
        | DnsResolutionFailed host     ->
            if String.IsNullOrEmpty host then "DNS 해석 실패 — 호스트 주소 확인"
            else sprintf "DNS 해석 실패 — '%s' 호스트 확인" host
        | ConnectionRefused            -> "연결 거부 — 대상 서비스 미실행 또는 포트 차단"
        | Timeout ms when ms > 0       -> sprintf "응답 timeout (%d ms) — 네트워크 / 부하 확인" ms
        | Timeout _                    -> "응답 timeout — 네트워크 / 부하 확인"
        | AuthenticationFailed         -> "인증 실패 — 자격 증명 / 토큰 확인"
        | ProtocolMismatch detail      -> sprintf "프로토콜 불일치 — %s" detail
        | PlcNoResponse                -> "PLC 응답 없음 — 전원 / 통신 모듈 확인"
        | InternalError msg            ->
            if String.IsNullOrEmpty msg then "내부 오류"
            else sprintf "내부 오류 — %s" msg
        | Reconnecting (attempt, eta)  ->
            sprintf "재연결 시도 #%d — %.1f초 후 재시도" attempt (float eta / 1000.0)
