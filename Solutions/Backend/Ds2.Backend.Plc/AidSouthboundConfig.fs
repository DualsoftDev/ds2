namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels

[<RequireQualifiedAccess>]
type AidSouthboundProtocol =
    | OpcUa
    | Modbus
    | Mqtt
    | Http

[<Sealed>]
type AidSouthboundSignalDescriptor internal
    (signalId: string, idShort: string, semanticId: string, valueType: string,
     unitName: string, href: string, operation: string, mostSignificantWord: bool,
     scale: float, offset: float, qos: int, contentType: string, payloadPath: string,
     pollIntervalMs: Nullable<int>, samplingIntervalMs: Nullable<int>,
     publishingIntervalMs: Nullable<int>, deadbandAbsolute: Nullable<float>,
     deadbandPercent: Nullable<float>, queueSize: Nullable<int>) =
    member _.SignalId = signalId
    member _.IdShort = idShort
    member _.SemanticId = semanticId
    member _.ValueType = valueType
    member _.Unit = unitName
    member _.Href = href
    member _.Operation = operation
    member _.MostSignificantWord = mostSignificantWord
    member _.Scale = scale
    member _.Offset = offset
    member _.Qos = qos
    member _.ContentType = contentType
    member _.PayloadPath = payloadPath
    member _.PollIntervalMs = pollIntervalMs
    member _.SamplingIntervalMs = samplingIntervalMs
    member _.PublishingIntervalMs = publishingIntervalMs
    member _.DeadbandAbsolute = deadbandAbsolute
    member _.DeadbandPercent = deadbandPercent
    member _.QueueSize = queueSize

[<Sealed>]
type AidSouthboundEventDescriptor internal
    (signalId: string, idShort: string, semanticId: string, eventTypeSemanticId: string,
     sourceNodeHref: string, payloadPath: string) =
    member _.SignalId = signalId
    member _.IdShort = idShort
    member _.SemanticId = semanticId
    member _.EventTypeSemanticId = eventTypeSemanticId
    member _.SourceNodeHref = sourceNodeHref
    member _.PayloadPath = payloadPath

[<Sealed>]
type AidSouthboundEndpointDescriptor internal
    (name: string, protocol: AidSouthboundProtocol, baseAddress: string,
     security: string, unitId: Nullable<byte>, authReferenceVault: string,
     signals: AidSouthboundSignalDescriptor array, events: AidSouthboundEventDescriptor array) =
    member _.Name = name
    member _.Protocol = protocol
    member _.BaseAddress = baseAddress
    member _.Security = security
    member _.UnitId = unitId
    member _.AuthReferenceVault = authReferenceVault
    member _.Signals = signals
    member _.Events = events

[<Sealed>]
type AidSouthboundConfigResult internal
    (endpoints: AidSouthboundEndpointDescriptor array, errors: string array, hasBinding: bool) =
    member _.Endpoints = endpoints
    member _.Errors = errors
    member _.HasBinding = hasBinding
    member _.Success = errors.Length = 0
    member _.SignalCount = endpoints |> Array.sumBy (fun endpoint -> endpoint.Signals.Length)
    member _.EventCount = endpoints |> Array.sumBy (fun endpoint -> endpoint.Events.Length)

