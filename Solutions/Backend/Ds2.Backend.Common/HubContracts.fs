namespace Ds2.Backend.Common

open System
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module HubMethod =
    [<Literal>]
    let WriteTag = "WriteTag"
    [<Literal>]
    let OnTagChanged = "OnTagChanged"
    /// Batch 변형 — 여러 tag 변경을 1개 SignalR 프레임으로 송수신.
    [<Literal>]
    let WriteTags = "WriteTags"
    [<Literal>]
    let OnTagsChanged = "OnTagsChanged"
    [<Literal>]
    let SubscribeTag = "SubscribeTag"
    [<Literal>]
    let UnsubscribeTag = "UnsubscribeTag"
    /// Control 시작 시 현재 Tag 값 조회 (Hub 캐시에서 반환)
    [<Literal>]
    let QueryTag = "QueryTag"
    /// PLC 어댑터 1개의 연결 상태 변화 (connect/disconnect/setting 오류) — Server → Client.
    /// 신규 접속 클라이언트는 OnConnectedAsync 단계에서 현재 모든 PLC 상태를 캐스트로 받는다.
    [<Literal>]
    let OnPlcConnectionStatus = "OnPlcConnectionStatus"
    /// v12 — Control/Monitoring 경로이탈 이상감지. Server → Client.
    [<Literal>]
    let OnAbnormal = "OnAbnormal"
    /// Runtime remote command — session start.
    [<Literal>]
    let RuntimeStart = "RuntimeStart"
    [<Literal>]
    let RuntimePause = "RuntimePause"
    [<Literal>]
    let RuntimeResume = "RuntimeResume"
    [<Literal>]
    let RuntimeStop = "RuntimeStop"
    [<Literal>]
    let RuntimeReset = "RuntimeReset"
    [<Literal>]
    let RuntimeApplyInitialStates = "RuntimeApplyInitialStates"
    [<Literal>]
    let RuntimeStep = "RuntimeStep"
    [<Literal>]
    let RuntimeCanAdvanceStep = "RuntimeCanAdvanceStep"
    [<Literal>]
    let RuntimeStepWithSourcePriming = "RuntimeStepWithSourcePriming"
    [<Literal>]
    let RuntimeBeginStepBatch = "RuntimeBeginStepBatch"
    [<Literal>]
    let RuntimeIsStepBatchActive = "RuntimeIsStepBatchActive"
    [<Literal>]
    let RuntimeEndStep = "RuntimeEndStep"
    [<Literal>]
    let RuntimeAdvanceSimulationTo = "RuntimeAdvanceSimulationTo"
    [<Literal>]
    let RuntimeForceWorkState = "RuntimeForceWorkState"
    [<Literal>]
    let RuntimeForceCallState = "RuntimeForceCallState"
    [<Literal>]
    let RuntimeTryForceWorkStateIfGoing = "RuntimeTryForceWorkStateIfGoing"
    [<Literal>]
    let RuntimeTryForceWorkStateIfReady = "RuntimeTryForceWorkStateIfReady"
    [<Literal>]
    let RuntimeGetWorkState = "RuntimeGetWorkState"
    [<Literal>]
    let RuntimeGetCallState = "RuntimeGetCallState"
    [<Literal>]
    let RuntimeGetFlowState = "RuntimeGetFlowState"
    [<Literal>]
    let RuntimeSeedToken = "RuntimeSeedToken"
    [<Literal>]
    let RuntimeStartSourceWork = "RuntimeStartSourceWork"
    [<Literal>]
    let RuntimeDiscardToken = "RuntimeDiscardToken"
    [<Literal>]
    let RuntimeInjectIOValue = "RuntimeInjectIOValue"
    [<Literal>]
    let RuntimeInjectIOValueByAddress = "RuntimeInjectIOValueByAddress"
    [<Literal>]
    let RuntimeSetAllFlowStates = "RuntimeSetAllFlowStates"
    [<Literal>]
    let RuntimeReloadConnections = "RuntimeReloadConnections"
    [<Literal>]
    let RuntimeReloadDurations = "RuntimeReloadDurations"
    [<Literal>]
    let RuntimeStartWithHomingPhase = "RuntimeStartWithHomingPhase"
    [<Literal>]
    let RuntimeGetWorkToken = "RuntimeGetWorkToken"
    [<Literal>]
    let RuntimeGetTokenOrigin = "RuntimeGetTokenOrigin"
    /// Runtime remote query — DTO snapshots only. Runtime object graphs are not serialized.
    [<Literal>]
    let RuntimeGetSnapshot = "RuntimeGetSnapshot"
    [<Literal>]
    let RuntimeGetIndexProjection = "RuntimeGetIndexProjection"
    [<Literal>]
    let RuntimeGetIOMapProjection = "RuntimeGetIOMapProjection"
    /// Runtime server → client DTO pushes.
    [<Literal>]
    let OnRuntimeSnapshot = "OnRuntimeSnapshot"
    [<Literal>]
    let OnRuntimeWorkStateChanged = "OnRuntimeWorkStateChanged"
    [<Literal>]
    let OnRuntimeCallStateChanged = "OnRuntimeCallStateChanged"
    [<Literal>]
    let OnRuntimeStatusChanged = "OnRuntimeStatusChanged"
    [<Literal>]
    let OnRuntimeTokenEvent = "OnRuntimeTokenEvent"
    [<Literal>]
    let OnRuntimeCallTimeout = "OnRuntimeCallTimeout"
    [<Literal>]
    let OnRuntimeHomingPhaseCompleted = "OnRuntimeHomingPhaseCompleted"
    [<Literal>]
    let OnRuntimeCommandRejected = "OnRuntimeCommandRejected"

