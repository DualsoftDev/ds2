namespace Ds2.Core.Store

open System
open Ds2.Core
open Ds2.Core.Store

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

    /// ApiCall에서 ApiDef.Name이 일치하는 것 찾기 (대소문자 무시) — 첫 매칭 한 개.
    /// B안 v2: 한 Call 안에 같은 ApiDef.Name 의 ApiCall 이 여러 개 있을 수 있음
    /// (예: CC1.RET / CC2.RET — 다른 Device 같은 API). 모든 매칭이 필요하면
    /// `findApiCallsByDeviceName` 사용.
    let findApiCallByDeviceName (call: Call) (deviceName: string) (store: DsStore) : ApiCall option =
        call.ApiCalls
        |> Seq.tryFind (fun ac ->
            ac.ApiDefId
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.exists (fun apiDef -> apiDef.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)))

    /// ApiCall 에서 ApiDef.Name 일치하는 것 모두 찾기 (B안 v2 — 다중 device 호출 지원).
    let findApiCallsByDeviceName (call: Call) (deviceName: string) (store: DsStore) : ApiCall list =
        call.ApiCalls
        |> Seq.filter (fun ac ->
            ac.ApiDefId
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.exists (fun apiDef -> apiDef.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)))
        |> Seq.toList

    /// Device alias + count → alias 목록 생성 (count=1이면 [alias], 아니면 [alias1..aliasN])
    let generateDeviceAliases (alias: string) (count: int) : string list =
        if count = 1 then [ alias ]
        else [ for i in 1..count -> $"{alias}{i}" ]

    /// DeviceAliases × ApiNames → Call 이름 목록 (e.g. "dev1.api1", "dev1.api2", ...)
    let generateCallNames (deviceAliases: string list) (apiNames: string list) : string list =
        [ for dev in deviceAliases do
            for api in apiNames do
                $"{dev}.{api}" ]
