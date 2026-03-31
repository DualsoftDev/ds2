namespace Ds2.Store.DsQuery

open System
open Ds2.Core
open Ds2.Store

/// 이름 기반 Call 검색 + Device 이름 생성
module Device =

    /// FlowName + WorkName + CallName 조합으로 Call 검색 (대소문자 무시)
    let findCallByName (flowName: string) (workName: string) (callName: string) (store: DsStore) : Call option =
        Queries.allFlows store
        |> List.tryPick (fun flow ->
            if not (flow.Name.Equals(flowName, StringComparison.OrdinalIgnoreCase)) then None
            else
                Queries.worksOf flow.Id store
                |> List.tryPick (fun work ->
                    if not (work.Name.Equals(workName, StringComparison.OrdinalIgnoreCase)) then None
                    else
                        Queries.callsOf work.Id store
                        |> List.tryFind (fun call ->
                            call.Name.Equals(callName, StringComparison.OrdinalIgnoreCase))))

    /// ApiCall에서 ApiDef.Name이 일치하는 것 찾기 (대소문자 무시)
    let findApiCallByDeviceName (call: Call) (deviceName: string) (store: DsStore) : ApiCall option =
        call.ApiCalls
        |> Seq.tryFind (fun ac ->
            ac.ApiDefId
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.exists (fun apiDef -> apiDef.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)))

    /// Device alias + count → alias 목록 생성 (count=1이면 [alias], 아니면 [alias1..aliasN])
    let generateDeviceAliases (alias: string) (count: int) : string list =
        if count = 1 then [ alias ]
        else [ for i in 1..count -> $"{alias}{i}" ]

    /// DeviceAliases × ApiNames → Call 이름 목록 (e.g. "dev1.api1", "dev1.api2", ...)
    let generateCallNames (deviceAliases: string list) (apiNames: string list) : string list =
        [ for dev in deviceAliases do
            for api in apiNames do
                $"{dev}.{api}" ]