[<RequireQualifiedAccess>]
module AidSouthboundConfig =

    let private textTooLong maximum (value: string) =
        not (isNull value) && value.Length > maximum

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

    let private nullableOption value =
        match value with Some v -> Nullable v | None -> Nullable()

    let private policyLookup (errors: ResizeArray<string>) (policies: seq<SignalPolicy>) =
        let lookup = Dictionary<string, SignalPolicy>(StringComparer.Ordinal)
        for policy in policies do
            match SignalPolicy.validate policy with
            | Ok () ->
                if lookup.ContainsKey policy.SignalId.Value then
                    errors.Add(sprintf "CollectionPolicy/%s: duplicate signalId." policy.SignalId.Value)
                else
                    lookup.[policy.SignalId.Value] <- policy
            | Error message -> errors.Add(sprintf "CollectionPolicy/%s: %s" policy.SignalId.Value message)
        lookup

    let private httpHrefStaysOnEndpoint (baseAddress: string) (href: string) =
        try
            if String.IsNullOrWhiteSpace href || href.StartsWith("//", StringComparison.Ordinal)
               || href.StartsWith("\\", StringComparison.Ordinal) then false
            else
                let baseUri = Uri(baseAddress, UriKind.Absolute)
                let resolved = Uri(baseUri, href)
                resolved.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && resolved.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
                && resolved.Port = baseUri.Port
        with _ -> false

    let private securityContains (token: string) (security: string option) =
        security
        |> Option.exists (fun value -> value.Contains(token, StringComparison.OrdinalIgnoreCase))

    let private policyFields (policies: Dictionary<string, SignalPolicy>) signalId =
        match policies.TryGetValue signalId with
        | true, policy ->
            nullableOption policy.SamplingIntervalMs,
            nullableOption policy.PublishingIntervalMs,
            nullableOption policy.DeadbandAbsolute,
            nullableOption policy.DeadbandPercent,
            nullableOption policy.QueueSize
        | _ -> Nullable(), Nullable(), Nullable(), Nullable(), Nullable()

    let private addSignal
        (errors: ResizeArray<string>)
        (seenSignals: HashSet<string>)
        (policies: Dictionary<string, SignalPolicy>)
        (endpointName: string)
        (idShort: string)
        (semanticId: SemanticId)
        (valueType: XsdType)
        (unitName: string option)
        (href: string)
        (operation: string)
        (mostSignificantWord: bool)
        (scale: float)
        (offset: float)
        (qos: int)
        (contentType: string)
        (payloadPath: string)
        (pollIntervalMs: int option)
        (signalId: SignalId) =
        let sid = signalId.Value
        if String.IsNullOrWhiteSpace sid then
            errors.Add(sprintf "%s/%s: signalId is empty." endpointName idShort)
            None
        elif sid.Length > 512 then
            errors.Add(sprintf "%s/%s: signalId exceeds 512 characters." endpointName idShort)
            None
        elif not (seenSignals.Add sid) then
            errors.Add(sprintf "%s/%s: duplicate signalId '%s'." endpointName idShort sid)
            None
        elif String.IsNullOrWhiteSpace href then
            errors.Add(sprintf "%s/%s: href is empty." endpointName idShort)
            None
        elif href.Length > 4096 then
            errors.Add(sprintf "%s/%s: href exceeds 4096 characters." endpointName idShort)
            None
        elif String.IsNullOrWhiteSpace idShort || idShort.Length > 256 then
            errors.Add(sprintf "%s: interaction idShort is empty or exceeds 256 characters." endpointName)
            None
        elif textTooLong 2048 semanticId.Value
             || (unitName |> Option.exists (textTooLong 128))
             || textTooLong 256 contentType
             || textTooLong 1024 payloadPath then
            errors.Add(sprintf "%s/%s: interaction metadata exceeds supported length limits." endpointName idShort)
            None
        else
            let sampling, publishing, absolute, percent, queue = policyFields policies sid
            Some(AidSouthboundSignalDescriptor(
                sid, idShort, semanticId.Value, valueTypeName valueType,
                defaultArg unitName "", href.Trim(), operation, mostSignificantWord,
                scale, offset, qos, contentType, payloadPath, nullableOption pollIntervalMs,
                sampling, publishing, absolute, percent, queue))

    let private validateEndpoint
        (errors: ResizeArray<string>)
        (endpointName: string)
        (expectedSchemes: string list)
        (baseAddress: string)
        (authReference: string option) =
        if isNull baseAddress || baseAddress.Length > 2048 then
            errors.Add(sprintf "%s: EndpointMetadata.base exceeds 2048 characters." endpointName)
        match Uri.TryCreate(baseAddress, UriKind.Absolute) with
        | false, _ ->
            errors.Add(sprintf "%s: EndpointMetadata.base is not an absolute URI." endpointName)
        | true, uri when String.IsNullOrWhiteSpace uri.Host ->
            errors.Add(sprintf "%s: EndpointMetadata.base has no host." endpointName)
        | true, uri when not (expectedSchemes |> List.exists (fun scheme -> uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))) ->
            errors.Add(sprintf "%s: URI scheme '%s' is not supported." endpointName uri.Scheme)
        | true, uri when not (String.IsNullOrWhiteSpace uri.UserInfo) ->
            errors.Add(sprintf "%s: credentials in EndpointMetadata.base are forbidden; use authReferenceVault." endpointName)
        | true, uri when not (String.IsNullOrEmpty uri.Fragment) ->
            errors.Add(sprintf "%s: EndpointMetadata.base must not contain a URI fragment." endpointName)
        | _ -> ()
        match authReference with
        | Some value when textTooLong 2048 value ->
            errors.Add(sprintf "%s: authReferenceVault exceeds 2048 characters." endpointName)
        | Some value when not (String.IsNullOrWhiteSpace value) && not (value.StartsWith("@vault:", StringComparison.Ordinal)) ->
            errors.Add(sprintf "%s: authReferenceVault must use @vault:... and must not contain inline credentials." endpointName)
        | _ -> ()

    let private buildCore (aid: AssetInterfacesDescription) (policiesSeq: seq<SignalPolicy>) =
        let errors = ResizeArray<string>()
        let endpoints = ResizeArray<AidSouthboundEndpointDescriptor>()
        let seenSignals = HashSet<string>(StringComparer.Ordinal)
        let webhookRoutes = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let policies = policyLookup errors policiesSeq
        let mutable standardBindingCount = 0
        let mutable index = 0

        for binding in aid.Interfaces do
            match binding with
            | Xgt _ -> ()
            | OpcUa (endpoint, interactions, events) ->
                standardBindingCount <- standardBindingCount + 1
                index <- index + 1
                let name = sprintf "AID-OPCUA#%d" index
                validateEndpoint errors name ["opc.tcp"] endpoint.Base endpoint.AuthReferenceVault
                match Uri.TryCreate(endpoint.Base, UriKind.Absolute) with
                | true, uri when securityContains "none" endpoint.Security
                                 && (not uri.IsLoopback || not (securityContains "insecure-local" endpoint.Security)) ->
                    errors.Add(sprintf "%s: SecurityPolicy None is restricted to an explicitly marked insecure-local loopback endpoint." name)
                | _ -> ()
                if interactions.IsEmpty && events.IsEmpty then
                    errors.Add(sprintf "%s: no interactions or events are configured." name)
                let signals =
                    interactions
                    |> List.choose (fun item ->
                        addSignal errors seenSignals policies name
                            item.IdShort item.SemanticId item.ValueType item.Unit item.Href "read"
                            false 1.0 0.0 0 "" "" None item.SignalId)
                    |> List.toArray
                let eventItems =
                    events
                    |> List.choose (fun item ->
                        if String.IsNullOrWhiteSpace item.SignalId.Value then
                            errors.Add(sprintf "%s/%s: event signalId is empty." name item.IdShort)
                            None
                        elif item.SignalId.Value.Length > 512
                             || String.IsNullOrWhiteSpace item.IdShort || item.IdShort.Length > 256
                             || textTooLong 2048 item.SemanticId.Value
                             || textTooLong 2048 item.EventType.Value
                             || textTooLong 4096 item.SourceNodeHref
                             || textTooLong 1024 item.PayloadPath then
                            errors.Add(sprintf "%s/%s: event metadata exceeds supported length limits." name item.IdShort)
                            None
                        elif not (seenSignals.Add item.SignalId.Value) then
                            errors.Add(sprintf "%s/%s: duplicate signalId '%s'." name item.IdShort item.SignalId.Value)
                            None
                        elif String.IsNullOrWhiteSpace item.SourceNodeHref then
                            errors.Add(sprintf "%s/%s: event source href is empty." name item.IdShort)
                            None
                        else
                            Some(AidSouthboundEventDescriptor(
                                item.SignalId.Value, item.IdShort, item.SemanticId.Value,
                                item.EventType.Value, item.SourceNodeHref.Trim(), item.PayloadPath)))
                    |> List.toArray
                endpoints.Add(AidSouthboundEndpointDescriptor(
                    name, AidSouthboundProtocol.OpcUa, endpoint.Base, defaultArg endpoint.Security "",
                    Nullable(), defaultArg endpoint.AuthReferenceVault "", signals, eventItems))

            | Modbus (endpoint, interactions) ->
                standardBindingCount <- standardBindingCount + 1
                index <- index + 1
                let name = sprintf "AID-MODBUS#%d" index
                validateEndpoint errors name ["modbus+tcp"; "tcp"] endpoint.Base endpoint.AuthReferenceVault
                if interactions.IsEmpty then errors.Add(sprintf "%s: no interactions are configured." name)
                if endpoint.AuthReferenceVault.IsSome then
                    errors.Add(sprintf "%s: Modbus TCP adapter has no authentication field; remove authReferenceVault or use a secured gateway." name)
                let signals =
                    interactions
                    |> List.choose (fun item ->
                        let operation =
                            match item.Function with
                            | ReadHoldingRegisters -> "readHoldingRegisters"
                            | ReadInputRegisters -> "readInputRegisters"
                            | ReadCoils -> "readCoils"
                            | ReadDiscreteInputs -> "readDiscreteInputs"
                            | WriteSingleRegister -> "writeSingleRegister"
                            | WriteMultipleRegisters -> "writeMultipleRegisters"
                        if operation.StartsWith("write", StringComparison.Ordinal) then
                            errors.Add(sprintf "%s/%s: write-only Modbus interactions cannot populate a read-only telemetry node." name item.IdShort)
                            None
                        else
                            addSignal errors seenSignals policies name
                                item.IdShort item.SemanticId item.ValueType item.Unit item.Href operation
                                item.MostSignificantWord item.Scale item.Offset 0 "" "" None item.SignalId)
                    |> List.toArray
                endpoints.Add(AidSouthboundEndpointDescriptor(
                    name, AidSouthboundProtocol.Modbus, endpoint.Base, defaultArg endpoint.Security "",
                    endpoint.UnitId |> nullableOption, defaultArg endpoint.AuthReferenceVault "", signals, [||]))

            | Mqtt (endpoint, interactions) ->
                standardBindingCount <- standardBindingCount + 1
                index <- index + 1
                let name = sprintf "AID-MQTT#%d" index
                validateEndpoint errors name ["mqtt"; "mqtts"] endpoint.Base endpoint.AuthReferenceVault
                if interactions.IsEmpty then errors.Add(sprintf "%s: no interactions are configured." name)
                match Uri.TryCreate(endpoint.Base, UriKind.Absolute) with
                | true, uri when uri.Scheme.Equals("mqtt", StringComparison.OrdinalIgnoreCase)
                                 && not (securityContains "tls" endpoint.Security)
                                 && not uri.IsLoopback
                                 && not (securityContains "insecure-private" endpoint.Security) ->
                    errors.Add(sprintf "%s: plaintext MQTT requires an explicit insecure-private security marker; use mqtts:// or TLS by default." name)
                | _ -> ()
                match Uri.TryCreate(endpoint.Base, UriKind.Absolute), endpoint.AuthReferenceVault, endpoint.Security with
                | (true, uri), Some _, security
                    when uri.Scheme.Equals("mqtt", StringComparison.OrdinalIgnoreCase)
                         && not ((defaultArg security "").Contains("tls", StringComparison.OrdinalIgnoreCase)) ->
                    errors.Add(sprintf "%s: credentialed MQTT requires mqtts:// or an explicit TLS security profile." name)
                | _ -> ()
                let signals =
                    interactions
                    |> List.choose (fun item ->
                        if item.Qos < 0 || item.Qos > 2 then
                            errors.Add(sprintf "%s/%s: MQTT QoS must be 0, 1, or 2." name item.IdShort)
                            None
                        elif item.ControlPacket = Publish then
                            errors.Add(sprintf "%s/%s: publish-only MQTT interactions cannot populate a read-only telemetry node." name item.IdShort)
                            None
                        else
                            addSignal errors seenSignals policies name
                                item.IdShort item.SemanticId item.ValueType item.Unit item.Href "subscribe"
                                false 1.0 0.0 item.Qos item.ContentType item.PayloadPath None item.SignalId)
                    |> List.toArray
                endpoints.Add(AidSouthboundEndpointDescriptor(
                    name, AidSouthboundProtocol.Mqtt, endpoint.Base, defaultArg endpoint.Security "",
                    Nullable(), defaultArg endpoint.AuthReferenceVault "", signals, [||]))

            | Http (endpoint, interactions) ->
                standardBindingCount <- standardBindingCount + 1
                index <- index + 1
                let name = sprintf "AID-HTTP#%d" index
                validateEndpoint errors name ["http"; "https"] endpoint.Base endpoint.AuthReferenceVault
                if interactions.IsEmpty then errors.Add(sprintf "%s: no interactions are configured." name)
                match Uri.TryCreate(endpoint.Base, UriKind.Absolute) with
                | true, uri when uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                                 && not uri.IsLoopback
                                 && not (securityContains "insecure-private" endpoint.Security) ->
                    errors.Add(sprintf "%s: plaintext HTTP requires an explicit insecure-private security marker; use https:// by default." name)
                | _ -> ()
                match Uri.TryCreate(endpoint.Base, UriKind.Absolute), endpoint.AuthReferenceVault with
                | (true, uri), Some _ when uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ->
                    errors.Add(sprintf "%s: credentialed HTTP requires https://." name)
                | _ -> ()
                let signals =
                    interactions
                    |> List.choose (fun item ->
                        if not (httpHrefStaysOnEndpoint endpoint.Base item.Href) then
                            errors.Add(sprintf "%s/%s: HTTP href must stay on the endpoint base origin." name item.IdShort)
                            None
                        else
                            let operation =
                                match item.Method with Get -> "GET" | Post -> "POST" | Put -> "PUT" | Delete -> "DELETE"
                            match item.PollIntervalMs with
                            | None ->
                                if operation <> "POST" && operation <> "PUT" then
                                    errors.Add(sprintf "%s/%s: webhook telemetry interactions must use POST or PUT." name item.IdShort)
                                if endpoint.AuthReferenceVault.IsNone then
                                    errors.Add(sprintf "%s/%s: webhook HTTP interaction requires authReferenceVault." name item.IdShort)
                                let route = Uri(Uri(endpoint.Base), item.Href).AbsolutePath
                                if route.StartsWith("/hub", StringComparison.OrdinalIgnoreCase)
                                   || route.Equals("/healthz", StringComparison.OrdinalIgnoreCase) then
                                    errors.Add(sprintf "%s/%s: webhook route '%s' is reserved." name item.IdShort route)
                                if not (webhookRoutes.Add(operation + " " + route)) then
                                    errors.Add(sprintf "%s/%s: duplicate webhook route '%s %s'." name item.IdShort operation route)
                                addSignal errors seenSignals policies name
                                    item.IdShort item.SemanticId item.ValueType item.Unit item.Href operation
                                    false 1.0 0.0 0 item.ContentType item.PayloadPath None item.SignalId
                            | Some interval when interval < 10 ->
                                errors.Add(sprintf "%s/%s: pollIntervalMs must be at least 10." name item.IdShort)
                                None
                            | Some _ when operation <> "GET" ->
                                errors.Add(sprintf "%s/%s: polled telemetry interactions must use GET; state-changing HTTP methods are forbidden." name item.IdShort)
                                None
                            | _ ->
                                addSignal errors seenSignals policies name
                                    item.IdShort item.SemanticId item.ValueType item.Unit item.Href operation
                                    false 1.0 0.0 0 item.ContentType item.PayloadPath item.PollIntervalMs item.SignalId)
                    |> List.toArray
                endpoints.Add(AidSouthboundEndpointDescriptor(
                    name, AidSouthboundProtocol.Http, endpoint.Base, defaultArg endpoint.Security "",
                    Nullable(), defaultArg endpoint.AuthReferenceVault "", signals, [||]))

        AidSouthboundConfigResult(endpoints.ToArray(), errors.ToArray(), standardBindingCount > 0)

    let build (aid: AssetInterfacesDescription) =
        buildCore aid Seq.empty

    let buildWithPolicies (aid: AssetInterfacesDescription, policies: IEnumerable<SignalPolicy>) =
        buildCore aid policies

    let buildForProject (store: DsStore, project: Project, aid: AssetInterfacesDescription) =
        let policies =
            Queries.activeSystemsOf project.Id store
            |> Seq.collect (fun system ->
                match system.GetLoggingProperties() with
                | Some logging -> logging.SignalPolicies :> seq<SignalPolicy>
                | None -> Seq.empty)
        buildCore aid policies
