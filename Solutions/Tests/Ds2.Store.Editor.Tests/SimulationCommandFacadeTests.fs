module Ds2.Store.Editor.Tests.SimulationCommandFacadeTests

open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Core.SimulationCommandFacade
open Xunit

// SimulationCommandFacade.DecideXxx 의 typed Decision/Rejection 검증.
// Slice 4 에서 RuntimeCommandPolicy.canXxx* boolean 함수가 본 typed facade 로 대체됨.

[<Fact>]
let ``Start 는 정지 또는 일시정지 상태에서 homing 페이즈 아닐 때만 허용`` () =
    Assert.Equal(Accepted, decideStart false false false)
    Assert.Equal(Accepted, decideStart true true false)
    Assert.Equal(Rejected AlreadySimulating, decideStart true false false)
    Assert.Equal(Rejected InHomingPhase, decideStart false false true)

[<Fact>]
let ``Pause 는 passive 모드 / 실 PLC 제어 / 이미 paused / homing 일 때 거부`` () =
    Assert.Equal(Accepted, decidePause true false false RuntimeMode.Simulation false)
    Assert.Equal(Accepted, decidePause true false false RuntimeMode.Control false)
    Assert.Equal(Rejected RealLineControlActive, decidePause true false false RuntimeMode.Control true)
    Assert.Equal(Rejected PassiveMode, decidePause true false false RuntimeMode.VirtualPlant false)
    Assert.Equal(Rejected PassiveMode, decidePause true false false RuntimeMode.Monitoring false)
    Assert.Equal(Rejected AlreadyPaused, decidePause true true false RuntimeMode.Simulation false)
    Assert.Equal(Rejected InHomingPhase, decidePause true false true RuntimeMode.Simulation false)
    Assert.Equal(Rejected NotSimulating, decidePause false false false RuntimeMode.Simulation false)

[<Fact>]
let ``Stop 과 Reset 은 시뮬레이션 동작 중에만 허용`` () =
    Assert.Equal(Accepted, decideStop true)
    Assert.Equal(Rejected NotSimulating, decideStop false)
    Assert.Equal(Accepted, decideReset true)
    Assert.Equal(Rejected NotSimulating, decideReset false)

[<Fact>]
let ``Step 은 Simulation 모드 + (정지 또는 paused) 일 때만 허용`` () =
    Assert.Equal(Accepted, decideStep false false false RuntimeMode.Simulation)
    Assert.Equal(Accepted, decideStep true true false RuntimeMode.Simulation)
    Assert.Equal(Rejected AlreadySimulating, decideStep true false false RuntimeMode.Simulation)
    Assert.Equal(Rejected InHomingPhase, decideStep true true true RuntimeMode.Simulation)
    Assert.Equal(Rejected NotInSimulationMode, decideStep true true false RuntimeMode.Control)
    Assert.Equal(Rejected NotInSimulationMode, decideStep true true false RuntimeMode.VirtualPlant)
    Assert.Equal(Rejected NotInSimulationMode, decideStep true true false RuntimeMode.Monitoring)

[<Fact>]
let ``ForceWork 는 selection 필수 + passive 모드 차단 + real PLC 는 허용 (manual control)`` () =
    Assert.Equal(Accepted, decideForceWork true false false RuntimeMode.Simulation true)
    Assert.Equal(Accepted, decideForceWork true false false RuntimeMode.Control true)
    Assert.Equal(Accepted, decideForceWork true false false RuntimeMode.Control true)  // real PLC 도 허용
    Assert.Equal(Rejected NoSelectedWork, decideForceWork true false false RuntimeMode.Simulation false)
    Assert.Equal(Rejected PassiveMode, decideForceWork true false false RuntimeMode.VirtualPlant true)
    Assert.Equal(Rejected PassiveMode, decideForceWork true false false RuntimeMode.Monitoring true)

[<Fact>]
let ``SeedToken 은 token source 선택 + manual control 조건 충족 시 허용`` () =
    Assert.Equal(Accepted, decideSeedToken true false false RuntimeMode.Simulation true)
    Assert.Equal(Accepted, decideSeedToken true false false RuntimeMode.Control true)
    Assert.Equal(Rejected NoSelectedTokenSource, decideSeedToken true false false RuntimeMode.Simulation false)
    Assert.Equal(Rejected PassiveMode, decideSeedToken true false false RuntimeMode.Monitoring true)
    Assert.Equal(Rejected InHomingPhase, decideSeedToken true false true RuntimeMode.Simulation true)

[<Fact>]
let ``BeginHoming 은 Control + 실 PLC 연결 + 시뮬 미동작 + 누름 안 됨 일 때만 허용`` () =
    Assert.Equal(Accepted, decideBeginHoming RuntimeMode.Control true false false false)
    Assert.Equal(Rejected HomingNotAllowed, decideBeginHoming RuntimeMode.Control false false false false)
    Assert.Equal(Rejected HomingNotAllowed, decideBeginHoming RuntimeMode.Monitoring true false false false)
    Assert.Equal(Rejected AlreadySimulating, decideBeginHoming RuntimeMode.Control true true false false)
    Assert.Equal(Rejected InHomingPhase, decideBeginHoming RuntimeMode.Control true false true false)
    Assert.Equal(Rejected HomingAlreadyPressed, decideBeginHoming RuntimeMode.Control true false false true)

[<Fact>]
let ``IsAccepted 는 Accepted 만 true 로 반환`` () =
    Assert.True(isAccepted Accepted)
    Assert.False(isAccepted (Rejected NotSimulating))
    Assert.False(isAccepted (Rejected AlreadySimulating))

[<Fact>]
let ``RejectionLabel 은 사용자 표시용 한국어 라벨 반환`` () =
    Assert.Equal("이미 시뮬레이션 동작 중", rejectionLabel AlreadySimulating)
    Assert.Equal("시뮬레이션 정지 상태", rejectionLabel NotSimulating)
    Assert.Equal("실 PLC 연결 중에는 차단 (안전)", rejectionLabel RealLineControlActive)
    Assert.Equal("VirtualPlant/Monitoring 모드에서는 사용 불가", rejectionLabel PassiveMode)
