module Ds2.Store.Editor.Tests.HubSourceTests

open System
open System.Net
open System.Reflection
open System.Text.Json
open System.Threading.Tasks
open Ds2.Backend
open Ds2.Backend.Common
open Xunit

let private assertHubMethod (name: string) (parameterType: Type) (returnType: Type) =
    let flags = BindingFlags.Instance ||| BindingFlags.Public
    let methodInfo = typeof<SignalHub>.GetMethod(name, flags)
    Assert.NotNull(methodInfo)
    Assert.Equal(returnType, methodInfo.ReturnType)
    let parameters = methodInfo.GetParameters()
    Assert.Equal(1, parameters.Length)
    Assert.Equal(parameterType, parameters.[0].ParameterType)

[<Fact>]
let ``HubSource.WellKnownSources 는 literal 6개 모두 포함`` () =
    Assert.True(HubSource.isWellKnown HubSource.Control)
    Assert.True(HubSource.isWellKnown HubSource.VirtualPlant)
    Assert.True(HubSource.isWellKnown HubSource.Monitoring)
    Assert.True(HubSource.isWellKnown HubSource.Plc)
    Assert.True(HubSource.isWellKnown HubSource.Web)
    Assert.True(HubSource.isWellKnown HubSource.Resync)

[<Fact>]
let ``HubSource.isWellKnown 은 case-insensitive`` () =
    Assert.True(HubSource.isWellKnown "CONTROL")
    Assert.True(HubSource.isWellKnown "Plc")
    Assert.True(HubSource.isWellKnown "virtualPLANT")

[<Fact>]
let ``HubSource.isWellKnown 은 unknown 차단`` () =
    Assert.False(HubSource.isWellKnown "random_source")
    Assert.False(HubSource.isWellKnown "")
    Assert.False(HubSource.isWellKnown null)

[<Fact>]
let ``HubSource.DefaultAcceptedSources 는 Control + VirtualPlant + Plc + Resync`` () =
    let defaults = HubSource.DefaultAcceptedSources |> Set.ofArray
    Assert.Equal(4, defaults.Count)
    Assert.Contains(HubSource.Control, defaults)
    Assert.Contains(HubSource.VirtualPlant, defaults)
    Assert.Contains(HubSource.Plc, defaults)
    // Resync = PLC 재연결 직후 1회 baseline 스냅샷 — DSPilot 이 받아 추론 기준선만 갱신(edge 처리 금지).
    Assert.Contains(HubSource.Resync, defaults)

[<Fact>]
let ``HubSource.DefaultAcceptedSources 는 Monitoring/Web 차단 (echo / 외부 UI 주입 방지)`` () =
    let defaults = HubSource.DefaultAcceptedSources |> Set.ofArray
    Assert.DoesNotContain(HubSource.Monitoring, defaults)
    Assert.DoesNotContain(HubSource.Web, defaults)

[<Fact>]
let ``위임 스캔은 수신 source와 무관하게 Agent PLC 재쓰기를 차단`` () =
    for source in [| HubSource.Plc; HubSource.Resync; HubSource.Control; HubSource.Monitoring; "pi5"; null |] do
        Assert.False(
            SignalHubWritePolicy.shouldForwardToPlc true true "%QX0.1.2" source,
            $"delegated source '{source}' must not be forwarded")

[<Fact>]
let ``직접 스캔도 PLC 관측 source는 self echo를 차단`` () =
    Assert.False(SignalHubWritePolicy.shouldForwardToPlc false true "%IX0.0.1" HubSource.Plc)
    Assert.False(SignalHubWritePolicy.shouldForwardToPlc false true "%IX0.0.1" "PLC")
    Assert.False(SignalHubWritePolicy.shouldForwardToPlc false true "%IX0.0.1" HubSource.Resync)

[<Fact>]
let ``Agent가 PLC owner인 모드는 제어 source만 PLC로 전달`` () =
    Assert.True(SignalHubWritePolicy.shouldForwardToPlc false true "%QX0.1.2" HubSource.Control)
    Assert.True(SignalHubWritePolicy.shouldForwardToPlc false true "%QX0.1.2" HubSource.VirtualPlant)
    Assert.False(SignalHubWritePolicy.shouldForwardToPlc false false "%QX0.1.2" HubSource.Control)
    Assert.False(SignalHubWritePolicy.shouldForwardToPlc false true "" HubSource.Control)

