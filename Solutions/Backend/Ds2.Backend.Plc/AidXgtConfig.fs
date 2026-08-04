namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels

/// AID XGT interaction을 Agent/UA bridge가 소비하기 위한 평탄화 결과.
[<Sealed>]
type AidXgtSignalDescriptor internal (connectionName: string, address: string, signalId: string, valueType: string) =
    member _.ConnectionName = connectionName
    member _.Address = address
    member _.SignalId = signalId
    member _.ValueType = valueType

/// C# Agent 호출을 위한 AID XGT 계획 결과.
[<Sealed>]
type AidXgtConfigResult internal
    (config: PlcGatewayConfig, errors: string array, hasBinding: bool, signals: AidXgtSignalDescriptor array) =
    member _.Config = config
    member _.Errors = errors
    member _.HasBinding = hasBinding
    member _.Signals = signals
    member _.Success = not (obj.ReferenceEquals(config, null)) && errors.Length = 0

/// DualSoft InterfaceXGT 확장 바인딩 → 기존 검증된 PlcGatewayConfig 변환.
[<RequireQualifiedAccess>]
module AidXgtGatewayConfig =

    let private valueTypeName = function
        | XsDouble -> "double"
        | XsFloat -> "float"
        | XsInt -> "int"
        | XsLong -> "long"
        | XsUnsignedInt -> "uint"
        | XsUnsignedLong -> "ulong"
        | XsBoolean -> "boolean"
        | XsString -> "string"
        | XsDateTime -> "dateTime"
        | XsByteString -> "byteString"

    let private parseEndpoint (transport: XgtTransport) (value: string) =
        try
            if isNull value || value.Length > 2048 then
                invalidArg (nameof value) "EndpointMetadata.base exceeds 2048 characters."
            let uri = Uri(value, UriKind.Absolute)
            let expectedScheme = match transport with XgtTcp -> "xgt+tcp" | XgtUdp -> "xgt+udp"
            if String.IsNullOrWhiteSpace uri.Host then Error "InterfaceXGT EndpointMetadata.base에 host가 없습니다."
            elif not (uri.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase)) then
                Error(sprintf "InterfaceXGT EndpointMetadata.base scheme은 transport에 맞는 '%s'여야 합니다." expectedScheme)
            elif not (String.IsNullOrWhiteSpace uri.UserInfo) then
                Error "InterfaceXGT EndpointMetadata.base에 inline credential을 넣을 수 없습니다."
            elif not (String.IsNullOrEmpty uri.Fragment) then
                Error "InterfaceXGT EndpointMetadata.base에 URI fragment를 넣을 수 없습니다."
            else
                let port = if uri.Port > 0 then uri.Port else 2004
                Ok(uri.Host, port)
        with ex -> Error(sprintf "InterfaceXGT EndpointMetadata.base가 잘못되었습니다: %s" ex.Message)

    let private buildCore
        (samplingBySignalId: IReadOnlyDictionary<string, int>)
        (aid: AssetInterfacesDescription) : AidXgtConfigResult =
        let errors = ResizeArray<string>()
        let connections = ResizeArray<PlcConnectionConfig>()
        let signals = ResizeArray<AidXgtSignalDescriptor>()
        let seenSignalIds = HashSet<string>(StringComparer.Ordinal)
        let mutable index = 0

        for binding in aid.Interfaces do
            match binding with
            | Xgt (endpoint, interactions) ->
                index <- index + 1
                let connectionName = sprintf "AID-XGT#%d" index
                if endpoint.AuthReferenceVault.IsSome then
                    errors.Add(sprintf "InterfaceXGT #%d는 프로토콜 인증 필드가 없으므로 authReferenceVault를 사용할 수 없습니다." index)
                match parseEndpoint endpoint.Transport endpoint.Base with
                | Error message -> errors.Add message
                | Ok (host, port) ->
                    if interactions.IsEmpty then
                        errors.Add(sprintf "InterfaceXGT #%d에 InteractionMetadata가 없습니다." index)
                    else
                        let vendor =
                            match endpoint.CpuModel with
                            | Xgi -> PlcVendor.LsXgi
                            | Xgk -> PlcVendor.LsXgk
                            | Xgb -> PlcVendor.LsXgb
                        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        let tags =
                            interactions
                            |> List.choose (fun interaction ->
                                let address = if isNull interaction.Href then "" else interaction.Href.Trim()
                                if String.IsNullOrWhiteSpace address then
                                    errors.Add(sprintf "InterfaceXGT/%s href가 비어 있습니다." interaction.IdShort)
                                    None
                                elif String.IsNullOrWhiteSpace interaction.SignalId.Value then
                                    errors.Add(sprintf "InterfaceXGT/%s signalId가 비어 있습니다." interaction.IdShort)
                                    None
                                elif interaction.SignalId.Value.Length > 512 || address.Length > 4096
                                     || String.IsNullOrWhiteSpace interaction.IdShort || interaction.IdShort.Length > 256 then
                                    errors.Add(sprintf "InterfaceXGT/%s metadata가 허용 길이를 초과했습니다." interaction.IdShort)
                                    None
                                elif not (seenSignalIds.Add interaction.SignalId.Value) then
                                    errors.Add(sprintf "InterfaceXGT/%s signalId '%s'가 중복되었습니다." interaction.IdShort interaction.SignalId.Value)
                                    None
                                else
                                    signals.Add(
                                        AidXgtSignalDescriptor(
                                            connectionName,
                                            address,
                                            interaction.SignalId.Value,
                                            valueTypeName interaction.ValueType))
                                    if not (seen.Add address) then None
                                    else
                                        Some {
                                            HubAddress = address
                                            PlcAddress = address
                                            DataType = PlcAddressInfer.dataType vendor address
                                        }
                                )
                        if not tags.IsEmpty then
                            // Endpoint scanInterval은 장치 기본값이고, 신호별 CollectionPolicy가 더 빠른
                            // sampling을 요구하면 connection scan도 그 요구를 만족하도록 당긴다.
                            // 느린 신호의 최종 감산/deadband는 Collector MonitoredItem에서 수행한다.
                            let policySampling =
                                interactions
                                |> List.choose (fun interaction ->
                                    match samplingBySignalId.TryGetValue interaction.SignalId.Value with
                                    | true, interval when interval > 0 -> Some interval
                                    | _ -> None)
                            let scanIntervalMs =
                                match policySampling with
                                | [] -> endpoint.ScanIntervalMs
                                | values -> min endpoint.ScanIntervalMs (List.min values)
                            connections.Add {
                                Name = connectionName
                                Vendor = vendor
                                IpAddress = host
                                Port = port
                                LocalEthernet = endpoint.LocalEthernet
                                NetworkNumber = endpoint.NetworkNumber
                                StationNumber = endpoint.StationNumber
                                Transport = match endpoint.Transport with XgtTcp -> PlcTransport.Tcp | XgtUdp -> PlcTransport.Udp
                                TimeoutMs = max 100 endpoint.TimeoutMs
                                ScanInterval = Some(TimeSpan.FromMilliseconds(float (max 10 scanIntervalMs)))
                                Tags = tags
                            }
            | _ -> ()

        if connections.Count = 0 && errors.Count = 0 then
            errors.Add "AASX에 InterfaceXGT 바인딩이 없습니다."

        let config =
            if errors.Count > 0 then Unchecked.defaultof<PlcGatewayConfig>
            else { Connections = List.ofSeq connections }
        AidXgtConfigResult(config, errors.ToArray(), index > 0, signals.ToArray())

    let build (aid: AssetInterfacesDescription) : AidXgtConfigResult =
        let empty = Dictionary<string, int>() :> IReadOnlyDictionary<string, int>
        buildCore empty aid

    /// 테스트·도구용: 명시적인 SignalPolicy 집합을 XGT 계획에 적용한다.
    let buildWithPolicies
        (aid: AssetInterfacesDescription, policies: IEnumerable<SignalPolicy>) : AidXgtConfigResult =
        let sampling = Dictionary<string, int>(StringComparer.Ordinal)
        for policy in policies do
            match SignalPolicy.validate policy, policy.SamplingIntervalMs with
            | Ok (), Some interval -> sampling.[policy.SignalId.Value] <- interval
            | _ -> ()
        buildCore (sampling :> IReadOnlyDictionary<string, int>) aid

    /// Agent 정식 경로: Project active system의 SequenceLogging 정책을 모아 적용한다.
    let buildForProject
        (store: DsStore, project: Project, aid: AssetInterfacesDescription) : AidXgtConfigResult =
        let policies =
            Queries.activeSystemsOf project.Id store
            |> Seq.collect (fun system ->
                match system.GetLoggingProperties() with
                | Some logging -> logging.SignalPolicies :> seq<SignalPolicy>
                | None -> Seq.empty)
        buildWithPolicies(aid, policies)
