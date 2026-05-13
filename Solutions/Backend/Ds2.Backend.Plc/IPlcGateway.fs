namespace Ds2.Backend.Plc

open System
open System.Threading
open System.Threading.Tasks

/// 한 태그의 변화 이벤트.
type PlcTagChange = {
    HubAddress : string
    Value      : string
    Source     : string
}

/// SignalHub 와 실 PLC 사이의 경계 인터페이스.
/// - WriteAsync: SignalHub.WriteTag 가 source!="plc" 일 때 호출 → PLC OUT 코일 쓰기.
/// - TagChanged: 스캔 서비스가 읽어들인 IN 변화 이벤트. SignalHub broadcaster 가 구독.
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