[<Fact>]
let ``Pi5 delegated ingress preserves the accepted WriteTags batch`` () =
    let identity =
        { SessionId = "delegated-session"
          ModelHash = "model-1"
          Generation = 3
          Mode = HubSource.Monitoring }
    let items =
        [| { Address = "%IX0.0.1"
             Value = "true"
             Source = HubSource.Plc
             OriginTsMs = 123L
             WallClockMs = 456L }
           { Address = "%IX0.0.2"
             Value = "17"
             Source = HubSource.Plc
             OriginTsMs = 124L
             WallClockMs = 457L } |]
    let mutable received : RuntimeIOAddressBatchCommand option = None

    SignalHubRuntimeIngress.injectBatch identity items (fun command ->
        received <- Some command
        Task.CompletedTask)
    |> fun pending -> pending.GetAwaiter().GetResult()

    let command = received.Value
    Assert.Equal(identity.SessionId, command.Envelope.SessionId)
    Assert.Equal(identity.ModelHash, command.Envelope.ModelHash)
    Assert.Equal(identity.Generation, command.Envelope.Generation)
    Assert.Equal(identity.Mode, command.Envelope.Mode)
    Assert.Same(items, command.Items)

[<Fact>]
let ``단말 인증 미설정 Hub는 기존 연결을 모두 허용`` () =
    Assert.True(SignalHubConnectionPolicy.isAllowed false false false false)
    Assert.True(SignalHubConnectionPolicy.isAllowed false false true false)

[<Fact>]
let ``단말 인증 설정 시 헤더 없는 loopback 클라이언트만 예외 허용`` () =
    Assert.True(SignalHubConnectionPolicy.isAllowed true true false false)
    Assert.False(SignalHubConnectionPolicy.isAllowed true false false false)

[<Fact>]
let ``원격 단말은 device credential 검증 성공 시에만 허용`` () =
    Assert.True(SignalHubConnectionPolicy.isAllowed true false true true)
    Assert.False(SignalHubConnectionPolicy.isAllowed true false true false)
    // 로컬이라도 명시적으로 잘못된 헤더를 보냈다면 우회시키지 않는다.
    Assert.False(SignalHubConnectionPolicy.isAllowed true true true false)

[<Fact>]
let ``원격 단말 메서드는 수집 ingress만 허용`` () =
    for methodName in [ "WriteTags"; "ReportScanHeartbeat"; "ReportPlcConnectionStatus" ] do
        Assert.True(SignalHubConnectionPolicy.isRemoteMethodAllowed methodName)
    for methodName in [ "WriteTag"; "SetScanIntervalMs"; "RuntimeStop"; "RuntimeGetSnapshot" ] do
        Assert.False(SignalHubConnectionPolicy.isRemoteMethodAllowed methodName)

[<Theory>]
[<InlineData("127.0.0.1")>]
[<InlineData("::1")>]
[<InlineData("10.0.0.1")>]
[<InlineData("172.16.0.1")>]
[<InlineData("172.31.255.254")>]
[<InlineData("192.168.10.20")>]
[<InlineData("169.254.1.2")>]
[<InlineData("fc00::1")>]
[<InlineData("fd12:3456::1")>]
[<InlineData("fe80::1")>]
let ``private HTTP peer ranges are accepted`` (value: string) =
    Assert.True(SignalHubConnectionPolicy.isPrivateOrLoopbackAddress(IPAddress.Parse value))

[<Theory>]
[<InlineData("8.8.8.8")>]
[<InlineData("172.15.255.255")>]
[<InlineData("172.32.0.1")>]
[<InlineData("192.0.2.10")>]
[<InlineData("2001:4860:4860::8888")>]
let ``public HTTP peer ranges are rejected`` (value: string) =
    Assert.False(SignalHubConnectionPolicy.isPrivateOrLoopbackAddress(IPAddress.Parse value))

