namespace Ds2.OpcUa.Server.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Opc.Ua
open Opc.Ua.Server
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds

/// ADR-001 · Pure Aggregator NodeManager.
///
/// 서버는 southbound driver 를 갖지 않음. 어댑터가 UA Write / Method Call 로 값 반영.
type internal AssetContext = {
    GlobalAssetId  : GlobalAssetId
    NsIndex        : int
    AssetFolder    : FolderState
    Variables      : ConcurrentDictionary<string, BaseDataVariableState>
    VariableTypes  : ConcurrentDictionary<string, BuiltInType>
    EventsFolder   : FolderState
    RaiseEventMethod : MethodState
}

module private DataTypes =
    let ofBuiltIn (t: BuiltInType) : NodeId =
        match t with
        | BuiltInType.Boolean       -> DataTypeIds.Boolean
        | BuiltInType.SByte         -> DataTypeIds.SByte
        | BuiltInType.Byte          -> DataTypeIds.Byte
        | BuiltInType.Int16         -> DataTypeIds.Int16
        | BuiltInType.UInt16        -> DataTypeIds.UInt16
        | BuiltInType.Int32         -> DataTypeIds.Int32
        | BuiltInType.UInt32        -> DataTypeIds.UInt32
        | BuiltInType.Int64         -> DataTypeIds.Int64
        | BuiltInType.UInt64        -> DataTypeIds.UInt64
        | BuiltInType.Float         -> DataTypeIds.Float
        | BuiltInType.Double        -> DataTypeIds.Double
        | BuiltInType.String        -> DataTypeIds.String
        | BuiltInType.DateTime      -> DataTypeIds.DateTime
        | BuiltInType.Guid          -> DataTypeIds.Guid
        | BuiltInType.ByteString    -> DataTypeIds.ByteString
        | BuiltInType.XmlElement    -> DataTypeIds.XmlElement
        | _                         -> DataTypeIds.BaseDataType

    let defaultValue (t: BuiltInType) : obj =
        match t with
        | BuiltInType.Boolean    -> box false
        | BuiltInType.SByte      -> box 0y
        | BuiltInType.Byte       -> box 0uy
        | BuiltInType.Int16      -> box 0s
        | BuiltInType.UInt16     -> box 0us
        | BuiltInType.Int32      -> box 0
        | BuiltInType.UInt32     -> box 0u
        | BuiltInType.Int64      -> box 0L
        | BuiltInType.UInt64     -> box 0UL
        | BuiltInType.Float      -> box 0.0f
        | BuiltInType.Double     -> box 0.0
        | BuiltInType.String     -> box ""
        | BuiltInType.DateTime   -> box DateTime.MinValue
        | BuiltInType.Guid       -> box Guid.Empty
        | BuiltInType.ByteString -> box Array.empty<byte>
        | _                      -> null

    /// 외부 adapter가 문자열을 넘겨도 Variable 선언 타입으로 중앙 변환한다.
    /// 변환 불가능한 값은 호출자가 BadTypeMismatch로 품질을 전환하도록 Error를 반환한다.
    let tryCoerce (target: BuiltInType) (value: obj) : Result<obj, string> =
        let inv = CultureInfo.InvariantCulture
        try
            if isNull value then
                Error "null is not valid for a scalar value"
            else
                let converted : obj =
                    match target with
                    | BuiltInType.Boolean ->
                        match value with
                        | :? bool -> value
                        | :? string as s when s = "1" -> box true
                        | :? string as s when s = "0" -> box false
                        | _ -> box (Convert.ToBoolean(value, inv))
                    | BuiltInType.SByte -> box (Convert.ToSByte(value, inv))
                    | BuiltInType.Byte -> box (Convert.ToByte(value, inv))
                    | BuiltInType.Int16 -> box (Convert.ToInt16(value, inv))
                    | BuiltInType.UInt16 -> box (Convert.ToUInt16(value, inv))
                    | BuiltInType.Int32 -> box (Convert.ToInt32(value, inv))
                    | BuiltInType.UInt32 -> box (Convert.ToUInt32(value, inv))
                    | BuiltInType.Int64 -> box (Convert.ToInt64(value, inv))
                    | BuiltInType.UInt64 -> box (Convert.ToUInt64(value, inv))
                    | BuiltInType.Float -> box (Convert.ToSingle(value, inv))
                    | BuiltInType.Double -> box (Convert.ToDouble(value, inv))
                    | BuiltInType.String -> box (Convert.ToString(value, inv))
                    | BuiltInType.DateTime ->
                        match value with
                        | :? DateTime -> value
                        | :? DateTimeOffset as dto -> box dto.UtcDateTime
                        | :? string as s ->
                            box (DateTime.Parse(s, inv, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal))
                        | _ -> box (Convert.ToDateTime(value, inv))
                    | BuiltInType.Guid ->
                        match value with
                        | :? Guid -> value
                        | _ -> box (Guid.Parse(Convert.ToString(value, inv)))
                    | BuiltInType.ByteString ->
                        match value with
                        | :? (byte array) -> value
                        | :? string as s ->
                            try box (Convert.FromBase64String s)
                            with :? FormatException -> box (Encoding.UTF8.GetBytes s)
                        | _ -> invalidArg "value" "ByteString requires byte[] or string"
                    | _ -> invalidArg "target" (sprintf "unsupported scalar BuiltInType: %O" target)
                Ok converted
        with ex -> Error ex.Message

