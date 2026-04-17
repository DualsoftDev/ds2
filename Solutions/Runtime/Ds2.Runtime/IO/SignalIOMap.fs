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

    /// DsStore에서 IO 매핑 빌드
    let build (store: DsStore) : SignalIOMap =
        let mappings = ResizeArray<SignalMapping>()

        for call in store.Calls.Values do
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
            |> List.map (fun (wg, ms) -> wg, ms |> List.map (fun m -> m.OutAddress))
            |> Map.ofList

        let rxWorkMap =
            list
            |> List.filter (fun m -> m.RxWorkGuid.IsSome && not (String.IsNullOrEmpty m.InAddress))
            |> List.groupBy (fun m -> m.RxWorkGuid.Value)
            |> List.map (fun (wg, ms) -> wg, ms |> List.map (fun m -> m.InAddress))
            |> Map.ofList

        {
            Mappings = list
            OutAddressToMappings = outMap
            InAddressToMappings = inMap
            CallToMappings = callMap
            TxWorkToOutAddresses = txWorkMap
            RxWorkToInAddresses = rxWorkMap
        }
