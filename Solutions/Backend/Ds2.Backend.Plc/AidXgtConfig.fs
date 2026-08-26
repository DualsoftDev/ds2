namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels

/// AID XGT interaction을 Agent/UA bridge가 소비하기 위한 평탄화 결과.
[<Sealed>]
type AidXgtSignalDescriptor internal (connectionName: string, systemId: Guid option, address: string, signalId: string, valueType: string) =
    member _.ConnectionName = connectionName
    member _.SystemId = systemId |> Option.toNullable
    member _.Address = address
    member _.SignalId = signalId
    member _.ValueType = valueType

/// C# Agent 호출을 위한 AID XGT 계획 결과.
[<Sealed>]
type AidXgtConfigResult internal
    (config: PlcGatewayConfig, errors: string array, warnings: string array, notices: string array,
     hasBinding: bool, signals: AidXgtSignalDescriptor array) =
    member _.Config = config
    member _.Errors = errors
    /// 활성화를 막지는 않지만 **사람이 모델을 고쳐야 하는** 사항. 현재는 같은 System 안 주소 충돌
    /// (복합키로도 구분 불가). Errors 와 달리 Success 판정에 영향을 주지 않는다 — 기존 현장이 그대로
    /// 뜨는 게 우선이고, 대신 눈에 띄게 남긴다.
    member _.Warnings = warnings
    /// 정상 지원되는 구성인데 기록해 둘 가치가 있는 사항. 현재는 System 간 주소 중복 —
    /// (SystemId, 주소) 복합키로 구분되므로 조치가 필요 없다. 다만 systemId 를 안 싣는 구버전
    /// 수집기/DSPilot 이 섞이면 여전히 구분이 안 되므로 진단 근거로 남긴다.
    member _.Notices = notices
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
                                            endpoint.SystemId,
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
                                SystemId = endpoint.SystemId
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

        // 연결(=PLC) 간 주소 중복 진단. 주소는 System 마다 독립적으로 쓸 수 있게 설계돼 있고
        // 하위 계층이 (SystemId, 주소) 복합키를 쓰므로 System 간 중복은 **에러도 경고도 아니다**(정보).
        // 반면 같은 System 안 중복은 복합키로도 못 가르므로 사람이 고쳐야 한다 → 경고.
        let describeOwners (dup: TagAddressDuplicate) =
            dup.Owners
            |> List.map (fun o ->
                match o.SystemId with
                | Some sid -> sprintf "%s(system %O)" o.ConnectionName sid
                | None     -> sprintf "%s(system 미상)" o.ConnectionName)
            |> String.concat ", "
        let duplicates = PlcGatewayConfig.duplicateAddresses { Connections = List.ofSeq connections }
        let notices =
            duplicates
            |> List.filter (fun dup -> dup.Conflict = AcrossSystems)
            |> List.map (fun dup ->
                sprintf
                    "주소 '%s'를 서로 다른 System 의 PLC 가 함께 사용합니다: %s. \
                     (SystemId, 주소) 복합키로 구분되므로 조치는 필요 없습니다. \
                     단, systemId 를 싣지 않는 구버전 수집기/DSPilot 이 섞여 있으면 구분되지 않습니다."
                    dup.Address (describeOwners dup))
        let warnings =
            duplicates
            |> List.filter (fun dup -> dup.Conflict = WithinSameSystem)
            |> List.map (fun dup ->
                sprintf
                    "주소 '%s'를 같은 System(또는 귀속 미상)의 연결 여러 개가 사용합니다: %s. \
                     복합키로도 구분되지 않으므로 모델의 주소 배정을 수정해야 합니다."
                    dup.Address (describeOwners dup))

        let config =
            if errors.Count > 0 then Unchecked.defaultof<PlcGatewayConfig>
            else { Connections = List.ofSeq connections }
        AidXgtConfigResult(
            config, errors.ToArray(), List.toArray warnings, List.toArray notices,
            index > 0, signals.ToArray())

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

    let private bindLegacyEndpointsToOnlySystem (systems: DsSystem list) (aid: AssetInterfacesDescription) =
        match systems with
        | [ system ] ->
            for index = 0 to aid.Interfaces.Count - 1 do
                match aid.Interfaces.[index] with
                | Xgt (endpoint, interactions) when endpoint.SystemId.IsNone ->
                    aid.Interfaces.[index] <- Xgt ({ endpoint with SystemId = Some system.Id }, interactions)
                | _ -> ()
        | _ -> ()

    let private validateEndpointSystemRefs (systems: DsSystem list) (aid: AssetInterfacesDescription) =
        let activeIds = systems |> Seq.map _.Id |> HashSet
        [ for binding in aid.Interfaces do
              match binding with
              | Xgt (endpoint, _) ->
                  match endpoint.SystemId with
                  | None when systems.Length > 1 ->
                      yield "Project에 active System이 여러 개인 경우 모든 InterfaceXGT EndpointMetadata.systemRef가 필요합니다."
                  | Some systemId when not (activeIds.Contains systemId) ->
                      yield $"InterfaceXGT EndpointMetadata.systemRef '{systemId}'가 이 Project의 active System이 아닙니다."
                  | _ -> ()
              | _ -> () ]

    /// Agent 정식 경로: Project active system의 SequenceLogging 정책을 모아 적용한다.
    let buildForProject
        (store: DsStore, project: Project, aid: AssetInterfacesDescription) : AidXgtConfigResult =
        let systems = Queries.activeSystemsOf project.Id store
        // One-System legacy files had no endpoint owner.  The association is
        // unambiguous there, so normalize it before the plan is built/exported.
        bindLegacyEndpointsToOnlySystem systems aid
        // 중복 주소 모델은 AASX 저장까지는 성공하고 활성화에서만 실패하므로, 현장에 "저장은 됐는데
        // 안 뜨는" 파일이 이미 있을 수 있다. 같은 자동 분화 규칙으로 여기서 복구해 사용자가
        // 아무것도 하지 않아도 뜨게 한다(systemRef 귀속이 끝난 뒤여야 한정자를 만들 수 있어 순서가 중요).
        AidXgtEndpointSettings.deduplicateSignalIds aid |> ignore
        let policies =
            systems
            |> Seq.collect (fun system ->
                match system.GetLoggingProperties() with
                | Some logging -> logging.SignalPolicies :> seq<SignalPolicy>
                | None -> Seq.empty)
        let result = buildWithPolicies(aid, policies)
        let ownershipErrors = validateEndpointSystemRefs systems aid
        if ownershipErrors.IsEmpty then result
        else
            AidXgtConfigResult(
                Unchecked.defaultof<PlcGatewayConfig>,
                Array.append result.Errors (ownershipErrors |> List.toArray),
                result.Warnings,
                result.Notices,
                result.HasBinding,
                result.Signals)
