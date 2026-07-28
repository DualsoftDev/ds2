namespace Ds2.OpcUa.Server.NodeIds

open Ds2.Core

/// ADR-002 · 결정론적 NodeId 계약.
///
/// 형식: `NodeId = ns={nsIndex};s={signalId}` (String NodeId).
/// - Variable: `s={signalId}`
/// - Method:   `s={methodPath}` (예: "Events/RaiseAssetEvent")
/// - Event object: `s=Events`
/// - Asset folder: `s=Asset`
///
/// 이 형식은 어댑터가 서버 상태 조회 없이 로컬 계산 가능.
type NodeIdKind =
    | AssetFolder
    | Variable of signalId: SignalId
    | EventsFolder
    | RaiseAssetEventMethod

/// Deterministic NodeId 값 표현 (UA 스택 NodeId 로 변환은 wire-up 층 소관).
type DeterministicNodeId = {
    NsIndex : int
    Identifier : string
}
    with
    member this.Format() =
        sprintf "ns=%d;s=%s" this.NsIndex this.Identifier

module DeterministicNodeId =

    let build (nsIndex: int) (kind: NodeIdKind) : DeterministicNodeId =
        let id =
            match kind with
            | AssetFolder            -> "Asset"
            | Variable sig'          -> sig'.Value
            | EventsFolder           -> "Events"
            | RaiseAssetEventMethod  -> "Events/RaiseAssetEvent"
        { NsIndex = nsIndex; Identifier = id }

    let parse (raw: string) : DeterministicNodeId option =
        // ns=N;s=str
        if not (raw.StartsWith "ns=") then None
        else
            let semi = raw.IndexOf ';'
            if semi < 0 then None
            else
                let nsPart = raw.Substring(3, semi - 3)
                let idPart = raw.Substring(semi + 1)
                if not (idPart.StartsWith "s=") then None
                else
                    match System.Int32.TryParse nsPart with
                    | true, n -> Some { NsIndex = n; Identifier = idPart.Substring 2 }
                    | _       -> None
