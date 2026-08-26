namespace Ds2.Runtime.IO

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

/// Call의 ApiCall 단위 IO 매핑 정보
type SignalMapping = {
    ApiCallGuid: Guid
    CallGuid: Guid
    TxWorkGuid: Guid option
    RxWorkGuid: Guid option
    OutAddress: string          // OutTag.Address (빈 문자열이면 매핑 없음)
    InAddress: string           // InTag.Address
    /// 이 Call 을 소유한 System (Call→Work→Flow→System). 주소 맵이 주소 단독 키라
    /// 서로 다른 PLC 가 같은 주소를 쓰면 한 신호가 두 System 의 Call 을 모두 발화시킨다(팬아웃).
    /// 관측값에 실려온 SystemId 와 이 값을 대조해 남의 System 신호를 걸러낸다.
    /// None = 귀속 미상(그래프가 끊긴 경우) → 종전대로 필터 없이 통과.
    SystemId: Guid option
}

/// 빌드타임 IO 매핑 결과
type SignalIOMap = {
    Mappings: SignalMapping list
    /// OutTag 주소 → 매핑 목록 (같은 주소에 여러 Call 가능)
    OutAddressToMappings: Map<string, SignalMapping list>
    /// InTag 주소 → 매핑 목록
    InAddressToMappings: Map<string, SignalMapping list>
    /// Call Guid → 관련 매핑들
    CallToMappings: Map<Guid, SignalMapping list>
    /// TxWork Guid → Out 주소 (executeApiCall에서 사용)
    TxWorkToOutAddresses: Map<Guid, string list>
    /// RxWork Guid → In 주소 목록 (상호 리셋 시 사용)
    RxWorkToInAddresses: Map<Guid, string list>
}

/// C#에서 사용하기 쉬운 SignalIOMap 조회 헬퍼
type SignalIOMap with
    /// OutTag 주소로 매핑 목록 조회
    member this.GetByOutAddress(address: string) : SignalMapping list =
        this.OutAddressToMappings |> Map.tryFind address |> Option.defaultValue []

    /// InTag 주소로 매핑 목록 조회
    member this.GetByInAddress(address: string) : SignalMapping list =
        this.InAddressToMappings |> Map.tryFind address |> Option.defaultValue []

    /// OutTag 주소로 첫 번째 매핑 (하위 호환)
    member this.TryGetByOutAddress(address: string) : SignalMapping option =
        this.OutAddressToMappings |> Map.tryFind address |> Option.bind List.tryHead

    /// InTag 주소로 첫 번째 매핑 (하위 호환)
    member this.TryGetByInAddress(address: string) : SignalMapping option =
        this.InAddressToMappings |> Map.tryFind address |> Option.bind List.tryHead

    /// Call의 OutTag 주소 목록 (C#용)
    member this.GetOutAddressesForCall(callGuid: Guid) : string list =
        this.CallToMappings
        |> Map.tryFind callGuid
        |> Option.defaultValue []
        |> List.choose (fun m -> if System.String.IsNullOrEmpty m.OutAddress then None else Some m.OutAddress)

module SignalIOMap =

    /// DsStore에서 IO 매핑 빌드. callFilter 가 Some 이면 그 Call 집합만 —
    /// System 단위 실행(멀티 PLC)에서 엔진 인덱스에 담긴 Call 로 스코프를 맞춘다.
    let buildFiltered (store: DsStore) (callFilter: Set<Guid> option) : SignalIOMap =
        let mappings = ResizeArray<SignalMapping>()

        let calls =
            match callFilter with
            | Some ids -> store.Calls.Values |> Seq.filter (fun c -> ids.Contains c.Id)
            | None -> store.Calls.Values :> seq<_>

        // Call → Work → Flow → System 부모 체인으로 소유 System 을 유도한다.
        // (Ds2.Core 는 수정하지 않는다 — 기존 조회 API 만 사용.)
        // Flow 단위로 결과를 캐시: 한 Flow 의 Call 이 수십~수백 개라 매번 2단 조회는 낭비다.
        let systemIdByFlow = System.Collections.Generic.Dictionary<Guid, Guid option>()
        let systemIdOfCall (call: Call) : Guid option =
            match Queries.getWork call.ParentId store with
            | None -> None
            | Some work ->
                match systemIdByFlow.TryGetValue work.ParentId with
                | true, cached -> cached
                | _ ->
                    let resolved =
                        Queries.getFlow work.ParentId store |> Option.map (fun flow -> flow.ParentId)
                    systemIdByFlow.[work.ParentId] <- resolved
                    resolved

        for call in calls do
            let callSystemId = systemIdOfCall call
            for apiCall in call.ApiCalls do
                let apiDef =
                    apiCall.ApiDefId
                    |> Option.bind (fun defId ->
                        match store.ApiDefs.TryGetValue(defId) with
                        | true, d -> Some d | _ -> None)

                let outAddr = apiCall.OutTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""
                let inAddr = apiCall.InTag |> Option.map (fun t -> t.Address) |> Option.defaultValue ""

                if not (String.IsNullOrEmpty outAddr) || not (String.IsNullOrEmpty inAddr) then
                    mappings.Add({
                        ApiCallGuid = apiCall.Id
                        CallGuid = call.Id
                        TxWorkGuid = apiDef |> Option.bind (fun d -> d.TxGuid)
                        RxWorkGuid = apiDef |> Option.bind (fun d -> d.RxGuid)
                        OutAddress = outAddr
                        InAddress = inAddr
                        SystemId = callSystemId
                    })

        let list = mappings |> Seq.toList

        let outMap =
            list
            |> List.filter (fun m -> not (String.IsNullOrEmpty m.OutAddress))
            |> List.groupBy (fun m -> m.OutAddress)
            |> Map.ofList

        let inMap =
            list
            |> List.filter (fun m -> not (String.IsNullOrEmpty m.InAddress))
            |> List.groupBy (fun m -> m.InAddress)
            |> Map.ofList

        let callMap =
            list
            |> List.groupBy (fun m -> m.CallGuid)
            |> Map.ofList

        let txWorkMap =
            list
            |> List.filter (fun m -> m.TxWorkGuid.IsSome && not (String.IsNullOrEmpty m.OutAddress))
            |> List.groupBy (fun m -> m.TxWorkGuid.Value)
            |> List.map (fun (wg, ms) ->
                wg,
                (ms
                 |> List.map (fun m -> m.OutAddress)
                 |> List.distinct))
            |> Map.ofList

        let rxWorkMap =
            list
            |> List.filter (fun m -> m.RxWorkGuid.IsSome && not (String.IsNullOrEmpty m.InAddress))
            |> List.groupBy (fun m -> m.RxWorkGuid.Value)
            |> List.map (fun (wg, ms) ->
                wg,
                (ms
                 |> List.map (fun m -> m.InAddress)
                 |> List.distinct))
            |> Map.ofList

        {
            Mappings = list
            OutAddressToMappings = outMap
            InAddressToMappings = inMap
            CallToMappings = callMap
            TxWorkToOutAddresses = txWorkMap
            RxWorkToInAddresses = rxWorkMap
        }

    /// 프로젝트 전체 IO 매핑 (기존 동작 유지용 래퍼).
    let build (store: DsStore) : SignalIOMap =
        buildFiltered store None
