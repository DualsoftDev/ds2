namespace Ds2.Mermaid

open System
open System.Collections.Generic

/// IO 태그 한 건 — Ds2.Mermaid 임포트 시 Call 노드에 in-memory 로 바인딩되는 PLC 주소/심볼 정보.
/// 디스크 영속화는 `*.iotag.json` 페어 파일이 담당하며, Ds2.Core/Store 는 무수정.
type IoTagRecord = {
    Address: string
    Symbol: string option
    Comment: string option
    DataType: string option
    Direction: string option   // "Input" | "Output" | "Memory"
}

/// CallId → IoTagRecord 매핑 in-memory registry.
/// `Ds2.Core` 무수정 전략: ImportPlanOperation 추가 없이 sidecar 만으로 IO 표시.
///
/// 사용 순서:
///   1. `MermaidMapperTargets.mapToSystemEx` 가 callPath → CallId 인덱스 누적 후 반환
///   2. `IoTagPairLoader.loadFile` 로 `*.iotag.json` 파싱
///   3. `IoTagBinder.bind` 가 callPath 매칭 후 `IoTagSidecar.set` 호출
///   4. ProMaker Call 패널이 `IoTagSidecar.tryGet` 으로 조회
[<RequireQualifiedAccess>]
module IoTagSidecar =

    let private store = Dictionary<Guid, IoTagRecord>()

    let set (callId: Guid) (record: IoTagRecord) : unit =
        store.[callId] <- record

    let tryGet (callId: Guid) : IoTagRecord option =
        match store.TryGetValue callId with
        | true, v -> Some v
        | _ -> None

    let removeCall (callId: Guid) : bool =
        store.Remove(callId)

    let clear () : unit =
        store.Clear()

    let count () : int =
        store.Count

    let all () : IReadOnlyDictionary<Guid, IoTagRecord> =
        upcast store
