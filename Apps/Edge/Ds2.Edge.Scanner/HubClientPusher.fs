module Pi5ScanPoc.HubClientPusher

// Pi5 = SignalR *클라이언트* (Hub 는 클라우드 Agent 소유). 변화 push + 재연결 flush.
//   설계: research_result.md §10.2 / §10.8.
//   - 실시간/재전송 경로 통일: 항상 event_log 에 append 후 FlushBuffer(offset 이후 청크 push).
//     직송 없음(§10.7.1 "flush 중에도 실시간을 seq 뒤에 붙여 순서 유지").
//   - Ack 정식 핸드셰이크(§10.8): Hub.WriteTags 는 Task 반환 서버 메서드 → InvokeAsync 는 서버가 그
//     batch 를 받아 fan-out 완료했을 때 반환. **그 성공 반환 = ack** → 청크 max seq 로 last_acked_seq 전진
//     → retention 이 acked 이하 정리. 별도 ack contract 불필요(InvokeAsync 반환이 곧 왕복 완료).
//   - 재부팅 보정은 EventLog.ReadSince 가 전송값에 이미 적용(저장=raw / 전송=복원).
//   heartbeat : HubMethod.ReportScanHeartbeat(확정) invoke → Hub 가 OnScanHeartbeat fan-out.
//   인증(§10.8): 상시 단말 인증 = **device_id(RPi 시리얼) 단순 membership**. Pi5 가 자기 시리얼을
//     X-Device-Id 헤더로 제시 → Agent 가 cloudinit 화이트리스트에 있으면 통과. Bearer 토큰 없음
//     (provision_token 은 부트스트랩 전용). 헤더는 WebSocket 핸드셰이크에도 실려 서버 헤더 검증과 일치.
//     값 주입은 프로비저닝 몫(하드코딩 없음). 서버 검증 = SignalHub.ValidateDeviceCredential(deviceId).

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Ds2.Backend.Common
open Pi5ScanPoc.Config
open Pi5ScanPoc.EventLog
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.AspNetCore.Http.Connections.Client

/// Pi5 → Hub 단말 신원 헤더. SignalHub.OnConnectedAsync 의 검증과 이름 일치(양쪽 계약).
[<Literal>]
let DeviceIdHeader = "X-Device-Id"

