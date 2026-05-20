namespace Ds2.Backend.Common

[<RequireQualifiedAccess>]
module HubMethod =
    [<Literal>]
    let WriteTag = "WriteTag"
    [<Literal>]
    let OnTagChanged = "OnTagChanged"
    /// Batch 변형 — 여러 tag 변경을 1개 SignalR 프레임으로 송수신.
    [<Literal>]
    let WriteTags = "WriteTags"
    [<Literal>]
    let OnTagsChanged = "OnTagsChanged"
    [<Literal>]
    let SubscribeTag = "SubscribeTag"
    [<Literal>]
    let UnsubscribeTag = "UnsubscribeTag"
    /// Control 시작 시 현재 Tag 값 조회 (Hub 캐시에서 반환)
    [<Literal>]
    let QueryTag = "QueryTag"

[<RequireQualifiedAccess>]
module HubSource =
    [<Literal>]
    let Control = "control"
    [<Literal>]
    let VirtualPlant = "virtualplant"
    [<Literal>]
    let Monitoring = "monitoring"
    [<Literal>]
    let Plc = "plc"
    [<Literal>]
    let Web = "web"

    /// <summary>spec §SignalR — 알려진 source 전체 집합. literal 5개. 외부 source 는 *unknown* 으로 분류 (분류 외 차단 또는 별도 처리).
    /// 새 source 추가 시 본 set 갱신 → DSPilot 의 _acceptedSources default 검토 → 통합 테스트.</summary>
    let WellKnownSources : System.Collections.Generic.IReadOnlySet<string> =
        let s = System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        s.Add(Control)        |> ignore
        s.Add(VirtualPlant)   |> ignore
        s.Add(Monitoring)     |> ignore
        s.Add(Plc)            |> ignore
        s.Add(Web)            |> ignore
        s :> System.Collections.Generic.IReadOnlySet<string>

    /// <summary>임의 source 가 WellKnown 인지. unknown 은 통계/로그용으로 분리 처리하는 호출자 측 helper.</summary>
    let isWellKnown (source: string) : bool =
        not (isNull source) && WellKnownSources.Contains(source)

    /// <summary>DSPilot consumer 기본 accept policy — Promaker host 가 broadcast 하는
    /// 실 IO source (Control / VirtualPlant / Plc). Monitoring 은 자기 자신 echo 이므로 차단.
    /// Web 은 외부 UI 직접 주입이라 검수 대상 — default 에선 차단.</summary>
    let DefaultAcceptedSources : string array =
        [| Control; VirtualPlant; Plc |]

/// Batch payload for WriteTags / OnTagsChanged.
/// [<CLIMutable>] 필수 — SignalR JsonHubProtocol(System.Text.Json, camelCase)이
/// F# record를 ctor 기반으로 deserialize 할 때 ctor parameter 이름 매칭이
/// 환경에 따라 깨져 모든 field가 null 인 record 가 만들어지는 사례 차단.
[<CLIMutable>]
type TagWrite = {
    Address: string
    Value: string
    Source: string
}
