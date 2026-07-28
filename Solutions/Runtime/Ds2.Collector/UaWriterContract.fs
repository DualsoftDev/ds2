namespace Ds2.Adapter.Common

open System
open System.Threading.Tasks
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds

/// ADR-002 · UA Client write 계약. 어댑터는 이 인터페이스로 중앙 UA 서버와 통신.
///
/// 실제 UA 스택 구현체는 Phase 4 후반에 wire-up 예정.
type IUaWriter =
    abstract ConnectAsync : unit -> Task
    abstract IsConnected : bool
    abstract WriteAsync :
        nodeId: DeterministicNodeId *
        value: SampleValue *
        sourceTs: DateTimeOffset *
        statusCode: uint32 -> Task<uint32>
    abstract CallRaiseAssetEventAsync :
        methodNodeId: DeterministicNodeId *
        eventTypeSemanticId: string *
        sourceSignalId: SignalId *
        sourceTimestamp: DateTimeOffset *
        payloadJson: string -> Task<Guid * uint32>
    abstract RefreshNamespaceMap : unit -> Task
    abstract DisposeAsync : unit -> Task

/// 서버 접속 없이 로컬 테스트용 stub. 실제 UA 통신 없이 in-memory 로 기록만.
type InMemoryUaWriter() =
    let writes = System.Collections.Concurrent.ConcurrentBag<obj>()
    let mutable connected = false

    member _.Writes = writes :> seq<obj>

    interface IUaWriter with

        member _.ConnectAsync() = task { connected <- true; return () }

        member _.IsConnected = connected

        member _.WriteAsync(nodeId, value, sourceTs, statusCode) = task {
            writes.Add(box {| NodeId = nodeId.Format(); Value = value; Ts = sourceTs; Status = statusCode |})
            return 0u  // Good
        }

        member _.CallRaiseAssetEventAsync(methodNodeId, eventTypeSemanticId, sourceSignalId, sourceTimestamp, payloadJson) = task {
            writes.Add(box {|
                Method = methodNodeId.Format()
                EventType = eventTypeSemanticId
                SignalId = sourceSignalId.Value
                SourceTs = sourceTimestamp
                Payload = payloadJson
            |})
            return (Guid.NewGuid(), 0u)
        }

        member _.RefreshNamespaceMap() = Task.CompletedTask

        member _.DisposeAsync() = task { connected <- false }
