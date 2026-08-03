namespace Ds2.OpcUa.Server.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Opc.Ua
open Opc.Ua.Configuration
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Core.Store
open Ds2.OpcUa.Server.NodeIds

/// LoadStore 트리 라벨 — 브라우저에서 아이템별 인지 가능하도록 폴더 이름 통일.
module private BrowseFolders =
    let Aid = "AID"
    let SystemKpi = "System KPI"
    let Transitions = "Transitions"
    let Works = "Works"
    let Calls = "Calls"
    let Io = "IO"

/// Agent(정식) 또는 Promaker WPF 데모 호스트가 프로젝트 수명주기에 맞춰 소유하는 인프로세스 OPC UA 서버.
type EmbeddedUaServer(
        root: string,
        endpointUrl: string,
        applicationName: string,
        applicationUri: string,
        allowAnonymous: bool,
        allowUnsecuredEndpoint: bool,
        autoAcceptUntrustedCertificates: bool,
        maxSessions: int,
        sessionTimeoutMs: int,
        minSamplingIntervalMs: int,
        defaultSamplingIntervalMs: int) =

    static let log = log4net.LogManager.GetLogger("EmbeddedUaServer")

    let runtimeIoNodes = ConcurrentDictionary<Guid, struct (GlobalAssetId * SignalId)>()
    let workStateNodes = ConcurrentDictionary<Guid, struct (GlobalAssetId * SignalId)>()
    let callStateNodes = ConcurrentDictionary<Guid, struct (GlobalAssetId * SignalId)>()
    let aidSignalNodes = ConcurrentDictionary<string, struct (GlobalAssetId * SignalId)>(StringComparer.Ordinal)
    let mutable aidSignalNodeCount = 0
    let mutable server : DsUaServer option = None
    /// WriteRuntimeIo 시 key 매치 실패 (silent drop) 누적 카운트. 진단용.
    let mutable writeMissCount = 0L
    let mutable stateWriteMissCount = 0L
    let mutable aidWriteMissCount = 0L
    let missLogGate = obj()
    let mutable lastMissLogUtc = DateTime.MinValue

    let warnMiss kind batch total known =
        lock missLogGate (fun () ->
            let now = DateTime.UtcNow
            if lastMissLogUtc = DateTime.MinValue || (now - lastMissLogUtc).TotalSeconds >= 30.0 then
                lastMissLogUtc <- now
                log.Warn($"OPC UA write miss: kind={kind} batch={batch} total={total} registered={known}"))

    let gaidOf (project: Project) (system: DsSystem) =
        match system.IRI with
        | Some iri when not (String.IsNullOrWhiteSpace iri) -> GlobalAssetId iri
        | _ -> GlobalAssetId(sprintf "urn:dualsoft:promaker:%s:%s" (project.Id.ToString("N")) (system.Id.ToString("N")))

    let aidGaidOf (project: Project) =
        GlobalAssetId(sprintf "urn:dualsoft:aas:%s" (project.Id.ToString("N")))

    let xsdToBuiltIn = function
        | XsDouble -> BuiltInType.Double
        | XsFloat -> BuiltInType.Float
        | XsInt -> BuiltInType.Int32
        | XsLong -> BuiltInType.Int64
        | XsUnsignedInt -> BuiltInType.UInt32
        | XsUnsignedLong -> BuiltInType.UInt64
        | XsBoolean -> BuiltInType.Boolean
        | XsDateTime -> BuiltInType.DateTime
        | XsByteString -> BuiltInType.ByteString
        | XsString -> BuiltInType.String

    let addUnique
        (seen: HashSet<string>)
        (signals: ResizeArray<string list * SignalId * string * BuiltInType * string>)
        (path: string list)
        (signalId: SignalId)
        (unitName: string)
        (dataType: BuiltInType)
        (displayName: string) =
        if seen.Add signalId.Value then
            signals.Add(path, signalId, unitName, dataType, displayName)

    /// AID InteractionMetadata를 중앙 UA 주소공간의 Variable 정의로 투영한다.
    /// endpoint/href는 southbound 연결 정보이고, 중앙 NodeId는 SignalId로 결정론적으로 생성한다.
    let appendAidSignals
        (aid: AssetInterfacesDescription)
        (policies: IReadOnlyDictionary<string, SignalPolicy>)
        (seen: HashSet<string>)
        (signals: ResizeArray<string list * SignalId * string * BuiltInType * string * SignalPolicy option>) =
        let add (path: string) (signalId: SignalId) (unitName: string) (dataType: BuiltInType) (displayName: string) =
            if seen.Add signalId.Value then
                let policy =
                    match policies.TryGetValue signalId.Value with
                    | true, value -> Some value
                    | _ -> None
                signals.Add([BrowseFolders.Aid; path], signalId, unitName, dataType, displayName, policy)
        for binding in aid.Interfaces do
            match binding with
            | OpcUa (_, interactions, events) ->
                for interaction in interactions do
                    add "OPC UA" interaction.SignalId (defaultArg interaction.Unit "")
                        (xsdToBuiltIn interaction.ValueType) interaction.IdShort
                for eventBinding in events do
                    add "OPC UA Events" eventBinding.SignalId "" BuiltInType.String eventBinding.IdShort
            | Modbus (_, interactions) ->
                for interaction in interactions do
                    add "Modbus" interaction.SignalId (defaultArg interaction.Unit "")
                        (xsdToBuiltIn interaction.ValueType) interaction.IdShort
            | Mqtt (_, interactions) ->
                for interaction in interactions do
                    add "MQTT" interaction.SignalId (defaultArg interaction.Unit "")
                        (xsdToBuiltIn interaction.ValueType) interaction.IdShort
            | Http (_, interactions) ->
                for interaction in interactions do
                    add "HTTP" interaction.SignalId (defaultArg interaction.Unit "")
                        (xsdToBuiltIn interaction.ValueType) interaction.IdShort
            | Xgt (_, interactions) ->
                for interaction in interactions do
                    add "XGT" interaction.SignalId (defaultArg interaction.Unit "")
                        (xsdToBuiltIn interaction.ValueType) interaction.IdShort

    let signalPoliciesForProject (store: DsStore) (project: Project) =
        let result = Dictionary<string, SignalPolicy>(StringComparer.Ordinal)
        for system in Queries.activeSystemsOf project.Id store do
            match system.GetLoggingProperties() with
            | Some logging ->
                for policy in logging.SignalPolicies do
                    match SignalPolicy.validate policy with
                    | Ok () ->
                        if result.ContainsKey policy.SignalId.Value then
                            log.Warn($"Duplicate CollectionPolicy ignored: signalId={policy.SignalId.Value} system={system.Name}")
                        else
                            result.[policy.SignalId.Value] <- policy
                    | Error message ->
                        log.Warn($"Invalid CollectionPolicy ignored: signalId={policy.SignalId.Value} reason={message}")
            | None -> ()
        result :> IReadOnlyDictionary<string, SignalPolicy>

    /// Arrow FQDN 이 KpiWalker 에서 `%s.%s->%s` (sys.src->tgt) 로 만들어짐.
    /// 시각화용으로 폴더 이름 `src → tgt` 로 뽑아냄.
    let arrowFolderLabel (sysName: string) (arrowFqdn: string) =
        let prefix = sysName + "."
        let body =
            if arrowFqdn.StartsWith(prefix, StringComparison.Ordinal) then
                arrowFqdn.Substring(prefix.Length)
            else arrowFqdn
        let sep = "->"
        let idx = body.IndexOf(sep, StringComparison.Ordinal)
        if idx < 0 then body
        else
            let src = body.Substring(0, idx)
            let tgt = body.Substring(idx + sep.Length)
            sprintf "%s → %s" src tgt

    /// KPI 대상을 이 system 스코프로 필터링해 (System / Arrow / UserTag-by-fqdn) 로 버킷.
    /// UserTag 는 apiCall 매칭 시 fqdn key 로 lookup 하므로 Dictionary 로 둠.
    let bucketSystemKpis (systemName: string) (kpiTargets: KpiTarget list) =
        let sys = ResizeArray<KpiTarget>()
        let arr = ResizeArray<KpiTarget>()
        let tag = Dictionary<string, KpiTarget>(StringComparer.Ordinal)
        let prefix = systemName + "."
        for target in kpiTargets do
            let belongsHere =
                target.EntityFqdn = systemName
                || target.EntityFqdn.StartsWith(prefix, StringComparison.Ordinal)
            if belongsHere then
                match target.Kind with
                | SystemKind -> sys.Add target
                | ArrowWorkKind -> arr.Add target
                | UserTagKind -> tag.[target.EntityFqdn] <- target
                | _ -> ()
        sys, arr, tag

    let startCore (managedAssets: GlobalAssetId array) : Task = task {
        if server.IsNone then
            Directory.CreateDirectory root |> ignore
            let allocator = NamespaceAllocator(Path.Combine(root, "nodeset-state.json")) :> INamespaceAllocator
            let managedNamespaces =
                managedAssets
                |> Array.map allocator.GlobalAssetIdToUri
                |> Array.distinct
            let cfg =
                { ServerConfiguration.defaultConfig root with
                    ApplicationName = applicationName
                    ApplicationUri = applicationUri
                    EndpointUrl = endpointUrl
                    AllowAnonymous = allowAnonymous
                    AllowUnsecuredEndpoint = allowUnsecuredEndpoint
                    AutoAcceptUntrustedCertificates = autoAcceptUntrustedCertificates
                    MaxSessionCount = max 1 maxSessions
                    SessionTimeoutMs = max 10_000 sessionTimeoutMs
                    MinSamplingIntervalMs = max 1 minSamplingIntervalMs }
            let appConfig = ServerConfiguration.build cfg
            let! certificateOk = ServerConfiguration.validateAndPrepare appConfig
            if not certificateOk then
                invalidOp "OPC UA application certificate could not be created or validated."
            let instance = ApplicationInstance(ApplicationConfiguration = appConfig)
            let uaServer = new DsUaServer(allocator, managedNamespaces, max minSamplingIntervalMs defaultSamplingIntervalMs)
            do! instance.Start uaServer
            server <- Some uaServer
    }

    /// v1 생성자 호환: 기본 샘플링 주기를 지정하지 않은 호출자는 1000ms를 사용한다.
    new(
        root: string,
        endpointUrl: string,
        applicationName: string,
        applicationUri: string,
        allowAnonymous: bool,
        maxSessions: int,
        sessionTimeoutMs: int,
        minSamplingIntervalMs: int,
        defaultSamplingIntervalMs: int) =
        new EmbeddedUaServer(
            root,
            endpointUrl,
            applicationName,
            applicationUri,
            allowAnonymous,
            true,
            true,
            maxSessions,
            sessionTimeoutMs,
            minSamplingIntervalMs,
            defaultSamplingIntervalMs)

    new(
        root: string,
        endpointUrl: string,
        applicationName: string,
        applicationUri: string,
        allowAnonymous: bool,
        maxSessions: int,
        sessionTimeoutMs: int,
        minSamplingIntervalMs: int) =
        new EmbeddedUaServer(
            root,
            endpointUrl,
            applicationName,
            applicationUri,
            allowAnonymous,
            true,
            true,
            maxSessions,
            sessionTimeoutMs,
            minSamplingIntervalMs,
            max minSamplingIntervalMs 1000)

    member _.EndpointUrl = endpointUrl
    member _.IsRunning = server.IsSome

    member _.StartAsync() : Task = startCore [||]

    /// 모델의 자산 namespace를 MasterNodeManager 생성 전에 등록한 뒤 노드를 적재한다.
    member this.StartForStoreAsync(store: DsStore, exposeKpi: bool, exposeLiveTags: bool, exposeSimulationData: bool) : Task<int> = task {
        let managedAssets =
            match Queries.allProjects store |> List.tryHead with
            | None -> [||]
            | Some project ->
                let systemAssets =
                    Queries.activeSystemsOf project.Id store
                    |> List.map (gaidOf project)
                let aidAssets =
                    match project.AssetInterfaces with
                    | Some aid when aid.Interfaces.Count > 0 -> [aidGaidOf project]
                    | _ -> []
                List.append systemAssets aidAssets |> List.toArray
        do! startCore managedAssets
        return this.LoadStore(store, exposeKpi, exposeLiveTags, exposeSimulationData)
    }

    /// Active System을 OPC UA Asset으로, KPI/Runtime 값을 하위 Variable로 투영한다.
    /// 브라우저 트리에서 아이템별로 인지 가능하도록 폴더 계층 배치:
    ///   Asset/
    ///     System KPI/              (System 단위 지표 · OEE/MTBF/…)
    ///     Transitions/<src → tgt>/ (Arrow KPIs · AvgLatencyMs/TransitionCount)
    ///     Works/<flow>/<work>/     (Work state)
    ///     Calls/<flow>/<work>/<call>/  (Call state)
    ///     IO/<flow>/<work>/<call>/<api>/  (Runtime IO 값 + UserTag KPIs)
    member _.LoadStore(store: DsStore, exposeKpi: bool, exposeLiveTags: bool, exposeSimulationData: bool) =
        let uaServer = server |> Option.defaultWith (fun () -> invalidOp "OPC UA server is not running.")
        runtimeIoNodes.Clear()
        workStateNodes.Clear()
        callStateNodes.Clear()
        aidSignalNodes.Clear()
        aidSignalNodeCount <- 0
        match Queries.allProjects store |> List.tryHead with
        | None -> 0
        | Some project ->
            let kpiTargets = if exposeKpi then KpiWalker.walk store project else []
            let mutable assetCount = 0

            // AASX의 AID가 바뀌면 Agent watcher가 서버를 재기동하고 이 투영을 다시 수행한다.
            // 표준 4종 binding 모두 동일한 결정론적 SignalId NodeId 계약을 사용한다.
            match project.AssetInterfaces with
            | Some aid when aid.Interfaces.Count > 0 ->
                let signals = ResizeArray<string list * SignalId * string * BuiltInType * string * SignalPolicy option>()
                let seen = HashSet<string>(StringComparer.Ordinal)
                let policies = signalPoliciesForProject store project
                appendAidSignals aid policies seen signals
                if signals.Count > 0 then
                    let aidGaid = aidGaidOf project
                    uaServer.NodeManager.AddAssetWithHierarchyAndPolicies(
                        aidGaid,
                        project.Name + " AID",
                        List.ofSeq signals) |> ignore
                    for (_, signalId, _, _, _, _) in signals do
                        aidSignalNodes.[signalId.Value] <- struct (aidGaid, signalId)
                    aidSignalNodeCount <- signals.Count
                    assetCount <- assetCount + 1
            | _ -> ()

            for system in Queries.activeSystemsOf project.Id store do
                let gaid = gaidOf project system
                let signals = ResizeArray<string list * SignalId * string * BuiltInType * string>()
                let seen = HashSet<string>(StringComparer.Ordinal)
                let systemKpis, arrowKpis, userTagKpis = bucketSystemKpis system.Name kpiTargets

                // 1) System KPI
                for target in systemKpis do
                    addUnique seen signals [BrowseFolders.SystemKpi]
                        target.SignalId target.Metric.Unit
                        (xsdToBuiltIn target.Metric.DataType) target.Metric.IdShortSuffix

                // 2) Transitions (Arrow KPI) — 폴더 이름 "src → tgt".
                for target in arrowKpis do
                    let label = arrowFolderLabel system.Name target.EntityFqdn
                    addUnique seen signals [BrowseFolders.Transitions; label]
                        target.SignalId target.Metric.Unit
                        (xsdToBuiltIn target.Metric.DataType) target.Metric.IdShortSuffix

                // 3) Works / Calls / IO — 각 엔티티 계층 순회.
                for flow in Queries.flowsOf system.Id store do
                    for work in Queries.worksOf flow.Id store do
                        if exposeSimulationData then
                            let workSignal = SignalId(sprintf "runtime.work.%s.state" (work.Id.ToString("N")))
                            addUnique seen signals [BrowseFolders.Works; flow.Name; work.Name] workSignal ""
                                BuiltInType.String "State"
                            workStateNodes.[work.Id] <- struct (gaid, workSignal)
                        for call in Queries.callsOf work.Id store do
                            if exposeSimulationData then
                                let callSignal = SignalId(sprintf "runtime.call.%s.state" (call.Id.ToString("N")))
                                addUnique seen signals [BrowseFolders.Calls; flow.Name; work.Name; call.Name] callSignal ""
                                    BuiltInType.String "State"
                                callStateNodes.[call.Id] <- struct (gaid, callSignal)

                            if exposeLiveTags then
                                for ac in call.ApiCalls do
                                    let apiLabel =
                                        if String.IsNullOrEmpty ac.Name then "Api" else ac.Name
                                    let ioFolder = [BrowseFolders.Io; flow.Name; work.Name; call.Name; apiLabel]

                                    // Runtime IO value channel — Sim engine 이 ApiCall.Id 로 키잉.
                                    let ioSignal = SignalId(sprintf "runtime.io.%s" (ac.Id.ToString("N")))
                                    addUnique seen signals ioFolder ioSignal ""
                                        BuiltInType.String "Value"
                                    runtimeIoNodes.[ac.Id] <- struct (gaid, ioSignal)

                                    // UserTag KPI (InTag / OutTag) — display 는 실제 태그 이름.
                                    let addTagKpi (label: string) =
                                        let fqdn = sprintf "%s.%s.%s.%s.%s" system.Name flow.Name work.Name call.Name label
                                        match userTagKpis.TryGetValue fqdn with
                                        | true, t ->
                                            addUnique seen signals ioFolder t.SignalId t.Metric.Unit
                                                (xsdToBuiltIn t.Metric.DataType) t.Metric.IdShortSuffix
                                        | _ -> ()
                                    addTagKpi "InTag"
                                    addTagKpi "OutTag"

                uaServer.NodeManager.AddAssetWithHierarchy(gaid, system.Name, List.ofSeq signals) |> ignore
                assetCount <- assetCount + 1
            assetCount

    /// WriteRuntimeIo 총 누적 miss (key 매치 실패). 0 이 아니면 sim/UA 키 스킴 mismatch 신호.
    member _.RuntimeIoMissCount = writeMissCount
    /// 등록된 runtime IO 노드 수 (진단용).
    member _.RuntimeIoNodeCount = runtimeIoNodes.Count
    /// 현재 AASX AID에서 자동 생성된 Variable 수.
    member _.AidSignalNodeCount = aidSignalNodeCount
    member _.AidWriteMissCount = aidWriteMissCount
    member _.StateWriteMissCount = stateWriteMissCount
    member _.TypeMismatchCount =
        match server with
        | Some uaServer -> uaServer.NodeManager.TypeMismatchCount
        | None -> 0L

    /// AID의 canonical signalId를 기준으로 southbound 관측값을 UA Variable에 반영한다.
    member _.WriteAidSignal(signalId: string, value: obj, sourceTs: DateTime, statusCode: uint32) =
        match server with
        | None -> false
        | Some uaServer ->
            match aidSignalNodes.TryGetValue signalId with
            | true, struct (gaid, sid) ->
                uaServer.NodeManager.WriteSignal(gaid, sid, value, sourceTs, statusCode)
            | _ ->
                let total = Threading.Interlocked.Increment(&aidWriteMissCount)
                warnMiss "aid" 1 total aidSignalNodes.Count
                false

    /// 지정한 AID signal들의 값은 유지하고 품질만 변경한다.
    member _.SetAidSignalQuality(signalIds: IEnumerable<string>, statusCode: uint32, sourceTs: DateTime) =
        match server with
        | None -> 0
        | Some uaServer ->
            let mutable changed = 0
            for signalId in signalIds do
                match aidSignalNodes.TryGetValue signalId with
                | true, struct (gaid, sid) ->
                    if uaServer.NodeManager.SetSignalQuality(gaid, sid, statusCode, sourceTs) then
                        changed <- changed + 1
                | _ -> ()
            changed

    member _.SetAllAidSignalQuality(statusCode: uint32, sourceTs: DateTime) =
        match server with
        | None -> 0
        | Some uaServer ->
            let mutable changed = 0
            for KeyValue(_, struct (gaid, sid)) in aidSignalNodes do
                if uaServer.NodeManager.SetSignalQuality(gaid, sid, statusCode, sourceTs) then
                    changed <- changed + 1
            changed

    /// Work/Call/runtime IO 노드의 값을 유지하고 품질만 변경한다.
    member _.SetRuntimeQuality(statusCode: uint32, sourceTs: DateTime) =
        match server with
        | None -> 0
        | Some uaServer ->
            let mutable changed = 0
            let setQuality (nodes: ConcurrentDictionary<Guid, struct (GlobalAssetId * SignalId)>) =
                for KeyValue(_, struct (gaid, sid)) in nodes do
                    if uaServer.NodeManager.SetSignalQuality(gaid, sid, statusCode, sourceTs) then
                        changed <- changed + 1
            setQuality runtimeIoNodes
            setQuality workStateNodes
            setQuality callStateNodes
            changed

    /// Work 상태 push. 실행 중 아니면 false. 등록되지 않은 workGuid 는 진단 카운터/경고에 기록한다.
    /// engine 의 WorkStateChanged 를 그대로 전달하는 용도 — 초기 상태(BadWaitingForInitialData) 해제.
    member _.WriteWorkState(workGuid: Guid, state: string, sourceTs: DateTime) =
        match server with
        | None -> false
        | Some uaServer ->
            match workStateNodes.TryGetValue workGuid with
            | true, struct (gaid, sid) ->
                uaServer.NodeManager.WriteSignal(gaid, sid, box state, sourceTs, uint32 StatusCodes.Good)
            | _ ->
                let total = Threading.Interlocked.Increment(&stateWriteMissCount)
                warnMiss "work-state" 1 total workStateNodes.Count
                false

    /// Call 상태 push. 조건 동일.
    member _.WriteCallState(callGuid: Guid, state: string, sourceTs: DateTime) =
        match server with
        | None -> false
        | Some uaServer ->
            match callStateNodes.TryGetValue callGuid with
            | true, struct (gaid, sid) ->
                uaServer.NodeManager.WriteSignal(gaid, sid, box state, sourceTs, uint32 StatusCodes.Good)
            | _ ->
                let total = Threading.Interlocked.Increment(&stateWriteMissCount)
                warnMiss "call-state" 1 total callStateNodes.Count
                false

    /// 등록된 Work state 노드의 (workGuid) 열거. 초기 스냅샷 push 용도.
    member _.WorkStateGuids = workStateNodes.Keys :> seq<Guid>
    /// 등록된 Call state 노드의 (callGuid) 열거. 초기 스냅샷 push 용도.
    member _.CallStateGuids = callStateNodes.Keys :> seq<Guid>

    /// Runtime IO snapshot을 UA Variable 값/SourceTimestamp로 반영한다.
    member _.WriteRuntimeIo(values: IReadOnlyDictionary<Guid, string>) =
        match server with
        | None -> 0
        | Some uaServer ->
            let mutable written = 0
            let mutable missed = 0
            for KeyValue(apiCallId, value) in values do
                match runtimeIoNodes.TryGetValue apiCallId with
                | true, struct (gaid, signalId) ->
                    if uaServer.NodeManager.WriteSignal(gaid, signalId, box value, DateTime.UtcNow, uint32 StatusCodes.Good) then
                        written <- written + 1
                | _ -> missed <- missed + 1
            if missed > 0 then
                let total = Threading.Interlocked.Add(&writeMissCount, int64 missed)
                warnMiss "runtime-io" missed total runtimeIoNodes.Count
            written

    member _.StopAsync() : Task = task {
        match server with
        | Some uaServer ->
            uaServer.Stop()
            server <- None
            runtimeIoNodes.Clear()
            workStateNodes.Clear()
            callStateNodes.Clear()
            aidSignalNodes.Clear()
            aidSignalNodeCount <- 0
        | None -> ()
    }

    interface IDisposable with
        member this.Dispose() = this.StopAsync().GetAwaiter().GetResult()
