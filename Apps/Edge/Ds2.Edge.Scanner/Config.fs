module Pi5ScanPoc.Config

// config JSON → PlcGatewayConfig 매핑 + 데몬 설정.
//   원래 PoC(Program.fs)에 있던 DTO/변환을 여기로 이동하고, 데몬화에 필요한
//   hub(SignalR 접속)·buffer(SQLite store-and-forward) 설정을 확장한다.
//   plc.json 이 비었거나(connections=[]) 파일이 없으면 daemon 은 idle.

open System
open System.Text.Json
open Ds2.Backend.Plc
open Ds2.Backend.Common

// ── config JSON DTO (Ev2 타입 직접 매핑은 번거로우므로 얇은 DTO 로 받는다) ──
type TagDto = { hub: string; plc: string; dtype: string }

type ConnDto =
    { name: string
      vendor: string          // "LsXgk" | "LsXgi" | "Mitsubishi"
      ip: string
      port: int
      localEthernet: bool
      timeoutMs: int
      scanMs: int
      /// 이 접속을 소유한 System(Guid 문자열). Agent 가 CollectorConfig 로 내려준다.
      /// "" 또는 누락(구버전 Agent) = 미지정 → 종전처럼 귀속 없이 동작.
      systemId: string
      tags: TagDto[] }

/// SignalR Hub 접속 설정.
///   url      : 연결된 클라우드 인스턴스 주소(PV서버가 관리, 프로비저닝이 주입). 비면 push 비활성(버퍼링만).
///   deviceId : RPi 하드웨어 시리얼(device.device_id). X-Device-Id 헤더로 전달 → Agent 화이트리스트 대조.
///              상시 인증은 이 시리얼 membership 만(Bearer 토큰 없음 — provision_token 은 부트스트랩 전용).
/// heartbeat/ack 는 확정 contract(ReportScanHeartbeat / WriteTags InvokeAsync 반환)라 config 불필요.
type HubDto =
    { url: string
      deviceId: string }

/// SQLite store-and-forward 버퍼 설정.
type BufferDto =
    { dbPath: string
      retentionMinutes: int   // 최대 보관(분). 초과분 오래된 것부터 삭제. 0 이면 기본 60.
      maxRows: int            // 최대 로그 수. 초과 시 오래된 것부터 삭제 + 경고. 0 이면 기본 100000.
      chunkSize: int }        // 재연결 flush 청크당 행 수. 0 이면 기본 200.

type CfgDto =
    { connections: ConnDto[]
      hub: HubDto             // STJ: 누락 시 null (참조 타입). Normalize 에서 방어.
      buffer: BufferDto }

// ── 정규화된 데몬 설정 (기본값 적용 후) ──
type BufferConfig =
    { DbPath : string
      RetentionMs : float
      MaxRows : int
      ChunkSize : int }

type HubConfig =
    { Url : string
      DeviceId : string }

type DaemonConfig =
    { Plc : PlcGatewayConfig
      Hub : HubConfig option        // None = push 비활성
      Buffer : BufferConfig
      HasConnections : bool }

let toDataType (s: string) =
    match (if isNull s then "" else s.Trim()) with
    | "Bool"    -> PlcDataTypes.Bool
    | "Int16"   -> PlcDataTypes.Int16
    | "UInt16"  -> PlcDataTypes.UInt16
    | "Int32"   -> PlcDataTypes.Int32
    | "UInt32"  -> PlcDataTypes.UInt32
    | "Float32" -> PlcDataTypes.Float32
    | "Float64" -> PlcDataTypes.Float64
    | _         -> PlcDataTypes.Bool

let toVendor (s: string) =
    match (if isNull s then "" else s.Trim()) with
    | "LsXgi"             -> PlcVendor.LsXgi
    | "LsXgk"             -> PlcVendor.LsXgk
    | "Mitsubishi" | "Mx" -> PlcVendor.Mitsubishi
    | _                   -> PlcVendor.LsXgk

let private toPlcConfig (conns: ConnDto[]) : PlcGatewayConfig =
    { Connections =
        conns
        |> Array.toList
        |> List.map (fun c ->
            { Name = c.name
              // SystemId: multi-System(멀티 PLC) 스코프 태그. Agent 가 CollectorConfig 로 내려준 값을
              // 그대로 신는다 — 이게 있어야 스캔한 태그마다 소유 System 이 각인돼(PlcTagChange.SystemId)
              // 서로 다른 PLC 의 같은 주소가 수신측에서 구분된다.
              // 구버전 Agent payload 에는 이 필드가 없어 null → None(종전 동작).
              SystemId =
                if String.IsNullOrWhiteSpace c.systemId then None
                else
                    match Guid.TryParse c.systemId with
                    | true, sid -> Some sid
                    | _ -> None
              Vendor = toVendor c.vendor
              IpAddress = c.ip
              Port = c.port
              LocalEthernet = c.localEthernet
              NetworkNumber = 0uy
              StationNumber = 0uy
              Transport = PlcTransport.Tcp
              TimeoutMs = c.timeoutMs
              ScanInterval = Some (TimeSpan.FromMilliseconds(float c.scanMs))
              Tags =
                c.tags
                |> Array.toList
                |> List.map (fun t ->
                    { HubAddress = t.hub
                      PlcAddress = (if String.IsNullOrWhiteSpace t.plc then t.hub else t.plc)
                      DataType = toDataType t.dtype }) }) }

