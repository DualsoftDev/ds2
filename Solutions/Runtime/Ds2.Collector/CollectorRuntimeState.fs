namespace Ds2.Collector

open System
open System.Threading

/// Collector ingest path의 운영 상태와 누적 진단값.
/// process liveness와 UA ingest readiness를 분리해 서비스 관리자가 정확히 판단할 수 있게 한다.
type CollectorRuntimeSnapshot = {
    Started: bool
    SubscriptionEnabled: bool
    UaConnected: bool
    WriterHealthy: bool
    Ready: bool
    PendingEnvelopes: int
    ReceivedEnvelopes: int64
    AcknowledgedEnvelopes: int64
    EnqueueFailures: int64
    WriteFailures: int64
    RetriedEnvelopes: int64
    LastReceivedAt: DateTimeOffset option
    LastPersistedAt: DateTimeOffset option
    LastError: string option
}

type CollectorRuntimeState() =
    let gate = obj()
    let mutable started = 0
    let mutable subscriptionEnabled = 0
    let mutable uaConnected = 0
    let mutable writerHealthy = 1
    let mutable receivedEnvelopes = 0L
    let mutable acknowledgedEnvelopes = 0L
    let mutable enqueueFailures = 0L
    let mutable writeFailures = 0L
    let mutable retriedEnvelopes = 0L
    let mutable lastReceivedAt : DateTimeOffset option = None
    let mutable lastPersistedAt : DateTimeOffset option = None
    let mutable lastError : string option = None

    let now () = DateTimeOffset.UtcNow

    member _.MarkStarted(enabled: bool) =
        Volatile.Write(&subscriptionEnabled, if enabled then 1 else 0)
        Volatile.Write(&started, 1)

    member _.MarkStopped() =
        Volatile.Write(&started, 0)
        Volatile.Write(&uaConnected, 0)

    member _.MarkConnected() =
        Volatile.Write(&uaConnected, 1)
        lock gate (fun () -> lastError <- None)

    member _.MarkDisconnected(error: string option) =
        Volatile.Write(&uaConnected, 0)
        match error with
        | Some message when not (String.IsNullOrWhiteSpace message) ->
            lock gate (fun () -> lastError <- Some message)
        | _ -> ()

    member _.MarkReceived() =
        Interlocked.Increment(&receivedEnvelopes) |> ignore
        lock gate (fun () -> lastReceivedAt <- Some(now ()))

    member _.MarkEnqueueFailure(error: string) =
        Interlocked.Increment(&enqueueFailures) |> ignore
        Volatile.Write(&writerHealthy, 0)
        lock gate (fun () -> lastError <- Some error)

    member _.MarkPersisted(acknowledged: int) =
        Interlocked.Add(&acknowledgedEnvelopes, int64 acknowledged) |> ignore
        Volatile.Write(&writerHealthy, 1)
        lock gate (fun () ->
            lastPersistedAt <- Some(now ())
            lastError <- None)

    member _.MarkWriteFailure(retried: int, error: string) =
        Interlocked.Increment(&writeFailures) |> ignore
        Interlocked.Add(&retriedEnvelopes, int64 retried) |> ignore
        Volatile.Write(&writerHealthy, 0)
        lock gate (fun () -> lastError <- Some error)

    member _.Snapshot(pendingEnvelopes: int) =
        let isStarted = Volatile.Read(&started) = 1
        let isEnabled = Volatile.Read(&subscriptionEnabled) = 1
        let isConnected = Volatile.Read(&uaConnected) = 1
        let isWriterHealthy = Volatile.Read(&writerHealthy) = 1
        let received = Interlocked.Read(&receivedEnvelopes)
        let acknowledged = Interlocked.Read(&acknowledgedEnvelopes)
        let enqueueFailed = Interlocked.Read(&enqueueFailures)
        let writesFailed = Interlocked.Read(&writeFailures)
        let retried = Interlocked.Read(&retriedEnvelopes)
        lock gate (fun () -> {
            Started = isStarted
            SubscriptionEnabled = isEnabled
            UaConnected = isConnected
            WriterHealthy = isWriterHealthy
            Ready = isStarted && isWriterHealthy && (not isEnabled || isConnected)
            PendingEnvelopes = pendingEnvelopes
            ReceivedEnvelopes = received
            AcknowledgedEnvelopes = acknowledged
            EnqueueFailures = enqueueFailed
            WriteFailures = writesFailed
            RetriedEnvelopes = retried
            LastReceivedAt = lastReceivedAt
            LastPersistedAt = lastPersistedAt
            LastError = lastError
        })