[<RequireQualifiedAccess>]
module HubSource =
    [<Literal>]
    let Control = "control"
    [<Literal>]
    let VirtualPlant = "virtualplant"
    [<Literal>]
    let Monitoring = "monitoring"
    [<Literal>]
    let Plc = "plc"
    [<Literal>]
    let Web = "web"

    /// <summary>spec §SignalR — 알려진 source 전체 집합. literal 5개. 외부 source 는 *unknown* 으로 분류 (분류 외 차단 또는 별도 처리).
    /// 새 source 추가 시 본 set 갱신 → DSPilot 의 _acceptedSources default 검토 → 통합 테스트.</summary>
    let WellKnownSources : System.Collections.Generic.IReadOnlySet<string> =
        let s = System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        s.Add(Control)        |> ignore
        s.Add(VirtualPlant)   |> ignore
        s.Add(Monitoring)     |> ignore
        s.Add(Plc)            |> ignore
        s.Add(Web)            |> ignore
        s :> System.Collections.Generic.IReadOnlySet<string>

    /// <summary>임의 source 가 WellKnown 인지. unknown 은 통계/로그용으로 분리 처리하는 호출자 측 helper.</summary>
    let isWellKnown (source: string) : bool =
        not (isNull source) && WellKnownSources.Contains(source)

    /// <summary>DSPilot consumer 기본 accept policy — Promaker host 가 broadcast 하는
    /// 실 IO source (Control / VirtualPlant / Plc). Monitoring 은 자기 자신 echo 이므로 차단.
    /// Web 은 외부 UI 직접 주입이라 검수 대상 — default 에선 차단.</summary>
    let DefaultAcceptedSources : string array =
        [| Control; VirtualPlant; Plc |]

/// Batch payload for WriteTags / OnTagsChanged.
/// [<CLIMutable>] 필수 — SignalR JsonHubProtocol(System.Text.Json, camelCase)이
/// F# record를 ctor 기반으로 deserialize 할 때 ctor parameter 이름 매칭이
/// 환경에 따라 깨져 모든 field가 null 인 record 가 만들어지는 사례 차단.
[<CLIMutable>]
type TagWrite = {
    Address: string
    Value: string
    Source: string
}

/// PLC 어댑터 1개의 연결 상태 스냅샷. SignalR JSON 직렬화로 양방향 전달되는 contract.
/// 같은 이유로 [<CLIMutable>] 필요 — DSPilot 측 System.Text.Json 역직렬화 호환.
/// LastError: 마지막 connect 실패 사유 (없으면 "" — IsConnected=true 인 정상 케이스).
/// FailedAttempts: 마지막 성공 이후 연속 실패 횟수. 0 이면 정상.
/// AtUtc: 본 상태가 관측된 시각 (UTC) — UI 가 "n초 전 끊김" 같은 표시에 사용 가능.
[<CLIMutable>]
type PlcConnectionStatus = {
    Name: string
    Vendor: string
    IpAddress: string
    Port: int
    IsConnected: bool
    LastError: string
    FailedAttempts: int
    AtUtc: System.DateTime
}

