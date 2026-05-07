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

/// Batch payload for WriteTags / OnTagsChanged.
/// 별도 record 로 정의 — System.Text.Json 으로 양방향 직렬화 가능.
type TagWrite = {
    Address: string
    Value: string
    Source: string
}