module private AssetEvents =
    [<Literal>]
    let MaxPayloadBytes = 262_144

    let private forbiddenTimestampFields =
        HashSet<string>(
            [| "sourceTimestamp"; "time"; "timestamp"; "receiveTime" |],
            StringComparer.OrdinalIgnoreCase)

    let rec private hasForbiddenTimestampField (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.exists (fun property ->
                forbiddenTimestampFields.Contains property.Name
                || hasForbiddenTimestampField property.Value)
        | JsonValueKind.Array ->
            element.EnumerateArray() |> Seq.exists hasForbiddenTimestampField
        | _ -> false

    let tryCreateWireMessage eventTypeSemanticId sourceSignalId payloadJson =
        if String.IsNullOrWhiteSpace eventTypeSemanticId then Error "eventTypeSemanticId is required"
        elif String.IsNullOrWhiteSpace sourceSignalId then Error "sourceSignalId is required"
        elif String.IsNullOrWhiteSpace payloadJson then Error "payloadJson is required"
        elif Encoding.UTF8.GetByteCount payloadJson > MaxPayloadBytes then
            Error(sprintf "payloadJson exceeds %d bytes" MaxPayloadBytes)
        else
            try
                use document = JsonDocument.Parse payloadJson
                if hasForbiddenTimestampField document.RootElement then
                    Error "payloadJson must not contain timestamp fields"
                else
                    let wrapper = JsonObject()
                    wrapper.["eventTypeSemanticId"] <- JsonValue.Create eventTypeSemanticId
                    wrapper.["sourceSignalId"] <- JsonValue.Create sourceSignalId
                    wrapper.["payload"] <- JsonNode.Parse payloadJson
                    Ok(wrapper.ToJsonString())
            with :? JsonException as ex -> Error("payloadJson is not valid JSON: " + ex.Message)

type DsNodeManager(server: IServerInternal,
                   configuration: ApplicationConfiguration,
                   allocator: INamespaceAllocator,
                   managedNamespaceUris: string array,
                   defaultSamplingIntervalMs: int,
                   allowExternalEventInjection: bool) as this =
    inherit CustomNodeManager2(server, configuration)

    static let log = log4net.LogManager.GetLogger("DsNodeManager")
    let assets = ConcurrentDictionary<GlobalAssetId, AssetContext>()
    let mutable typeMismatchCount = 0L
    let mutable rootFolder : FolderState = Unchecked.defaultof<_>
    let mutable assetsFolder : FolderState = Unchecked.defaultof<_>

    do
        // 서버 own namespace (ns=1) 을 등록. DS 폴더는 이 ns 에 배치.
        let namespaces =
            Array.append [| "urn:dualsoft:opcua:server" |] managedNamespaceUris
            |> Array.distinct
        this.SetNamespaces(namespaces)

    /// protected AddPredefinedNode 를 lambda 밖에서 호출할 수 있도록 래핑.
    member private this.AttachPredefined(node: NodeState) =
        this.AddPredefinedNode(this.SystemContext, node)

    /// 자산 namespace 를 이 NodeManager 가 소유하도록 등록 (ADR-002 §5 hot-append).
    /// SetNamespaces 는 매번 전체 목록을 재설정하므로 기존 + 새 것을 합쳐 호출.
    member private this.EnsureManagedNamespace(uri: string) : uint16 =
        let existing = base.NamespaceUris
        let owned =
            [| for u in existing do
                 if not (isNull u) then yield u |]
        let owned2 =
            if Array.contains uri owned then owned
            else Array.append owned [| uri |]
        if owned2.Length <> owned.Length then
            base.SetNamespaces owned2
        // 서버 전역 table 에도 등록 (client 에게 광고).
        let table = server.NamespaceUris
        let mutable idx = table.GetIndex uri
        if idx < 0 then
            idx <- int (table.Append uri)
        uint16 idx

    override this.CreateAddressSpace(externalReferences: IDictionary<NodeId, IList<IReference>>) =
        base.CreateAddressSpace(externalReferences)

        // "urn:dualsoft:opcua:server" 는 이미 SetNamespaces 로 등록됨.
        // 이 URI 의 server-side index 를 얻어서 DS 폴더 배치.
        let dsNs = server.NamespaceUris.GetIndex "urn:dualsoft:opcua:server"
        let dsNs = if dsNs < 0 then 1 else dsNs

        Monitor.Enter this.Lock
        try
            let ds = new FolderState(null)
            ds.SymbolicName <- "DS"
            ds.NodeId <- NodeId("DS", uint16 dsNs)
            ds.BrowseName <- QualifiedName("DS", uint16 dsNs)
            ds.DisplayName <- LocalizedText "DS"
            ds.EventNotifier <- EventNotifiers.None
            rootFolder <- ds

            match externalReferences.TryGetValue(ObjectIds.ObjectsFolder) with
            | true, refs ->
                refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, rootFolder.NodeId))
            | false, _ ->
                let list = new List<IReference>()
                list.Add(new NodeStateReference(ReferenceTypes.Organizes, false, rootFolder.NodeId))
                externalReferences.[ObjectIds.ObjectsFolder] <- list :> IList<_>

            this.AttachPredefined rootFolder

            let f = new FolderState(rootFolder)
            f.SymbolicName <- "Assets"
            f.NodeId <- NodeId("DS/Assets", uint16 dsNs)
            f.BrowseName <- QualifiedName("Assets", uint16 dsNs)
            f.DisplayName <- LocalizedText "Assets"
            f.EventNotifier <- EventNotifiers.None
            rootFolder.AddChild(f)
            this.AttachPredefined f
            assetsFolder <- f
        finally
            Monitor.Exit this.Lock

    /// idShort 안의 UA SymbolicName 규칙에 맞지 않는 문자를 `_` 로 치환.
    /// (알파벳/숫자/underscore 만 허용 — 폴더 leaf display 는 원본 유지, symbolic 만 sanitize)
    static member private SanitizeSymbolic (raw: string) : string =
        if String.IsNullOrEmpty raw then "_"
        else
            let sb = System.Text.StringBuilder(raw.Length)
            for c in raw do
                if (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_' then
                    sb.Append(c) |> ignore
                else
                    sb.Append('_') |> ignore
            sb.ToString()

    /// ADR-003 · asset 하나에 Events 폴더 + RaiseAssetEvent Method 를 배치.
    /// 시그니처: (eventTypeSemanticId, sourceSignalId, sourceTimestamp, payloadJson) → (eventId, statusCode).
    /// 리턴: (eventsFolder, raiseMethod) — caller 가 AssetContext 에 저장.
    member private this.BuildEventsSubtree(
            gaid: GlobalAssetId,
            assetFolder: FolderState,
            idShort: string,
            serverNs: int)
            : FolderState * MethodState =
        let eventsFolder = new FolderState(assetFolder)
        eventsFolder.SymbolicName <- "Events"
        eventsFolder.NodeId <- NodeId("Events", uint16 serverNs)
        eventsFolder.BrowseName <- QualifiedName("Events", uint16 serverNs)
        eventsFolder.DisplayName <- LocalizedText "Events"
        eventsFolder.EventNotifier <- EventNotifiers.SubscribeToEvents
        eventsFolder.ReferenceTypeId <- ReferenceTypeIds.Organizes
        eventsFolder.TypeDefinitionId <- ObjectTypeIds.FolderType
        assetFolder.AddChild(eventsFolder)
        this.AttachPredefined eventsFolder

        let raiseMethod = new MethodState(eventsFolder)
        raiseMethod.SymbolicName <- "RaiseAssetEvent"
        raiseMethod.NodeId <- NodeId("Events/RaiseAssetEvent", uint16 serverNs)
        raiseMethod.BrowseName <- QualifiedName("RaiseAssetEvent", uint16 serverNs)
        raiseMethod.DisplayName <- LocalizedText "RaiseAssetEvent"
        raiseMethod.ReferenceTypeId <- ReferenceTypeIds.HasComponent
        raiseMethod.Executable <- allowExternalEventInjection
        raiseMethod.UserExecutable <- allowExternalEventInjection

        let inArgs = new PropertyState<Argument array>(raiseMethod)
        inArgs.NodeId <- NodeId(sprintf "Events/RaiseAssetEvent/In-%s" idShort, uint16 serverNs)
        inArgs.BrowseName <- BrowseNames.InputArguments
        inArgs.DisplayName <- LocalizedText BrowseNames.InputArguments
        inArgs.TypeDefinitionId <- VariableTypeIds.PropertyType
        inArgs.ReferenceTypeId <- ReferenceTypeIds.HasProperty
        inArgs.DataType <- DataTypeIds.Argument
        inArgs.ValueRank <- ValueRanks.OneDimension
        inArgs.Value <- [|
            Argument(Name = "eventTypeSemanticId", DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = LocalizedText "OPC 30010 등 EventType semanticId")
            Argument(Name = "sourceSignalId", DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = LocalizedText "AID InteractionMetadata signalId")
            Argument(Name = "sourceTimestamp", DataType = DataTypeIds.DateTime, ValueRank = ValueRanks.Scalar, Description = LocalizedText "원천 발생 시각 (ADR-003 §1a 단일 원천)")
            Argument(Name = "payloadJson", DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = LocalizedText "이벤트 payload JSON (시각 필드 금지 · ADR-003 §4)")
        |]
        raiseMethod.InputArguments <- inArgs

        let outArgs = new PropertyState<Argument array>(raiseMethod)
        outArgs.NodeId <- NodeId(sprintf "Events/RaiseAssetEvent/Out-%s" idShort, uint16 serverNs)
        outArgs.BrowseName <- BrowseNames.OutputArguments
        outArgs.DisplayName <- LocalizedText BrowseNames.OutputArguments
        outArgs.TypeDefinitionId <- VariableTypeIds.PropertyType
        outArgs.ReferenceTypeId <- ReferenceTypeIds.HasProperty
        outArgs.DataType <- DataTypeIds.Argument
        outArgs.ValueRank <- ValueRanks.OneDimension
        outArgs.Value <- [|
            Argument(Name = "eventId", DataType = DataTypeIds.ByteString, ValueRank = ValueRanks.Scalar)
            Argument(Name = "statusCode", DataType = DataTypeIds.Int32, ValueRank = ValueRanks.Scalar)
        |]
        raiseMethod.OutputArguments <- outArgs

        raiseMethod.OnCallMethod <- GenericMethodCalledEventHandler(fun _ctx _meth inputArgs outputArgs ->
            if not allowExternalEventInjection then
                new ServiceResult(StatusCodes.BadUserAccessDenied)
            elif inputArgs.Count < 4 then
                new ServiceResult(StatusCodes.BadArgumentsMissing)
            else
                let eventTypeSemId = string inputArgs.[0]
                let sourceSignalId = string inputArgs.[1]
                let sourceTs =
                    match inputArgs.[2] with
                    | :? DateTime as dt -> dt
                    | _ -> DateTime.UtcNow
                let payloadJson = string inputArgs.[3]
                try
                    match this.RaiseAssetEvent(gaid, eventTypeSemId, sourceSignalId, sourceTs, payloadJson) with
                    | Some eventIdBytes ->
                        if outputArgs.Count > 0 then outputArgs.[0] <- (eventIdBytes :> obj)
                        if outputArgs.Count > 1 then outputArgs.[1] <- (0 :> obj)
                        new ServiceResult(StatusCodes.Good)
                    | None -> new ServiceResult(StatusCodes.BadInvalidArgument)
                with ex ->
                    log.Error(sprintf "RaiseAssetEvent failed: asset=%s signal=%s" gaid.Value sourceSignalId, ex)
                    new ServiceResult(StatusCodes.BadInternalError)
                (* Legacy inline emitter retained only in source history; the shared
                   RaiseAssetEvent path above is used by methods and runtime bridges.
                let eventTypeSemId = string (inputArgs.[0])
                let sourceSignalId = string (inputArgs.[1])
                let sourceTs =
                    match inputArgs.[2] with
                    | :? DateTime as dt -> dt
                    | _ -> DateTime.UtcNow
                let payloadJson = string (inputArgs.[3])
                // ADR-003 §1a · payload 안 시각 필드 검증 (간이).
                let forbidden = [| "\"sourceTimestamp\""; "\"time\""; "\"timestamp\""; "\"receiveTime\"" |]
                let violated = forbidden |> Array.exists (fun k -> payloadJson.Contains k)
                if violated then
                    new ServiceResult(StatusCodes.BadArgumentsMissing)
                else
                    try
                        let evt = new BaseEventState(assetFolder)
                        evt.Initialize(
                            this.SystemContext,
                            assetFolder,
                            EventSeverity.Medium,
                            new LocalizedText(sprintf "AssetEvent · %s · %s · %s" idShort eventTypeSemId sourceSignalId))
                        // BaseEventState property nodes may be lazily created — set only after init.
                        let eventIdBytes = Guid.NewGuid().ToByteArray()
                        if not (isNull evt.EventId) then evt.EventId.Value <- eventIdBytes
                        if not (isNull evt.EventType) then evt.EventType.Value <- ObjectTypeIds.BaseEventType
                        if not (isNull evt.SourceNode) then evt.SourceNode.Value <- assetFolder.NodeId
                        if not (isNull evt.SourceName) then evt.SourceName.Value <- sourceSignalId
                        if not (isNull evt.Time) then evt.Time.Value <- sourceTs
                        if not (isNull evt.ReceiveTime) then evt.ReceiveTime.Value <- DateTime.UtcNow
                        if not (isNull evt.Message) then evt.Message.Value <- new LocalizedText(payloadJson)
                        eventsFolder.ReportEvent(this.SystemContext, evt)
                        if outputArgs.Count > 0 then outputArgs.[0] <- (eventIdBytes :> obj)
                        if outputArgs.Count > 1 then outputArgs.[1] <- (0 :> obj)
                        new ServiceResult(StatusCodes.Good)
                    with ex ->
                        Console.Error.WriteLine(sprintf "[RaiseAssetEvent] error: %s · %s" ex.Message (ex.GetType().FullName))
                        new ServiceResult(StatusCodes.BadInternalError) *) )
        eventsFolder.AddChild(raiseMethod)
        this.AttachPredefined raiseMethod
        this.AttachPredefined inArgs
        this.AttachPredefined outArgs
        eventsFolder, raiseMethod

    member private _.BuildSignalProperty(
                parent: BaseDataVariableState,
                signalId: SignalId,
                name: string,
                dataType: NodeId,
                value: obj,
                serverNs: int) =
        let property = new BaseDataVariableState(parent)
        property.SymbolicName <- name
        property.NodeId <- NodeId(sprintf "Metadata/%s/%s" signalId.Value name, uint16 serverNs)
        property.BrowseName <- QualifiedName(name, uint16 serverNs)
        property.DisplayName <- LocalizedText name
        property.Description <- LocalizedText(sprintf "Signal metadata %s for %s" name signalId.Value)
        property.ReferenceTypeId <- ReferenceTypeIds.HasProperty
        property.TypeDefinitionId <- VariableTypeIds.PropertyType
        property.DataType <- dataType
        property.ValueRank <- ValueRanks.Scalar
        property.AccessLevel <- byte AccessLevels.CurrentRead
        property.UserAccessLevel <- property.AccessLevel
        property.MinimumSamplingInterval <- 0.0
        property.Historizing <- false
        property.Value <- value
        property.StatusCode <- StatusCodes.Good
        property.Timestamp <- DateTime.UtcNow
        parent.AddChild(property)

    member private _.BuildPolicyProperty(
                parent: BaseDataVariableState,
                signalId: SignalId,
                name: string,
                dataType: NodeId,
                value: obj,
                serverNs: int) =
        let property = new BaseDataVariableState(parent)
        property.SymbolicName <- name
        property.NodeId <- NodeId(sprintf "Policy/%s/%s" signalId.Value name, uint16 serverNs)
        property.BrowseName <- QualifiedName(name, uint16 serverNs)
        property.DisplayName <- LocalizedText name
        property.Description <- LocalizedText(sprintf "CollectionPolicy.%s for %s" name signalId.Value)
        property.ReferenceTypeId <- ReferenceTypeIds.HasProperty
        property.TypeDefinitionId <- VariableTypeIds.PropertyType
        property.DataType <- dataType
        property.ValueRank <- ValueRanks.Scalar
        property.AccessLevel <- byte AccessLevels.CurrentRead
        property.UserAccessLevel <- property.AccessLevel
        property.MinimumSamplingInterval <- 0.0
        property.Historizing <- false
        property.Value <- value
        property.StatusCode <- StatusCodes.Good
        property.Timestamp <- DateTime.UtcNow
        parent.AddChild(property)

    member private this.AttachCollectionPolicy(
                variable: BaseDataVariableState,
                signalId: SignalId,
                policy: SignalPolicy,
                serverNs: int) =
        let add name dataType value =
            this.BuildPolicyProperty(variable, signalId, name, dataType, value, serverNs)
        let mode =
            match policy.AcquisitionMode with
            | AcquisitionMode.Sampled -> "sampled"
            | AcquisitionMode.ChangeOfValue -> "changeOfValue"
            | AcquisitionMode.EventDriven -> "eventDriven"
        add SignalPolicyUaMetadata.AcquisitionMode DataTypeIds.String (box mode)
        policy.SamplingIntervalMs
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.SamplingIntervalMs DataTypeIds.Int32 (box value))
        policy.PublishingIntervalMs
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.PublishingIntervalMs DataTypeIds.Int32 (box value))
        policy.DeadbandAbsolute
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.DeadbandAbsolute DataTypeIds.Double (box value))
        policy.DeadbandPercent
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.DeadbandPercent DataTypeIds.Double (box value))
        policy.EngineeringRangeLow
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.EngineeringRangeLow DataTypeIds.Double (box value))
        policy.EngineeringRangeHigh
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.EngineeringRangeHigh DataTypeIds.Double (box value))
        match policy.EngineeringRangeLow, policy.EngineeringRangeHigh with
        | Some low, Some high ->
            let range = Opc.Ua.Range()
            range.Low <- low
            range.High <- high
            let property = new PropertyState<Opc.Ua.Range>(variable)
            property.SymbolicName <- BrowseNames.EURange
            property.NodeId <- NodeId(sprintf "Metadata/%s/%s" signalId.Value BrowseNames.EURange, uint16 serverNs)
            property.BrowseName <- QualifiedName(BrowseNames.EURange, 0us)
            property.DisplayName <- LocalizedText BrowseNames.EURange
            property.Description <- LocalizedText(sprintf "Engineering range for %s" signalId.Value)
            property.ReferenceTypeId <- ReferenceTypeIds.HasProperty
            property.TypeDefinitionId <- VariableTypeIds.PropertyType
            property.DataType <- DataTypeIds.Range
            property.ValueRank <- ValueRanks.Scalar
            property.AccessLevel <- byte AccessLevels.CurrentRead
            property.UserAccessLevel <- property.AccessLevel
            property.Value <- range
            property.StatusCode <- StatusCodes.Good
            property.Timestamp <- DateTime.UtcNow
            variable.AddChild(property)
        | _ -> ()
        policy.QueueSize
        |> Option.iter (fun value -> add SignalPolicyUaMetadata.QueueSize DataTypeIds.Int32 (box value))
        add SignalPolicyUaMetadata.Retention DataTypeIds.String (box policy.Retention)

    /// signal 하나에 대응하는 BaseDataVariableState 생성. 초기 StatusCode = BadWaitingForInitialData
    /// (실 값은 WriteSignal 로 들어와야 Good 으로 승격) — 브릿지가 초기 스냅샷 push 시 해제.
    member private this.BuildVariable(
                parent: NodeState,
                sigId: SignalId,
                builtin: BuiltInType,
                displayName: string,
                unitName: string,
                policy: SignalPolicy option,
                serverNs: int)
            : BaseDataVariableState =
        let v = new BaseDataVariableState(parent)
        v.SymbolicName <- sigId.Value
        v.NodeId <- NodeId(sigId.Value, uint16 serverNs)
        v.BrowseName <- QualifiedName(sigId.Value, uint16 serverNs)
        v.DisplayName <- LocalizedText(if String.IsNullOrWhiteSpace displayName then sigId.Value else displayName)
        v.Description <- LocalizedText sigId.Value
        v.ReferenceTypeId <- ReferenceTypeIds.HasComponent
        v.TypeDefinitionId <- VariableTypeIds.BaseDataVariableType
        v.DataType <- DataTypes.ofBuiltIn builtin
        v.ValueRank <- ValueRanks.Scalar
        // Central UA는 aggregator다. 외부 UA client write는 허용하지 않고 내부 bridge만 WriteSignal을 사용한다.
        v.AccessLevel <- byte (AccessLevels.CurrentRead ||| AccessLevels.HistoryRead)
        v.UserAccessLevel <- v.AccessLevel
        v.MinimumSamplingInterval <-
            policy
            |> Option.bind (fun p -> p.SamplingIntervalMs)
            |> Option.defaultValue defaultSamplingIntervalMs
            |> max 1
            |> float
        v.Historizing <- false
        v.Value <- DataTypes.defaultValue builtin
        v.StatusCode <- StatusCodes.BadWaitingForInitialData
        v.Timestamp <- DateTime.UtcNow
        parent.AddChild(v)
        if not (String.IsNullOrWhiteSpace unitName) then
            this.BuildSignalProperty(v, sigId, SignalUaMetadata.Unit, DataTypeIds.String, box unitName, serverNs)
        policy |> Option.iter (fun p -> this.AttachCollectionPolicy(v, sigId, p, serverNs))
        v

    /// 폴더 계층을 포함한 자산 등록. 브라우저 트리에서 아이템별로 인지 가능하도록 배치.
    /// <paramref name="signalsWithPath"/> = (folderPath, signalId, unit, builtin, displayName) 튜플 목록.
    /// folderPath 는 asset 루트 기준 상대 경로 — 빈 리스트면 asset 폴더 바로 아래 배치.
    /// 동일 folderPath 는 재사용 (idempotent) — 여러 signal 이 같은 경로면 한 폴더 안에 모임.
    /// 폴더 NodeId 는 "Folder/<join(path,'/')>" 로 asset namespace 안에서 유일.
    member private this.AddAssetWithHierarchyCore(
                gaid: GlobalAssetId,
                idShort: string,
                signalsWithPath: (string list * SignalId * string * BuiltInType * string * SignalPolicy option) list) : int =
        let _ = allocator.EnsureForAsset gaid
        let uri = allocator.GlobalAssetIdToUri gaid

        // Namespace 등록 (Manager 소유권 + 서버 광고 목록 둘 다).
        let nsIndex = this.EnsureManagedNamespace uri
        let serverNs = int nsIndex

        Monitor.Enter this.Lock
        try
            let assetFolder = new FolderState(assetsFolder)
            assetFolder.SymbolicName <- idShort
            assetFolder.NodeId <- NodeId("Asset", uint16 serverNs)
            assetFolder.BrowseName <- QualifiedName(idShort, uint16 serverNs)
            assetFolder.DisplayName <- LocalizedText idShort
            assetFolder.EventNotifier <- EventNotifiers.SubscribeToEvents
            assetsFolder.AddChild(assetFolder)

            let eventsFolder, raiseMethod = this.BuildEventsSubtree(gaid, assetFolder, idShort, serverNs)

            let folderByPath = Dictionary<string, FolderState>(StringComparer.Ordinal)
            let rec ensureFolder (path: string list) : FolderState =
                match path with
                | [] -> assetFolder
                | _ ->
                    let key = String.Join("/", path)
                    match folderByPath.TryGetValue key with
                    | true, f -> f
                    | _ ->
                        let parentPath = path |> List.take (List.length path - 1)
                        let parent = ensureFolder parentPath
                        let leafDisplay = List.last path
                        let f = new FolderState(parent)
                        f.SymbolicName <- DsNodeManager.SanitizeSymbolic leafDisplay
                        f.NodeId <- NodeId(sprintf "Folder/%s" key, uint16 serverNs)
                        f.BrowseName <- QualifiedName(leafDisplay, uint16 serverNs)
                        f.DisplayName <- LocalizedText leafDisplay
                        f.EventNotifier <- EventNotifiers.None
                        f.ReferenceTypeId <- ReferenceTypeIds.Organizes
                        f.TypeDefinitionId <- ObjectTypeIds.FolderType
                        parent.AddChild(f)
                        folderByPath.[key] <- f
                        f

            let vars = new ConcurrentDictionary<string, BaseDataVariableState>()
            let variableTypes = new ConcurrentDictionary<string, BuiltInType>()
            for (path, sigId, unitName, builtin, displayName, policy) in signalsWithPath do
                let parent = ensureFolder path
                let v = this.BuildVariable(parent, sigId, builtin, displayName, unitName, policy, serverNs)
                vars.[sigId.Value] <- v
                variableTypes.[sigId.Value] <- builtin

            // hot-append 경로 — 전체 subtree 완성 후 root 한 번 등록해야
            // descendants + hierarchical refs 가 함께 반영된다.
            this.AttachPredefined assetFolder
            this.AddRootNotifier assetFolder

            let ctx = {
                GlobalAssetId = gaid
                NsIndex = serverNs
                AssetFolder = assetFolder
                Variables = vars
                VariableTypes = variableTypes
                EventsFolder = eventsFolder
                RaiseEventMethod = raiseMethod
            }
            assets.[gaid] <- ctx
            serverNs
        finally
            Monitor.Exit this.Lock

    /// CollectionPolicy가 포함된 AID 신호 등록 경로.
    member this.AddAssetWithHierarchyAndPolicies(
                gaid: GlobalAssetId,
                idShort: string,
                signalsWithPath: (string list * SignalId * string * BuiltInType * string * SignalPolicy option) list) : int =
        this.AddAssetWithHierarchyCore(gaid, idShort, signalsWithPath)

    /// 기존 공개 API 호환: 정책이 없는 신호 계층 등록.
    member this.AddAssetWithHierarchy(
                gaid: GlobalAssetId,
                idShort: string,
                signalsWithPath: (string list * SignalId * string * BuiltInType * string) list) : int =
        signalsWithPath
        |> List.map (fun (path, signalId, unitName, builtin, displayName) ->
            path, signalId, unitName, builtin, displayName, None)
        |> fun signals -> this.AddAssetWithHierarchyCore(gaid, idShort, signals)

    /// 기존 flat API — 폴더 없이 asset 아래 바로 signal 배치. 폴더 계층 없이 호출 사이트를 유지하려는 경우.
    member this.AddAssetWithDisplayNames(gaid: GlobalAssetId, idShort: string, signals: (SignalId * string * BuiltInType * string) list) : int =
        let withEmptyPath =
            signals
            |> List.map (fun (sigId, unitName, builtin, displayName) -> [], sigId, unitName, builtin, displayName)
        this.AddAssetWithHierarchy(gaid, idShort, withEmptyPath)

    /// 기존 공개 API 호환용. DisplayName 미지정 시 안정적인 SignalId를 그대로 표시한다.
    member this.AddAsset(gaid: GlobalAssetId, idShort: string, signals: (SignalId * string * BuiltInType) list) : int =
        let withDisplayNames =
            signals
            |> List.map (fun (signalId, unitName, dataType) ->
                signalId, unitName, dataType, signalId.Value)
        this.AddAssetWithDisplayNames(gaid, idShort, withDisplayNames)

    /// UaWriter 로부터 값 변경 시 호출. 실제 값은 Variable 선언 BuiltInType으로 중앙 변환한다.
    /// 변환 실패 시 이전 값은 유지하고 품질만 BadTypeMismatch로 전환한다.
    member this.WriteSignal(gaid: GlobalAssetId, signalId: SignalId, value: obj, sourceTs: DateTime, statusCode: uint32) : bool =
        match assets.TryGetValue gaid with
        | false, _ -> false
        | true, ctx ->
            match ctx.Variables.TryGetValue signalId.Value with
            | false, _ -> false
            | true, v ->
                let target =
                    match ctx.VariableTypes.TryGetValue signalId.Value with
                    | true, t -> t
                    | _ -> BuiltInType.Null
                match DataTypes.tryCoerce target value with
                | Error reason ->
                    let total = Interlocked.Increment(&typeMismatchCount)
                    if total = 1L || total % 100L = 0L then
                        let actualType = if isNull value then "<null>" else value.GetType().FullName
                        log.Warn(
                            sprintf "OPC UA type mismatch: asset=%s signal=%s expected=%O actual=%s reason=%s total=%d"
                                gaid.Value signalId.Value target actualType reason total)
                    Monitor.Enter this.Lock
                    try
                        v.Timestamp <- sourceTs
                        v.StatusCode <- StatusCodes.BadTypeMismatch
                        v.ClearChangeMasks(this.SystemContext, false)
                    finally
                        Monitor.Exit this.Lock
                    false
                | Ok typedValue ->
                    Monitor.Enter this.Lock
                    try
                        v.Value <- typedValue
                        v.Timestamp <- sourceTs
                        v.StatusCode <- StatusCode statusCode
                        v.ClearChangeMasks(this.SystemContext, false)
                    finally
                        Monitor.Exit this.Lock
                    true

    /// 값은 유지하고 품질/시각만 변경한다. 통신 단절·런타임 정지 전파용.
    member this.SetSignalQuality(gaid: GlobalAssetId, signalId: SignalId, statusCode: uint32, sourceTs: DateTime) : bool =
        match assets.TryGetValue gaid with
        | false, _ -> false
        | true, ctx ->
            match ctx.Variables.TryGetValue signalId.Value with
            | false, _ -> false
            | true, v ->
                Monitor.Enter this.Lock
                try
                    v.Timestamp <- sourceTs
                    v.StatusCode <- StatusCode statusCode
                    v.ClearChangeMasks(this.SystemContext, false)
                finally
                    Monitor.Exit this.Lock
                true

    member this.SetAssetQuality(gaid: GlobalAssetId, statusCode: uint32, sourceTs: DateTime) : int =
        match assets.TryGetValue gaid with
        | false, _ -> 0
        | true, ctx ->
            let mutable changed = 0
            for KeyValue(_, v) in ctx.Variables do
                Monitor.Enter this.Lock
                try
                    v.Timestamp <- sourceTs
                    v.StatusCode <- StatusCode statusCode
                    v.ClearChangeMasks(this.SystemContext, false)
                    changed <- changed + 1
                finally
                    Monitor.Exit this.Lock
            changed

    /// Raises an asset-scoped BaseEvent using a JSON wire message that preserves
    /// the event semantic id and payload for generic OPC UA subscribers.
    member this.RaiseAssetEvent(
            gaid: GlobalAssetId,
            eventTypeSemanticId: string,
            sourceSignalId: string,
            sourceTs: DateTime,
            payloadJson: string) : byte array option =
        match assets.TryGetValue gaid with
        | false, _ -> None
        | true, ctx ->
            match AssetEvents.tryCreateWireMessage eventTypeSemanticId sourceSignalId payloadJson with
            | Error reason ->
                log.Warn(sprintf "Asset event rejected: asset=%s signal=%s reason=%s" gaid.Value sourceSignalId reason)
                None
            | Ok wireMessage ->
                let evt = new BaseEventState(ctx.AssetFolder)
                evt.Initialize(
                    this.SystemContext,
                    ctx.AssetFolder,
                    EventSeverity.Medium,
                    LocalizedText wireMessage)
                let eventIdBytes = Guid.NewGuid().ToByteArray()
                if not (isNull evt.EventId) then evt.EventId.Value <- eventIdBytes
                if not (isNull evt.EventType) then evt.EventType.Value <- ObjectTypeIds.BaseEventType
                if not (isNull evt.SourceNode) then evt.SourceNode.Value <- ctx.AssetFolder.NodeId
                if not (isNull evt.SourceName) then evt.SourceName.Value <- sourceSignalId
                if not (isNull evt.Time) then evt.Time.Value <- sourceTs.ToUniversalTime()
                if not (isNull evt.ReceiveTime) then evt.ReceiveTime.Value <- DateTime.UtcNow
                if not (isNull evt.Message) then evt.Message.Value <- LocalizedText wireMessage
                ctx.EventsFolder.ReportEvent(this.SystemContext, evt)
                Some eventIdBytes

    member _.TypeMismatchCount = Interlocked.Read(&typeMismatchCount)

    member _.ListAssets() =
        assets.Values |> Seq.map (fun c -> c.GlobalAssetId, c.NsIndex) |> Seq.toList

    /// 통합 테스트/진단용: node manager의 predefined index 등록 여부.
    member this.ContainsNode(nodeId: NodeId) =
        not (isNull (this.FindPredefinedNode(nodeId, typeof<NodeState>)))