type HubClientPusher(hubCfg: HubConfig, buffer: EventLog, chunkSize: int,
                     onCollectorConfig: CollectorConfigPayload -> unit, log: string -> unit) =

    // StartAsync 가 받은 세션 토큰 — connectLoop(최초 접속 실패 + Closed 재진입 공용)이 참조.
    // 세션 취소 시 이 토큰이 취소되어 재시도 루프가 스스로 멎는다.
    let mutable startCt = CancellationToken.None

    let hub =
        HubConnectionBuilder()
            .WithUrl(hubCfg.Url, fun (opts: HttpConnectionOptions) ->
                // 인증: RPi 시리얼(device_id)만 X-Device-Id 헤더로(WebSocket 핸드셰이크에도 실림).
                if not (String.IsNullOrWhiteSpace hubCfg.DeviceId) then
                    opts.Headers.[DeviceIdHeader] <- hubCfg.DeviceId)
            .WithAutomaticReconnect()
            .Build()

    // Last status per PLC. Connection attempts can finish before SignalR connects,
    // so keep the snapshot and replay it after every initial/reconnection handshake.
    let latestStatuses = ConcurrentDictionary<string, PlcConnectionStatus>()

    let sendConnectionStatus (status: PlcConnectionStatus) : Task =
        task {
            if hub.State = HubConnectionState.Connected then
                try
                    do! hub.InvokeAsync(HubMethod.ReportPlcConnectionStatus, status)
                with ex ->
                    log $"[hub] PLC 상태 보고 실패({status.Name}, 다음 연결/변화 때 재전송): {ex.Message}"
        } :> Task

    let flushConnectionStatuses () : Task =
        task {
            for status in latestStatuses.Values do
                do! sendConnectionStatus status
        } :> Task

    let toTagWrite (change: Ds2.Backend.Plc.PlcTagChange) (wallClockMs: int64) : TagWrite =
        { Address = change.HubAddress
          Value = change.Value
          Source = change.Source
          OriginTsMs = change.OriginTsMs
          // 스캔 직후 각인된 event_log.wall_clock_ms — DSPilot 이 plcTagLog.dateTime 을 이 값으로 기록해
          // 핑 두절→replay 시 신호가 원래 시각으로 복원된다(도착시각으로 찍으면 복구 순간에 뭉침).
          WallClockMs = wallClockMs }

    /// offset(last_acked_seq) 이후 로그를 청크 단위로 push. 실시간·재전송 공용 경로.
    /// 각 청크 InvokeAsync 성공 = ack → SetAckedSeq(청크 max) 로 offset 전진 + retention.
    /// 정상 연결이면 매 사이클 방금 append 한 소량이 바로 나가고 acked → 로그 거의 빈 상태 유지.
    let flushBuffer (ct: CancellationToken) =
        task {
            if hub.State = HubConnectionState.Connected then
                let mutable go = true
                let mutable sent = 0
                while go && not ct.IsCancellationRequested && hub.State = HubConnectionState.Connected do
                    // 매 루프 최신 acked 기준으로 다음 청크 — 앞 청크 ack 로 offset 이 이미 전진.
                    let chunk = buffer.ReadSince(buffer.LastAckedSeq, chunkSize)
                    match chunk with
                    | [] -> go <- false
                    | rows ->
                        let tags = rows |> List.map (fun (_, ch, wall) -> toTagWrite ch wall) |> List.toArray
                        try
                            do! hub.InvokeAsync(HubMethod.WriteTags, tags, ct)  // 성공 반환 = ack(왕복 완료)
                            let maxSeq = rows |> List.map (fun (s, _, _) -> s) |> List.max
                            buffer.SetAckedSeq maxSeq                            // 정식 ack 기록 + acked 이하 정리
                            sent <- sent + tags.Length
                        with ex ->
                            log $"[hub] push/ack 실패(다음 기회 재전송): {ex.Message}"
                            go <- false
                if sent > 0 then log $"[hub] flush {sent}건 push+ack 완료 (last_acked_seq={buffer.LastAckedSeq})"
        }

    // ★ 연결될 때까지 도는 재시도 루프 — 두 갭을 공용으로 막는다:
    //   (1) 최초 StartAsync 실패 : WithAutomaticReconnect 는 '연결된 뒤 끊김'만 커버하고 최초 실패는
    //       커버 안 함(SignalR 계약). PLAY 지연으로 Agent Hub 가 늦게 뜨면 최초 접속이 실패하는데,
    //       예전엔 그대로 포기해 수동 systemctl restart 전까지 영영 안 붙었다.
    //   (2) 재연결 소진 후 Closed : WithAutomaticReconnect 는 4회(0/2/10/30s) 시도 후 포기 → Closed.
    //       WG 터널이 30s 넘게 끊겼다 복구되면 그대로 죽어 있었다. add_Closed 에서 이 루프 재진입.
    //   State=Disconnected 에서만 StartAsync(그 외 상태에서 호출하면 예외). 세션 토큰 취소 시 스스로 멎음.
    let rec connectLoop () =
        task {
            if not startCt.IsCancellationRequested && hub.State = HubConnectionState.Disconnected then
                try
                    do! hub.StartAsync(startCt)
                    log $"[hub] connected → {hubCfg.Url}"
                    do! flushConnectionStatuses ()
                    do! flushBuffer startCt
                with ex ->
                    log $"[hub] 접속 재시도(5s 후): {ex.Message}"
                    try do! Task.Delay(5000, startCt) with :? OperationCanceledException -> ()
                    do! connectLoop ()
        }

    do
        // 재연결 시 밀린 버퍼 flush(각 청크 ack).
        hub.add_Reconnected(Func<string, Task>(fun _ ->
            task {
                do! flushConnectionStatuses ()
                do! flushBuffer startCt
            } :> Task))
        // 재연결 시도 소진 후 Closed → 처음부터 다시 재시도(연결될 때까지). 세션 취소면 no-op.
        hub.add_Closed(Func<exn, Task>(fun _ ->
            if startCt.IsCancellationRequested then Task.CompletedTask
            else connectLoop () :> Task))
        // Agent 가 내려주는 수집기 config(태그) 수신 → Daemon 이 plc.json 병합 → config watch 재구성.
        hub.On<CollectorConfigPayload>(HubMethod.OnCollectorConfig,
            Action<CollectorConfigPayload>(fun payload ->
                try onCollectorConfig payload
                with ex -> log $"[hub] OnCollectorConfig 처리 실패: {ex.Message}"))
        |> ignore

    /// 접속 + 초기 flush. 실패해도 throw 안 함(오프라인이면 버퍼링만 하고 다음 기회에).
    /// 재시도는 connectLoop 이 백그라운드에서 담당 — fire-and-forget 이라 scan loop(버퍼링) 진행을 막지 않는다.
    member _.StartAsync(ct: CancellationToken) =
        startCt <- ct
        // 즉시 리턴 — 재시도는 백그라운드에서. (task 는 hot 이라 ignore 해도 바로 실행 시작.)
        task { connectLoop () |> ignore }

    member _.IsConnected = hub.State = HubConnectionState.Connected

    /// 실시간/재전송 공용 flush. Daemon 이 append 후 매 사이클 호출 — 연결이면 밀린 것 push+ack,
    /// 미연결이면 내부에서 no-op(로그는 append 되어 버퍼가 보관).
    member _.FlushBuffer(ct: CancellationToken) = flushBuffer ct

    /// Cache the real field-side gateway state and report it whenever SignalR is up.
    /// Cached states are replayed automatically after reconnect.
    member _.ReportConnectionStatus(status: PlcConnectionStatus) : Task =
        if isNull (box status) || String.IsNullOrWhiteSpace status.Name then
            Task.CompletedTask
        else
            latestStatuses.[status.Name] <- status
            sendConnectionStatus status

    /// 생존 heartbeat — 확정 contract HubMethod.ReportScanHeartbeat 를 invoke → Hub 가 OnScanHeartbeat fan-out.
    member _.SendHeartbeat() =
        task {
            if hub.State = HubConnectionState.Connected then
                try do! hub.InvokeAsync(HubMethod.ReportScanHeartbeat)
                with ex -> log $"[hub] heartbeat 실패: {ex.Message}"
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                try do! hub.DisposeAsync()
                with _ -> ()
            })
