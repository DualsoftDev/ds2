namespace Ds2.Backend.Plc

open System
open System.Threading
open System.Threading.Tasks
open Ds2.Backend.Common

/// 한 태그의 변화 이벤트.
type PlcTagChange = {
    HubAddress : string
    Value      : string
    Source     : string
}

/// SignalHub 와 실 PLC 사이의 경계 인터페이스.
/// - WriteAsync: SignalHub.WriteTag 가 source!="plc" 일 때 호출 → PLC OUT 코일 쓰기.
/// - TagChanged: 스캔 서비스가 읽어들인 IN 변화 이벤트. SignalHub broadcaster 가 구독.
/// - GetConnectionStatuses / ConnectionStatusChanged: 어댑터별 연결 헬스. Promaker WPF / DSPilot 가
///   "PLC 통신 실패" 표시에 사용. 이벤트는 connect/disconnect 전이가 일어난 어댑터만 발화 — 매 sweep 발화 아님.
type IPlcGateway =
    /// 게이트웨이가 활성 상태이며 최소 1개 PLC 가 등록돼 있는지.
    abstract member IsEnabled : bool
    /// 등록된 모든 PLC 에 connect. 실패해도 throw 하지 않고 로그 후 일부 connect 만 살린다.
    abstract member ConnectAllAsync : CancellationToken -> Task
    /// 모든 PLC 에서 disconnect.
    abstract member DisconnectAllAsync : unit -> Task
    /// SignalHub 로부터의 쓰기 위임. 알 수 없는 주소는 false 반환.
    abstract member WriteAsync : address: string * value: string -> Task<bool>
    /// 1회 스캔 사이클. 변화분만 list 로 반환.
    abstract member ScanOnceAsync : CancellationToken -> Task<PlcTagChange list>
    /// 가장 짧은 ScanInterval 반환 (HostedService loop 이 사용).
    abstract member MinScanInterval : TimeSpan option
    /// 런타임 스캔 주기 오버라이드(ms). Hub SetScanIntervalMs / Agent 설정 감시가 set —
    /// scan loop 가 매 iteration 읽어 재시작 없이 즉시 반영한다. None 이면 MinScanInterval 사용.
    abstract member ScanIntervalOverrideMs : int option with get, set
    /// 현재 등록된 모든 PLC 어댑터의 연결 헬스 스냅샷. 아직 connect 시도 전이면 IsConnected=false, FailedAttempts=0.
    abstract member GetConnectionStatuses : unit -> PlcConnectionStatus list
    /// 어댑터별 연결 상태 전이 이벤트. 동일 상태 유지 시 발화 안 함 — flap noise 차단.
    abstract member ConnectionStatusChanged : IEvent<PlcConnectionStatus>
