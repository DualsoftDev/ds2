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
    /// OutTag 주소 → 매핑 (Engine이 Out Write 시 사용)
    OutAddressToMapping: Map<string, SignalMapping>
    /// InTag 주소 → 매핑 (Hub에서 In 수신 시 사용)
    InAddressToMapping: Map<string, SignalMapping>
    /// Call Guid → 관련 매핑들
    CallToMappings: Map<Guid, SignalMapping list>
    /// TxWork Guid → Out 주소 (executeApiCall에서 사용)
    TxWorkToOutAddresses: Map<Guid, string list>
}

/// C#에서 사용하기 쉬운 SignalIOMap 조회 헬퍼
type SignalIOMap with
    /// OutTag 주소로 매핑 조회 (C#용). 없으면 null.
    member this.TryGetByOutAddress(address: string) : SignalMapping option =
        this.OutAddressToMapping |> Map.tryFind address

    /// InTag 주소로 매핑 조회 (C#용). 없으면 null.
    member this.TryGetByInAddress(address: string) : SignalMapping option =
        this.InAddressToMapping |> Map.tryFind address

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
            |> List.map (fun m -> m.OutAddress, m)
            |> Map.ofList

        let inMap =
            list
            |> List.filter (fun m -> not (String.IsNullOrEmpty m.InAddress))
            |> List.map (fun m -> m.InAddress, m)
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

        {
            Mappings = list
            OutAddressToMapping = outMap
            InAddressToMapping = inMap
            CallToMappings = callMap
            TxWorkToOutAddresses = txWorkMap
        }