/// v12 abnormal 이벤트 payload — SignalR JSON(camelCase) 직렬화 contract.
/// 같은 이유로 [<CLIMutable>] 필요. Kind 이름 + KindValue(0~3) 병기.
/// Target 은 flat (Guid → string, 없으면 ""). Kind 로 어느 필드가 유효한지 판단:
///   Action* → ElapsedMs 유효(Sensor* 는 -1), Sensor* → Observed 유효(Action* 는 false).
[<CLIMutable>]
type AbnormalPayload = {
    Kind: string          // "SensorOpen" | "SensorShort" | "ActionOver" | "ActionUnder"
    KindValue: int        // 0~3
    CallId: string
    ApiCallId: string
    WorkId: string
    ElapsedMs: int        // Action* 유효, Sensor* 는 -1
    Observed: bool        // Sensor* 유효
    Mode: string          // "control" | "monitoring"
    Source: string
    TimestampUtc: System.DateTime
}

/// Runtime session identity carried by every remote command/event/snapshot.
/// Generation prevents stale commands and stale pushes after model reload or host restart.
[<CLIMutable>]
type RuntimeSessionIdentity = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
}

/// AASX 모델 식별 해시 — Agent(server engine owner)·client proxy 가 동일하게 계산해야
/// stale guard 의 ModelHash 비교가 통과한다. 같은 파일 → 같은 SHA256 → 동일 ModelHash.
module RuntimeModelHash =
    /// AASX 파일 내용 SHA256(hex). 파일 읽기 실패 시 빈 문자열.
    let compute (aasxPath: string) : string =
        try
            use sha = System.Security.Cryptography.SHA256.Create()
            use fs = System.IO.File.OpenRead aasxPath
            System.Convert.ToHexString(sha.ComputeHash fs)
        with _ -> ""

/// Common runtime command envelope. Command-specific hub methods may add extra arguments,
/// but this identity must be present so both sides can reject stale messages.
[<CLIMutable>]
type RuntimeCommandEnvelope = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    CommandId: string
}

[<RequireQualifiedAccess>]
module RuntimeCommandRejectReason =
    [<Literal>]
    let MissingSession = "missing-session"
    [<Literal>]
    let SessionMismatch = "session-mismatch"
    [<Literal>]
    let ModelHashMismatch = "model-hash-mismatch"
    [<Literal>]
    let GenerationMismatch = "generation-mismatch"
    [<Literal>]
    let ModeMismatch = "mode-mismatch"

/// Server/client-side stale command guard. Runtime host implementations should call this
/// before mutating runtime state.
module RuntimeSessionContract =
    let tryRejectCommand (current: RuntimeSessionIdentity) (command: RuntimeCommandEnvelope) : string option =
        if isNull (box current) || String.IsNullOrWhiteSpace current.SessionId then
            Some RuntimeCommandRejectReason.MissingSession
        elif isNull (box command) || not (String.Equals(command.SessionId, current.SessionId, StringComparison.Ordinal)) then
            Some RuntimeCommandRejectReason.SessionMismatch
        elif not (String.Equals(command.ModelHash, current.ModelHash, StringComparison.Ordinal)) then
            Some RuntimeCommandRejectReason.ModelHashMismatch
        elif command.Generation <> current.Generation then
            Some RuntimeCommandRejectReason.GenerationMismatch
        elif not (String.Equals(command.Mode, current.Mode, StringComparison.OrdinalIgnoreCase)) then
            Some RuntimeCommandRejectReason.ModeMismatch
        else
            None

    let isCurrentCommand current command =
        tryRejectCommand current command |> Option.isNone

[<CLIMutable>]
type RuntimeEmptyCommand = {
    Envelope: RuntimeCommandEnvelope
}

[<CLIMutable>]
type RuntimeStepPolicyCommand = {
    Envelope: RuntimeCommandEnvelope
    SelectedSourceWorkId: string
    AutoStartSources: bool
}

[<CLIMutable>]
type RuntimeStepBatchCommand = {
    Envelope: RuntimeCommandEnvelope
    BatchIds: string array
}

[<CLIMutable>]
type RuntimeAdvanceSimulationCommand = {
    Envelope: RuntimeCommandEnvelope
    TargetTimeMs: int64
}

[<CLIMutable>]
type RuntimeWorkStateCommand = {
    Envelope: RuntimeCommandEnvelope
    WorkId: string
    StatusName: string
    StatusValue: int
}

[<CLIMutable>]
type RuntimeCallStateCommand = {
    Envelope: RuntimeCommandEnvelope
    CallId: string
    StatusName: string
    StatusValue: int
}