[<Fact>]
let ``Runtime HubMethod names are locked`` () =
    Assert.Equal("RuntimeStart", HubMethod.RuntimeStart)
    Assert.Equal("RuntimeApplyInitialStates", HubMethod.RuntimeApplyInitialStates)
    Assert.Equal("RuntimeTryForceWorkStateIfGoing", HubMethod.RuntimeTryForceWorkStateIfGoing)
    Assert.Equal("RuntimeTryForceWorkStateIfReady", HubMethod.RuntimeTryForceWorkStateIfReady)
    Assert.Equal("RuntimeCanAdvanceStep", HubMethod.RuntimeCanAdvanceStep)
    Assert.Equal("RuntimeStepWithSourcePriming", HubMethod.RuntimeStepWithSourcePriming)
    Assert.Equal("RuntimeIsStepBatchActive", HubMethod.RuntimeIsStepBatchActive)
    Assert.Equal("RuntimeGetSnapshot", HubMethod.RuntimeGetSnapshot)
    Assert.Equal("RuntimeGetIndexProjection", HubMethod.RuntimeGetIndexProjection)
    Assert.Equal("RuntimeGetIOMapProjection", HubMethod.RuntimeGetIOMapProjection)
    Assert.Equal("OnRuntimeSnapshot", HubMethod.OnRuntimeSnapshot)
    Assert.Equal("OnRuntimeWorkStateChanged", HubMethod.OnRuntimeWorkStateChanged)
    Assert.Equal("OnRuntimeCallStateChanged", HubMethod.OnRuntimeCallStateChanged)
    Assert.Equal("OnRuntimeStatusChanged", HubMethod.OnRuntimeStatusChanged)
    Assert.Equal("OnRuntimeCommandRejected", HubMethod.OnRuntimeCommandRejected)

[<Fact>]
let ``Delegated PLC status report contract is exposed by SignalHub`` () =
    Assert.Equal("ReportPlcConnectionStatus", HubMethod.ReportPlcConnectionStatus)
    assertHubMethod HubMethod.ReportPlcConnectionStatus typeof<PlcConnectionStatus> typeof<Task>

[<Fact>]
let ``Runtime DTOs expose CLIMutable default constructors`` () =
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeSessionIdentity>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeStateSnapshot>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeIndexProjection>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeIOMapProjection>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeWorkStateChangedPayload>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeCallStateChangedPayload>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeHomingPhaseCompletedPayload>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeEmptyCommand>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeWorkStateCommand>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeIOAddressCommand>))
    Assert.NotNull(Activator.CreateInstance(typeof<RuntimeCommandRejectedPayload>))

[<Fact>]
let ``Runtime SignalHub methods expose DTO-only command surface`` () =
    assertHubMethod HubMethod.RuntimeStart typeof<RuntimeEmptyCommand> typeof<Task>
    assertHubMethod HubMethod.RuntimeStep typeof<RuntimeStepPolicyCommand> typeof<Task>
    assertHubMethod HubMethod.RuntimeCanAdvanceStep typeof<RuntimeStepPolicyCommand> typeof<Task<bool>>
    assertHubMethod HubMethod.RuntimeBeginStepBatch typeof<RuntimeStepBatchCommand> typeof<Task>
    assertHubMethod HubMethod.RuntimeForceWorkState typeof<RuntimeWorkStateCommand> typeof<Task>
    assertHubMethod HubMethod.RuntimeTryForceWorkStateIfGoing typeof<RuntimeWorkStateCommand> typeof<Task<bool>>
    assertHubMethod HubMethod.RuntimeTryForceWorkStateIfReady typeof<RuntimeWorkStateCommand> typeof<Task<bool>>
    assertHubMethod HubMethod.RuntimeGetWorkState typeof<RuntimeWorkCommand> typeof<Task<RuntimeGuidStatus>>
    assertHubMethod HubMethod.RuntimeInjectIOValueByAddress typeof<RuntimeIOAddressCommand> typeof<Task>
    assertHubMethod HubMethod.RuntimeGetSnapshot typeof<RuntimeCommandEnvelope> typeof<Task<RuntimeStateSnapshot>>
    assertHubMethod HubMethod.RuntimeGetIndexProjection typeof<RuntimeCommandEnvelope> typeof<Task<RuntimeIndexProjection>>
    assertHubMethod HubMethod.RuntimeGetIOMapProjection typeof<RuntimeCommandEnvelope> typeof<Task<RuntimeIOMapProjection>>

