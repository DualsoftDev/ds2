module Ds2.Core.Tests.AbnormalTests

open System
open Ds2.Core
open Xunit

// v12 §P1 — 정책 독립 코어 타입 + 순수 분류 검증.
// SSOT: D:/dualsoft/Abnormal-Spec-v12.html, 적용 계획: samples/Abnormal-v12-Apply-Plan.md §7.

let private noTarget : AbnormalTarget = { CallId = None; ApiCallId = None; WorkId = None }
let private range10to20 : RxTimingRange = { MinMs = 10; MaxMs = 20 }
let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

// --- enum 값 잠금 (DSPilot/직렬화 계약) ---

[<Fact>]
let ``AbnormalKind enum values locked 0..3`` () =
    Assert.Equal(0, int AbnormalKind.SensorOpen)
    Assert.Equal(1, int AbnormalKind.SensorShort)
    Assert.Equal(2, int AbnormalKind.ActionOver)
    Assert.Equal(3, int AbnormalKind.ActionUnder)

// --- 5-field invariant: Sensor* → Observed=Some, Elapsed=None ---

[<Fact>]
let ``sensorOpen has Observed=Some false and Elapsed=None`` () =
    let r = Abnormal.sensorOpen noTarget t0
    Assert.Equal(AbnormalKind.SensorOpen, r.Kind)
    Assert.Equal<int option>(None, r.ElapsedMs)
    Assert.Equal<bool option>(Some false, r.Observed)

[<Fact>]
let ``sensorShort has Observed=Some true and Elapsed=None`` () =
    let r = Abnormal.sensorShort noTarget t0
    Assert.Equal(AbnormalKind.SensorShort, r.Kind)
    Assert.Equal<int option>(None, r.ElapsedMs)
    Assert.Equal<bool option>(Some true, r.Observed)

// --- 5-field invariant: Action* → Elapsed=Some, Observed=None ---

[<Fact>]
let ``actionOver has Elapsed=Some and Observed=None`` () =
    let r = Abnormal.actionOver noTarget 25 t0
    Assert.Equal(AbnormalKind.ActionOver, r.Kind)
    Assert.Equal<int option>(Some 25, r.ElapsedMs)
    Assert.Equal<bool option>(None, r.Observed)

[<Fact>]
let ``actionUnder has Elapsed=Some and Observed=None`` () =
    let r = Abnormal.actionUnder noTarget 5 t0
    Assert.Equal(AbnormalKind.ActionUnder, r.Kind)
    Assert.Equal<int option>(Some 5, r.ElapsedMs)
    Assert.Equal<bool option>(None, r.Observed)

// --- classifyExpectedRising boundary: Min/Max 정상, Min-1/Max+1 abnormal ---

[<Fact>]
let ``classifyExpectedRising at Min boundary is normal`` () =
    Assert.Equal<AbnormalKind option>(None, Abnormal.classifyExpectedRising range10to20 10)

[<Fact>]
let ``classifyExpectedRising at Max boundary is normal`` () =
    Assert.Equal<AbnormalKind option>(None, Abnormal.classifyExpectedRising range10to20 20)

[<Fact>]
let ``classifyExpectedRising below Min is ActionUnder`` () =
    Assert.Equal<AbnormalKind option>(Some AbnormalKind.ActionUnder, Abnormal.classifyExpectedRising range10to20 9)

[<Fact>]
let ``classifyExpectedRising above Max is ActionOver`` () =
    Assert.Equal<AbnormalKind option>(Some AbnormalKind.ActionOver, Abnormal.classifyExpectedRising range10to20 21)

// --- classifyUnexpectedRising / classifyExpectedFalling ---

[<Fact>]
let ``classifyUnexpectedRising is SensorShort`` () =
    Assert.Equal(AbnormalKind.SensorShort, Abnormal.classifyUnexpectedRising)

[<Fact>]
let ``classifyExpectedFalling is SensorOpen`` () =
    Assert.Equal(AbnormalKind.SensorOpen, Abnormal.classifyExpectedFalling)

// --- classifyTick: 입력 미도달 + Max 초과에서만 ActionOver ---

[<Fact>]
let ``classifyTick fires ActionOver when input inactive and over Max`` () =
    Assert.Equal<AbnormalKind option>(Some AbnormalKind.ActionOver, Abnormal.classifyTick range10to20 21 false)

[<Fact>]
let ``classifyTick is silent when input already active`` () =
    Assert.Equal<AbnormalKind option>(None, Abnormal.classifyTick range10to20 21 true)

[<Fact>]
let ``classifyTick is silent within Max`` () =
    Assert.Equal<AbnormalKind option>(None, Abnormal.classifyTick range10to20 20 false)

// --- latchKeyOf ---

[<Fact>]
let ``latchKeyOf extracts Kind and Target`` () =
    let tgt = Abnormal.target (Some(Guid.NewGuid())) None None
    let r = Abnormal.actionOver tgt 25 t0
    let key = Abnormal.latchKeyOf r
    Assert.Equal(AbnormalKind.ActionOver, key.Kind)
    Assert.Equal(tgt, key.Target)