[<CLIMutable>]
type RuntimeWorkCommand = {
    Envelope: RuntimeCommandEnvelope
    WorkId: string
}

[<CLIMutable>]
type RuntimeCallCommand = {
    Envelope: RuntimeCommandEnvelope
    CallId: string
}

[<CLIMutable>]
type RuntimeFlowTagCommand = {
    Envelope: RuntimeCommandEnvelope
    FlowTagName: string
    FlowTagValue: int
}

[<CLIMutable>]
type RuntimeIOValueCommand = {
    Envelope: RuntimeCommandEnvelope
    ApiCallId: string
    Value: string
}

[<CLIMutable>]
type RuntimeIOAddressCommand = {
    Envelope: RuntimeCommandEnvelope
    Address: string
    Value: string
}

[<CLIMutable>]
type RuntimeTokenCommand = {
    Envelope: RuntimeCommandEnvelope
    WorkId: string
    TokenValue: int
}

[<CLIMutable>]
type RuntimeCommandRejectedPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    CommandId: string
    Reason: string
    TimestampUtc: DateTime
}

[<CLIMutable>]
type RuntimeGuidStatus = {
    Id: string
    StatusName: string
    StatusValue: int
}

[<CLIMutable>]
type RuntimeGuidFlowTag = {
    Id: string
    FlowTagName: string
    FlowTagValue: int
}

[<CLIMutable>]
type RuntimeGuidString = {
    Id: string
    Value: string
}

[<CLIMutable>]
type RuntimeGuidName = {
    Id: string
    Name: string
}

[<CLIMutable>]
type RuntimeGuidRef = {
    Id: string
    RefId: string
}

[<CLIMutable>]
type RuntimeGuidList = {
    Id: string
    Values: string array
}

/// Flat runtime state snapshot for SignalR push-cache.
/// This intentionally avoids SimState/SimIndex/SignalIOMap serialization.
[<CLIMutable>]
type RuntimeStateSnapshot = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    StatusName: string
    StatusValue: int
    ClockMs: int64
    CurrentTimeMs: int64
    NextEventTimeMs: Nullable<int64>
    WorkStates: RuntimeGuidStatus array
    CallStates: RuntimeGuidStatus array
    FlowStates: RuntimeGuidFlowTag array
    IOValues: RuntimeGuidString array
    HasStartableWork: bool
    HasActiveDuration: bool
    IsHomingPhase: bool
    TimestampUtc: DateTime
}

/// UI-facing projection of SimIndex. The full SimIndex contains store and function-rich maps
/// and must not be used as a SignalR wire contract.
[<CLIMutable>]
type RuntimeIndexProjection = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    WorkNames: RuntimeGuidName array
    WorkSystemNames: RuntimeGuidName array
    WorkFlowGuids: RuntimeGuidRef array
    CallWorkGuids: RuntimeGuidRef array
    WorkCallGuids: RuntimeGuidList array
    TokenSourceGuids: string array
    TokenSinkGuids: string array
}

[<CLIMutable>]
type RuntimeIOMapMappingProjection = {
    ApiCallId: string
    CallId: string
    TxWorkId: string
    RxWorkId: string
    OutAddress: string
    InAddress: string
}

/// UI-facing projection of SignalIOMap. The full map stays in the Agent runtime owner.
[<CLIMutable>]
type RuntimeIOMapProjection = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    OutAddresses: string array
    InAddresses: string array
    Mappings: RuntimeIOMapMappingProjection array
}

[<CLIMutable>]
type RuntimeWorkStateChangedPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    WorkId: string
    WorkName: string
    PreviousStatusName: string
    PreviousStatusValue: int
    NewStatusName: string
    NewStatusValue: int
    ClockMs: int64
}

[<CLIMutable>]
type RuntimeCallStateChangedPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    CallId: string
    CallName: string
    PreviousStatusName: string
    PreviousStatusValue: int
    NewStatusName: string
    NewStatusValue: int
    IsSkipped: bool
    ClockMs: int64
}

[<CLIMutable>]
type RuntimeStatusChangedPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    PreviousStatusName: string
    PreviousStatusValue: int
    NewStatusName: string
    NewStatusValue: int
}

[<CLIMutable>]
type RuntimeTokenEventPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    KindName: string
    KindValue: int
    TokenValue: int
    WorkId: string
    WorkName: string
    TargetWorkId: string
    TargetWorkName: string
    ClockMs: int64
}