[<Fact>]
let ``RuntimeStateSnapshot serializes as camelCase DTO with generation`` () =
    let snapshot = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = HubSource.Control
        StatusName = "Running"
        StatusValue = 0
        ClockMs = 1200L
        CurrentTimeMs = 1200L
        NextEventTimeMs = Nullable<int64>(1500L)
        WorkStates = [| { Id = "work-1"; StatusName = "Going"; StatusValue = 1 } |]
        CallStates = [| { Id = "call-1"; StatusName = "Ready"; StatusValue = 0 } |]
        FlowStates = [| { Id = "flow-1"; FlowTagName = "Ready"; FlowTagValue = 0 } |]
        IOValues = [| { Id = "api-1"; Value = "true" } |]
        HasStartableWork = true
        HasActiveDuration = false
        IsHomingPhase = false
        TimestampUtc = DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc)
    }
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let json = JsonSerializer.Serialize(snapshot, options)

    Assert.Contains("\"sessionId\":\"session-1\"", json)
    Assert.Contains("\"modelHash\":\"model-1\"", json)
    Assert.Contains("\"generation\":7", json)
    Assert.Contains("\"mode\":\"control\"", json)
    Assert.Contains("\"nextEventTimeMs\":1500", json)
    Assert.Contains("\"workStates\"", json)

[<Fact>]
let ``Runtime command DTO serializes envelope as camelCase`` () =
    let command = {
        Envelope = {
            SessionId = "session-1"
            ModelHash = "model-1"
            Generation = 7
            Mode = HubSource.Control
            CommandId = "cmd-1"
        }
        WorkId = "work-1"
        StatusName = "Going"
        StatusValue = 1
    }
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let json = JsonSerializer.Serialize(command, options)

    Assert.Contains("\"envelope\"", json)
    Assert.Contains("\"sessionId\":\"session-1\"", json)
    Assert.Contains("\"generation\":7", json)
    Assert.Contains("\"workId\":\"work-1\"", json)
    Assert.Contains("\"statusValue\":1", json)

[<Fact>]
let ``Runtime command rejection payload keeps command identity`` () =
    let command = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = HubSource.Control
        CommandId = "cmd-1"
    }

    let payload = RuntimeCommandRejected.fromEnvelope RuntimeCommandRejectReason.GenerationMismatch command

    Assert.Equal("session-1", payload.SessionId)
    Assert.Equal("model-1", payload.ModelHash)
    Assert.Equal(7, payload.Generation)
    Assert.Equal(HubSource.Control, payload.Mode)
    Assert.Equal("cmd-1", payload.CommandId)
    Assert.Equal(RuntimeCommandRejectReason.GenerationMismatch, payload.Reason)
    Assert.Equal(DateTimeKind.Utc, payload.TimestampUtc.Kind)

[<Fact>]
let ``RuntimeSessionContract accepts matching command identity`` () =
    let current = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = HubSource.Control
    }
    let command = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = "CONTROL"
        CommandId = "cmd-1"
    }

    Assert.True(RuntimeSessionContract.isCurrentCommand current command)
    Assert.True((RuntimeSessionContract.tryRejectCommand current command).IsNone)

[<Fact>]
let ``RuntimeSessionContract rejects stale command identities`` () =
    let current = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = HubSource.Control
    }
    let command = {
        SessionId = "session-1"
        ModelHash = "model-1"
        Generation = 7
        Mode = HubSource.Control
        CommandId = "cmd-1"
    }

    let sessionMismatch = { command with SessionId = "session-2" }
    let modelMismatch = { command with ModelHash = "model-2" }
    let generationMismatch = { command with Generation = 8 }
    let modeMismatch = { command with Mode = HubSource.Monitoring }

    Assert.Equal(
        Some RuntimeCommandRejectReason.SessionMismatch,
        RuntimeSessionContract.tryRejectCommand current sessionMismatch)
    Assert.Equal(
        Some RuntimeCommandRejectReason.ModelHashMismatch,
        RuntimeSessionContract.tryRejectCommand current modelMismatch)
    Assert.Equal(
        Some RuntimeCommandRejectReason.GenerationMismatch,
        RuntimeSessionContract.tryRejectCommand current generationMismatch)
    Assert.Equal(
        Some RuntimeCommandRejectReason.ModeMismatch,
        RuntimeSessionContract.tryRejectCommand current modeMismatch)
