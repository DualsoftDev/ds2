namespace Ds2.Adapter.Common

open System
open Ds2.Core

/// ADR-006 · At-least-once + dedup 을 위한 sample/event envelope.
/// 모든 어댑터 → Collector 파이프라인에서 이 스키마를 유지.

type EnvelopeKind =
    | Sample   // Variable (DataChange / Sampled)
    | Event    // AutoID · PLC trigger 등

type SampleValue =
    | ValueDouble of float
    | ValueLong of int64
    | ValueString of string
    | ValueBool of bool
    | ValueNone

/// 하나의 데이터 포인트를 표현하는 봉투.
type Envelope = {
    EnvelopeId       : Guid              // UUIDv7 권장 (충돌 방지 · 시간 정렬)
    Kind             : EnvelopeKind
    GlobalAssetId    : GlobalAssetId
    SignalId         : SignalId
    SourceTimestamp  : DateTimeOffset    // ADR-003 §1a 단일 원천
    ServerTimestamp  : DateTimeOffset option
    Value            : SampleValue
    StatusCode       : uint32
    Unit             : string option
    SeqNo            : uint64 option
    Origin           : string            // 어댑터 인스턴스 ID
    /// EventKind 인 경우 payload JSON (시각 필드 없음, ADR-003 §4).
    EventPayloadJson : string option
    /// EventKind 인 경우 EventType semanticId.
    EventTypeSemanticId : string option
}
    with
    static member NewSample(gaid, sig', ts, v, unit', origin) : Envelope =
        {
            EnvelopeId = Guid.NewGuid()
            Kind = Sample
            GlobalAssetId = gaid
            SignalId = sig'
            SourceTimestamp = ts
            ServerTimestamp = None
            Value = v
            StatusCode = 0u
            Unit = unit'
            SeqNo = None
            Origin = origin
            EventPayloadJson = None
            EventTypeSemanticId = None
        }

    static member NewEvent(gaid, sig', ts, eventType, payloadJson, origin) : Envelope =
        {
            EnvelopeId = Guid.NewGuid()
            Kind = Event
            GlobalAssetId = gaid
            SignalId = sig'
            SourceTimestamp = ts
            ServerTimestamp = None
            Value = ValueNone
            StatusCode = 0u
            Unit = None
            SeqNo = None
            Origin = origin
            EventPayloadJson = Some payloadJson
            EventTypeSemanticId = Some eventType
        }
