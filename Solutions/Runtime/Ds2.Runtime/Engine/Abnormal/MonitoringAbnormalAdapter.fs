namespace Ds2.Runtime.Engine.Abnormal

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.IO
open Ds2.Runtime.Engine.Core

// =============================================================================
// v12 P3c — Monitoring abnormal adapter (IO-edge 방식).
//
//   적용 계획: samples/Abnormal-v12-Apply-Plan.md (§2.2, §5.2, R2·R4·R6·R7).
//
//   설계(사용자 확정):
//     · 기존 PassiveInferenceSession 의 cycle 학습/정렬(synced, 3사이클)은 건드리지
//       않는다. 그건 Work/Call 상태 추론용이고, abnormal timing 은 그것과 "별개"로 돈다.
//     · ApiCall 마다 OutTag(시작 주소)·InTag(완료 주소)가 박혀 있고, ApiCall →
//       ApiDef.RxGuid → Device Work 로 duration(Min/Max)이 모델에 박혀 있다.
//       그러니 cycle 학습을 기다릴 필요 없이, IO 주소 edge 만 직접 본다:
//         OutTag On(rising)  = going 시작 → 시각 기록
//         InTag  On(rising)  = finish    → elapsed = finish - going, range 와 비교 → Under/Over
//     · going 은 off→on rising 으로만 잡으므로, 관측 도중(사이클 중간)에 시작된
//       1cycle 은 going edge 를 못 봐서 자동으로 버려진다("1cycle 부정확 → 버림").
//     · 같은 주소를 여러 ApiCall 이 공유할 수 있으므로(OutAddressToMappings), rising
//       판정은 주소+방향당 1회만 하고 매핑된 모든 ApiCall 에 적용한다.
//     · SensorShort/SensorOpen(센서 정합)은 cycle 판정이 필요해 이 단계 제외.
//
//   본체 무침투: ioMap/timestamp/sink 를 주입받아 단위 검증 가능.
//   wiring(PLC scan/Observe 경로에서 OnObservedIo 호출)은 P4(R6)에서 연결한다.
// =============================================================================

type MonitoringAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      nowUtc: unit -> DateTime,   // abnormal record timestamp. elapsed/latch 는 OnObservedIo 의 nowMs 사용(R7).
      sink: AbnormalRecord -> unit ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()      // apiCallId → OutTag On(going) 관측시각(ms)
    let prevActive = Dictionary<string, bool>()   // 방향+address → 직전 active (rising edge 판정)
    let latchWindowMs = 5000

    let emitLatched (record: AbnormalRecord) (nowMs: int) =
        let key = Abnormal.latchKeyOf record
        if AbnormalDetector.tryLatch detectorState key nowMs latchWindowMs then
            sink record

    /// off→on rising 판정. 첫 관측은 baseline(rising 아님) → 중간시작 배제.
    let risingEdge (key: string) (active: bool) : bool =
        let wasActive =
            match prevActive.TryGetValue key with
            | true, b -> b
            | _ -> active        // 첫 관측 = baseline → rising 으로 보지 않음
        prevActive.[key] <- active
        (not wasActive) && active

    /// PLC scan 으로 관측된 IO 값. OutTag On=going 시작, InTag On=finish.
    /// (PassiveInference 와 독립적으로 같은 Observe 경로에서 병행 호출한다.)
    member _.OnObservedIo(address: string, value: string, nowMs: int) =
        // 시작측: OutAddress rising → 매핑된 모든 ApiCall 의 going clock 기록.
        match ioMap.GetByOutAddress(address) with
        | [] -> ()
        | outMappings ->
            match Queries.getApiCall outMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveOutputValue apiCall value
                if risingEdge ("OUT:" + address) active then
                    for m in outMappings do
                        goingClock.[m.ApiCallGuid] <- nowMs
            | None -> ()

        // 완료측: InAddress rising → 매핑된 각 ApiCall finish. going 있으면 elapsed vs range.
        match ioMap.GetByInAddress(address) with
        | [] -> ()
        | inMappings ->
            match Queries.getApiCall inMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveInputValue apiCall value
                if risingEdge ("IN:" + address) active then
                    for m in inMappings do
                        match goingClock.TryGetValue m.ApiCallGuid,
                              AbnormalDetector.tryResolveRangeFromMapping index m with
                        | (true, goingAt), Some range ->
                            let elapsed = nowMs - goingAt
                            let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                            match Abnormal.classifyExpectedRising range elapsed with
                            | Some AbnormalKind.ActionUnder -> emitLatched (Abnormal.actionUnder target elapsed (nowUtc ())) nowMs
                            | Some AbnormalKind.ActionOver  -> emitLatched (Abnormal.actionOver target elapsed (nowUtc ())) nowMs
                            | _ -> ()       // 경계 포함 정상 — 오탐 0
                            goingClock.Remove m.ApiCallGuid |> ignore
                        | _ -> ()           // going 미관측(사이클 중간 시작) → 버림
            | None -> ()

    /// observed cycle 재시작/연결 reload 등으로 going 관측을 무효화할 때.
    member _.Reset() =
        goingClock.Clear()
        prevActive.Clear()

    /// C#(DSPilot) 에서 쓰기 위한 팩토리 — System.Func/Action 을 F# 함수로 래핑.
    static member FromDelegates(index: SimIndex, ioMap: SignalIOMap, nowUtc: System.Func<DateTime>, sink: System.Action<AbnormalRecord>) : MonitoringAbnormalAdapter =
        let nowFn () = nowUtc.Invoke()
        let sinkFn r = sink.Invoke r
        MonitoringAbnormalAdapter(index, ioMap, nowFn, sinkFn)
