namespace Ds2.OpcUa.Server.Hotpath

open System
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds

/// AAS 변경 이벤트에 대한 서버측 처리 계약 (ADR-002 §5 hot-append).
///
/// 실제 UA nodeset 조작은 UA 스택 통합 후 wire-up. 이 계층은 다음 절차의
/// 논리적 시퀀스만 정의하여 어댑터 팀이 계약을 확립할 수 있게 함.
///
/// 절차 (ADR-002 §5):
///   1. Write lock (in-memory namespace table + nodeset-state.json)
///   2. Uri 중복 확인
///   3. In-memory 갱신 · nsIndex 발급
///   4. Server.NamespaceArray Value 노드 SetValue
///   5. nodeset-state.json 원자적 교체
///   6. ModelChangeStructureEvent 발행
///   7. Server.NamespaceArray.NodeVersion attribute 증가
///   8. Lock 해제
///
/// 실패 시 전체 롤백.

type AasChangeType = Added | Modified | Removed | Deprecated

type AasChange = {
    ChangeType : AasChangeType
    GlobalAssetId : GlobalAssetId
    ResourceKind : string   // "Shell" | ...
}

type IAasChangeHandler =
    abstract Handle : AasChange -> unit

type LoggingAasChangeHandler(alloc: INamespaceAllocator, log: string -> unit) =
    interface IAasChangeHandler with
        member _.Handle change =
            match change.ChangeType with
            | Added ->
                match alloc.EnsureForAsset change.GlobalAssetId with
                | NamespaceAllocationResult.Added idx ->
                    log (sprintf "Namespace 신규 등록: %s → ns=%d" change.GlobalAssetId.Value idx)
                    // Wire-up 지점: UA Server.NamespaceArray 갱신 + ModelChangeStructureEvent
                | NamespaceAllocationResult.Existed idx ->
                    log (sprintf "Namespace 재사용: %s → ns=%d" change.GlobalAssetId.Value idx)
                | NamespaceAllocationResult.Deprecated idx ->
                    log (sprintf "Namespace deprecated 상태 재활성화 검토 필요: ns=%d" idx)
            | Modified ->
                log (sprintf "Asset modified: %s" change.GlobalAssetId.Value)
            | Removed | Deprecated ->
                let uri = alloc.GlobalAssetIdToUri change.GlobalAssetId
                if alloc.MarkDeprecated uri then
                    log (sprintf "Namespace deprecated: %s" uri)