let private normalizeBuffer (defaultDir: string) (b: BufferDto) : BufferConfig =
    let dbPath =
        if isNull (box b) || String.IsNullOrWhiteSpace b.dbPath
        then IO.Path.Combine(defaultDir, "pi5-events.db")
        else b.dbPath
    let retMin = if isNull (box b) || b.retentionMinutes <= 0 then 60 else b.retentionMinutes
    let maxRows = if isNull (box b) || b.maxRows <= 0 then 100_000 else b.maxRows
    let chunk = if isNull (box b) || b.chunkSize <= 0 then 200 else b.chunkSize
    { DbPath = dbPath
      RetentionMs = float retMin * 60.0 * 1000.0
      MaxRows = maxRows
      ChunkSize = chunk }

let private normalizeHub (h: HubDto) : HubConfig option =
    if isNull (box h) || String.IsNullOrWhiteSpace h.url then None
    else
        Some { Url = h.url
               DeviceId = (if isNull h.deviceId then "" else h.deviceId) }

/// config 파일을 읽어 정규화된 DaemonConfig 로. 파일이 없으면 None (idle).
/// 파싱 실패 시 예외를 던지지 않고 None (호출부가 idle 로 취급하고 재감시).
let tryLoad (cfgPath: string) : DaemonConfig option =
    try
        if not (IO.File.Exists cfgPath) then None
        else
            let json = IO.File.ReadAllText cfgPath
            if String.IsNullOrWhiteSpace json then None
            else
                let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let dto = JsonSerializer.Deserialize<CfgDto>(json, opts)
                let conns = if isNull dto.connections then [||] else dto.connections
                let defaultDir =
                    let d = IO.Path.GetDirectoryName(IO.Path.GetFullPath cfgPath)
                    if String.IsNullOrEmpty d then "." else d
                Some { Plc = toPlcConfig conns
                       Hub = normalizeHub dto.hub
                       Buffer = normalizeBuffer defaultDir dto.buffer
                       HasConnections = conns.Length > 0 }
    with _ ->
        None

/// Agent 가 push 한 **완결 수집기 config(접속+태그)** 를 로컬 plc.json 에 반영(분리 아키텍처).
/// 흐름(§10, 사용자 확정): 마법사 discovery → PV → cloudinit → 인스턴스(Agent) 가 접속+태그 완결 조립
///   → OnCollectorConfig → Pi5. 따라서 Pi5 는 **받은 payload connections 를 그대로** plc.json 에 쓴다
///   (접속·태그 둘 다 Agent = SSOT). name/IP 매칭 병합 없음.
///   - hub/buffer 섹션만 로컬 유지(마법사가 넣은 접속 url·deviceId·버퍼 설정 보존).
///   - Pi5 로컬 connections(마법사가 임시로 넣었을 수 있는 접속)는 Agent 수신 전 폴백일 뿐 → 수신 시 payload 로 덮인다.
/// 결과를 plc.json 에 쓰면 config watch 가 감지해 기존 재구성 경로로 scan 시작.
/// 내용이 기존과 같으면 쓰지 않음(불필요한 재구성/파일변경 이벤트 방지).
let applyCollectorConfig (cfgPath: string) (payload: CollectorConfigPayload) (log: string -> unit) =
    try
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        // 로컬 plc.json 은 hub/buffer 섹션 보존 목적으로만 읽는다(connections 는 payload 로 완전 교체).
        let localDto =
            if IO.File.Exists cfgPath then
                try Some (JsonSerializer.Deserialize<CfgDto>(IO.File.ReadAllText cfgPath, opts))
                with _ -> None
            else None
        let toTag (t: CollectorTagConfig) : TagDto = { hub = t.Hub; plc = t.Plc; dtype = t.Dtype }
        let payloadConns = if isNull (box payload) || isNull payload.Connections then [||] else payload.Connections
        let conns =
            payloadConns
            |> Array.map (fun ac ->
                { name = ac.Name; vendor = ac.Vendor; ip = ac.Ip; port = ac.Port
                  localEthernet = ac.LocalEthernet; timeoutMs = ac.TimeoutMs; scanMs = ac.ScanMs
                  // 구버전 Agent 는 이 필드를 안 보내 null 이 온다 — plc.json 엔 "" 로 기록.
                  systemId = (if isNull ac.SystemId then "" else ac.SystemId)
                  tags = (if isNull ac.Tags then [||] else ac.Tags) |> Array.map toTag })
        let outDto =
            match localDto with
            | Some d -> { d with connections = conns }   // hub/buffer 로컬 보존, connections = Agent payload
            | None -> { connections = conns
                        hub = Unchecked.defaultof<HubDto>
                        buffer = Unchecked.defaultof<BufferDto> }
        let json = JsonSerializer.Serialize(outDto, JsonSerializerOptions(WriteIndented = true))
        let existing = if IO.File.Exists cfgPath then IO.File.ReadAllText cfgPath else ""
        if json <> existing then
            IO.File.WriteAllText(cfgPath, json)
            log $"[cfg] Agent 완결 config 수신 → plc.json 갱신(connections={conns.Length}, 접속+태그 모두 Agent) → 재구성"
        else
            log "[cfg] Agent config 수신 — 기존과 동일, 갱신 생략"
    with ex ->
        log $"[cfg] Agent config 적용 실패: {ex.Message}"
