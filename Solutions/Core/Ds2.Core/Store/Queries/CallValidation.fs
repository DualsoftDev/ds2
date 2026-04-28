module Ds2.Core.CallValidation

open System
open Ds2.Core
open Ds2.Core.Store

// =============================================================================
// Call 구조 검증 — B안 v2 (1 Call = 1 FB 호출 단위, 다중 Device 공유 출력 허용)
// =============================================================================
//
// Invariants:
//   1. Call.ApiCalls.Count >= 1
//   2. Call.ApiCalls 의 모든 ApiCall → SystemType 이 동일 (preset 합성 가능)
//      (Device(passiveSystem) 자체는 달라도 됨 — 같은 SystemType 의 다른 Device
//       들에 동일 출력을 공유 제어하는 패턴이 정상.)
//
// 이 모듈은 읽기 전용 검증만 수행한다. 수정/마이그레이션은 호출 측 책임.

/// Call 내 모든 ApiCall 의 ApiDef.ParentId distinct 집합 계산 (조회 헬퍼).
let private collectParentIds (call: Call) (store: DsStore) : Guid list =
    call.ApiCalls
    |> Seq.choose (fun ac ->
        ac.ApiDefId
        |> Option.bind (fun id -> Queries.getApiDef id store)
        |> Option.map (fun def -> def.ParentId))
    |> Seq.distinct
    |> Seq.toList

/// Call 내 ApiCall 들의 SystemType distinct 집합.
let private collectSystemTypes (call: Call) (store: DsStore) : string list =
    call.ApiCalls
    |> Seq.choose (fun ac ->
        ac.ApiDefId
        |> Option.bind (fun id -> Queries.getApiDef id store)
        |> Option.bind (fun def -> Queries.getSystem def.ParentId store)
        |> Option.bind (fun sys -> sys.SystemType))
    |> Seq.distinct
    |> Seq.toList

/// Call.ApiCalls.Count == 0 검증.
let validateNonEmptyApiCalls (call: Call) : string list =
    if call.ApiCalls.Count = 0 then
        [ sprintf "Call '%s' (%O) 에 ApiCall 이 없습니다." call.Name call.Id ]
    else []

/// Call 내 모든 ApiCall 의 SystemType 이 동일한지 검증 (B안 v2).
let validateHomogeneousSystemType (call: Call) (store: DsStore) : string list =
    let types = collectSystemTypes call store
    if types.Length <= 1 then []
    else
        [ sprintf "Call '%s' (%O): SystemType 혼재 [%s] — preset 합성 불가."
                  call.Name call.Id (String.concat ", " types) ]

/// 하위 호환 — 기존 호출처를 위해 유지 (이종 Device 자체는 정상이므로 항상 [] 반환).
[<System.Obsolete("B안 v2: 이종 Device Call 정상. validateHomogeneousSystemType 사용.")>]
let validateHomogeneousDevice (_call: Call) (_store: DsStore) : string list = []

/// 한 Call 에 대한 전체 검증 (빈 + SystemType 동종성).
let validateCall (call: Call) (store: DsStore) : string list =
    validateNonEmptyApiCalls call @ validateHomogeneousSystemType call store

/// Store 전체에서 위반 Call 수집.
let detectInvalidCalls (store: DsStore) : (Guid * string list) list =
    store.Calls.Values
    |> Seq.choose (fun call ->
        match validateCall call store with
        | [] -> None
        | errors -> Some (call.Id, errors))
    |> Seq.toList

/// Call 의 Device 집합 (ApiDef.ParentId distinct).
/// 정보 제공용 — 마이그레이션 다이얼로그 등에서 사용.
let devicesOf (call: Call) (store: DsStore) : Guid list =
    collectParentIds call store

/// Call 이 동일 SystemType 인지 (B안 v2 의 진짜 동종성 기준).
let isHomogeneous (call: Call) (store: DsStore) : bool =
    collectSystemTypes call store |> List.length <= 1


// =============================================================================
// OriginFlowId 자동 복구 — 과거 버전에서 Panel 경유로 생성된 ApiCall 들은 OriginFlowId 가 누락됨.
// 프로젝트 로드 시 1회 실행해 Call→Work→Flow 체인으로 자동 설정.
// =============================================================================

/// OriginFlowId = None 인 ApiCall 을 찾아 parent Call 의 Work.ParentId(Flow.Id) 로 설정.
/// 반환: 복구된 ApiCall 개수.
let healMissingOriginFlowIds (store: DsStore) : int =
    let mutable healed = 0
    for call in store.Calls.Values do
        let flowIdOpt =
            match Queries.getWork call.ParentId store with
            | Some work -> Some work.ParentId
            | None -> None
        match flowIdOpt with
        | None -> ()
        | Some flowId ->
            for apiCall in call.ApiCalls do
                if apiCall.OriginFlowId.IsNone then
                    apiCall.OriginFlowId <- Some flowId
                    healed <- healed + 1
    healed