[<CLIMutable>]
type RuntimeCallTimeoutPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    CallId: string
    CallName: string
    TimeoutMs: int
    ClockMs: int64
}

[<CLIMutable>]
type RuntimeHomingPhaseCompletedPayload = {
    SessionId: string
    ModelHash: string
    Generation: int
    Mode: string
    TimestampUtc: DateTime
}

module RuntimeCommandRejected =
    let fromEnvelope (reason: string) (envelope: RuntimeCommandEnvelope) =
        let isMissing = isNull (box envelope)
        {
            SessionId = if isMissing then "" else envelope.SessionId
            ModelHash = if isMissing then "" else envelope.ModelHash
            Generation = if isMissing then 0 else envelope.Generation
            Mode = if isMissing then "" else envelope.Mode
            CommandId = if isMissing then "" else envelope.CommandId
            Reason = reason
            TimestampUtc = DateTime.UtcNow
        }

/// DTO-only runtime session boundary owned by the Agent application layer.
/// Ds2.Backend depends only on this contract; the concrete implementation may
/// reference Ds2.Runtime from the Agent without introducing a Backend -> Runtime edge.
type IRuntimeHubSession =
    abstract member CurrentIdentity : RuntimeSessionIdentity
    abstract member StartAsync : RuntimeEmptyCommand -> Task
    abstract member PauseAsync : RuntimeEmptyCommand -> Task
    abstract member ResumeAsync : RuntimeEmptyCommand -> Task
    abstract member StopAsync : RuntimeEmptyCommand -> Task
    abstract member ResetAsync : RuntimeEmptyCommand -> Task
    abstract member ApplyInitialStatesAsync : RuntimeEmptyCommand -> Task
    abstract member StepAsync : RuntimeStepPolicyCommand -> Task
    abstract member CanAdvanceStepAsync : RuntimeStepPolicyCommand -> Task<bool>
    abstract member StepWithSourcePrimingAsync : RuntimeStepPolicyCommand -> Task
    abstract member BeginStepBatchAsync : RuntimeStepBatchCommand -> Task
    abstract member IsStepBatchActiveAsync : RuntimeEmptyCommand -> Task<bool>
    abstract member EndStepAsync : RuntimeEmptyCommand -> Task
    abstract member AdvanceSimulationToAsync : RuntimeAdvanceSimulationCommand -> Task
    abstract member ForceWorkStateAsync : RuntimeWorkStateCommand -> Task
    abstract member ForceCallStateAsync : RuntimeCallStateCommand -> Task
    abstract member TryForceWorkStateIfGoingAsync : RuntimeWorkStateCommand -> Task<bool>
    abstract member TryForceWorkStateIfReadyAsync : RuntimeWorkStateCommand -> Task<bool>
    abstract member GetWorkStateAsync : RuntimeWorkCommand -> Task<RuntimeGuidStatus>
    abstract member GetCallStateAsync : RuntimeCallCommand -> Task<RuntimeGuidStatus>
    abstract member GetFlowStateAsync : RuntimeFlowTagCommand -> Task<RuntimeGuidFlowTag>
    abstract member SeedTokenAsync : RuntimeTokenCommand -> Task
    abstract member StartSourceWorkAsync : RuntimeWorkCommand -> Task
    abstract member DiscardTokenAsync : RuntimeTokenCommand -> Task
    abstract member InjectIOValueAsync : RuntimeIOValueCommand -> Task
    abstract member InjectIOValueByAddressAsync : RuntimeIOAddressCommand -> Task
    abstract member SetAllFlowStatesAsync : RuntimeFlowTagCommand -> Task
    abstract member ReloadConnectionsAsync : RuntimeEmptyCommand -> Task
    abstract member ReloadDurationsAsync : RuntimeEmptyCommand -> Task
    abstract member StartWithHomingPhaseAsync : RuntimeEmptyCommand -> Task
    abstract member GetWorkTokenAsync : RuntimeWorkCommand -> Task<int>
    abstract member GetTokenOriginAsync : RuntimeTokenCommand -> Task<string>
    abstract member GetSnapshotAsync : RuntimeCommandEnvelope -> Task<RuntimeStateSnapshot>
    abstract member GetIndexProjectionAsync : RuntimeCommandEnvelope -> Task<RuntimeIndexProjection>
    abstract member GetIOMapProjectionAsync : RuntimeCommandEnvelope -> Task<RuntimeIOMapProjection>
