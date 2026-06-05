module ModelProtocolTests

open System
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent
open Ds2.LlmAgent.Internal

/// Phase 1 YAML protocol PoC 테스트.
/// SSOT: Apps/Promaker/Docs/yaml-protocol-v0.md §3.1 / §3.2 (round-trip 통과).
/// Wire = JSON object (LLM tool_use native, escape 0). YAML 표기 fixture → YamlDotNet → JSON 으로 입력.

let private parseAndApply (store: DsStore) (yaml: string) =
    use jdoc = ModelProtocolYaml.yamlToJson yaml
    let plan = ImportPlanBuilder()
    let diag, refs = ModelProtocol.apply plan store jdoc.RootElement
    diag, refs, plan

let private parseApplyCommit (store: DsStore) (yaml: string) =
    let diag, refs, plan = parseAndApply store yaml
    if diag.HasErrors then
        failwithf "diagnostics 발견: %s" (diag.Format())
    store.ApplyImportPlan("yaml protocol test", plan.Build())
    refs

// ─── §3.1 단일 cylinder ─────────────────────────────────────────────────────

let private singleCylinderYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
      Adv:
        flow: Run
        calls: [Cyl1.ADV]
      Ret:
        flow: Run
        calls: [Cyl1.RET]
    arrows:
      - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``§3.1 단일 cylinder — YAML round-trip 성공`` () =
    let store = DsStore()
    let _refs = parseApplyCommit store singleCylinderYaml

    // Project 1, Systems 2 (Controller + Cyl1)
    let projects = Queries.allProjects store
    Assert.Equal(1, projects.Length)
    Assert.Equal("M1", projects.Head.Name)

    let actives = Queries.activeSystemsOf projects.Head.Id store
    let passives = Queries.passiveSystemsOf projects.Head.Id store
    Assert.Equal(1, actives.Length)
    Assert.Equal("Controller", actives.Head.Name)
    Assert.Equal(1, passives.Length)
    Assert.Equal("Cyl1", passives.Head.Name)

    // Cylinder cascade — Cyl1 의 ApiDef 2개 (ADV/RET) + 자체 Flow + Work×2 + ResetReset Arrow 1개
    let cylApiDefs = Queries.apiDefsOf passives.Head.Id store |> List.map (fun d -> d.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["ADV"; "RET"], cylApiDefs)

    // Controller flow Run + Adv/Ret Work
    let controllerFlows = Queries.flowsOf actives.Head.Id store
    Assert.Equal(1, controllerFlows.Length)
    Assert.Equal("Run", controllerFlows.Head.Name)
    let runWorks = Queries.worksOf controllerFlows.Head.Id store
    Assert.Equal(2, runWorks.Length)
    let workNames = runWorks |> List.map (fun w -> w.LocalName) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["Adv"; "Ret"], workNames)

    // Adv -> Ret : Start arrow (Controller 안 ArrowBetweenWorks)
    let arrows = Queries.arrowWorksOf actives.Head.Id store
    Assert.Equal(1, arrows.Length)
    Assert.Equal(ArrowType.Start, arrows.Head.ArrowType)

[<Fact>]
let ``§3.1 단일 cylinder — export 후 동일 의미 (round-trip 의 SSOT)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml

    // exported JSON 을 YAML 로 변환해서 정상 변환 가능한지 별도 확인 (round-trip 본체는 helper 가 수행).
    use exported = ModelProtocol.exportToJson store
    let yaml = ModelProtocolYaml.jsonElementToYaml exported.RootElement
    Assert.False(System.String.IsNullOrWhiteSpace yaml, "export → YAML 변환이 비어있음")

    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "shape mismatch: %A" diffs)

// ─── §3.2 multi-zone Part flow ──────────────────────────────────────────────

let private multiZoneYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Z1_Adv:    { flow: Run, calls: [Z1_C1.ADV, Z1_C2.ADV] }
        Z1_Punch:  { flow: Run, calls: [P1.PUNCH] }
        Z1_Ret:    { flow: Run, calls: [Z1_C1.RET, Z1_C2.RET] }
    arrows:
        - Z1_Adv   -> Z1_Punch : Start
        - Z1_Punch -> Z1_Ret   : Start

  - { system: Z1_C1, kind: passive, device: cylinder }
  - { system: Z1_C2, kind: passive, device: cylinder }
  - { system: P1,    kind: passive, device: "custom(Pusher)", apis: [PUNCH] }
"""

[<Fact>]
let ``§3.2 multi-zone (1 zone subset) — round-trip 성공`` () =
    let store = DsStore()
    let _ = parseApplyCommit store multiZoneYaml

    let projects = Queries.allProjects store
    Assert.Equal(1, projects.Length)

    let passives = Queries.passiveSystemsOf projects.Head.Id store
    let passiveNames = passives |> List.map (fun s -> s.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList [ "Z1_C1"; "Z1_C2"; "P1" ], passiveNames)

    // P1 (Pusher) ApiDef = PUNCH 1개
    let p1 = passives |> List.find (fun s -> s.Name = "P1")
    let p1Apis = Queries.apiDefsOf p1.Id store |> List.map (fun d -> d.Name)
    Assert.Equal<string list>([ "PUNCH" ], p1Apis)

    // Controller.Run 의 Work 3개 + arrow 2개
    let actives = Queries.activeSystemsOf projects.Head.Id store
    let ctrl = actives |> List.find (fun s -> s.Name = "Controller")
    let runFlow = (Queries.flowsOf ctrl.Id store) |> List.find (fun f -> f.Name = "Run")
    let works = Queries.worksOf runFlow.Id store
    Assert.Equal(3, works.Length)
    let arrows = Queries.arrowWorksOf ctrl.Id store
    Assert.Equal(2, arrows.Length)
    Assert.True(arrows |> List.forall (fun a -> a.ArrowType = ArrowType.Start))

// ─── 부정 케이스 — schema 위반 ───────────────────────────────────────────────

[<Fact>]
let ``protocol 키 누락 → validate 에러`` () =
    let yaml = """
project: M1
systems: []
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("protocol", diag.Format())

[<Fact>]
let ``device 인식 불가 (sugar 미정의) → validate 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: P1
    kind: passive
    device: pusher
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("sugar 미정의", diag.Format())

[<Fact>]
let ``arrow type 누락 → validate 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        A: { flow: Run, calls: [] }
        B: { flow: Run, calls: [] }
    arrows:
        - A -> B
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("type 누락", diag.Format())

// ─── YAML parser subset 검증 (SSOT §2.0) ────────────────────────────────────

[<Fact>]
let ``YAML anchor (&) 사용 → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
defaults: &cyl
  device: cylinder
systems:
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yaml
        ())
    Assert.Contains("anchor", ex.Message)

[<Fact>]
let ``YAML 1.1 boolean coercion (yes) → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
debug: yes
systems: []
"""
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yaml
        ())
    Assert.Contains("1.1 boolean coercion", ex.Message)

// ─── duration grammar / device parser 단위 ──────────────────────────────────

[<Fact>]
let ``parseDuration: 500ms / 2s OK, 그 외 에러`` () =
    Assert.Equal(System.TimeSpan.FromMilliseconds 500., (match ModelProtocol.parseDuration "500ms" with Ok x -> x | Error e -> failwith e))
    Assert.Equal(System.TimeSpan.FromSeconds 2., (match ModelProtocol.parseDuration "2s" with Ok x -> x | Error e -> failwith e))
    Assert.True((match ModelProtocol.parseDuration "1.5s" with Error _ -> true | _ -> false))
    Assert.True((match ModelProtocol.parseDuration "500" with Error _ -> true | _ -> false))

[<Fact>]
let ``parseDevice: cylinder / clamp / robot / custom(Pusher) OK`` () =
    Assert.Equal(ModelProtocol.KnownCylinder, (match ModelProtocol.parseDevice "cylinder" with Ok x -> x | Error e -> failwith e))
    Assert.Equal(ModelProtocol.KnownCylinder, (match ModelProtocol.parseDevice "Cylinder" with Ok x -> x | Error e -> failwith e))
    Assert.Equal(ModelProtocol.KnownClamp, (match ModelProtocol.parseDevice "clamp" with Ok x -> x | Error e -> failwith e))
    Assert.Equal(ModelProtocol.KnownRobot, (match ModelProtocol.parseDevice "robot" with Ok x -> x | Error e -> failwith e))
    Assert.Equal(ModelProtocol.Custom "Pusher", (match ModelProtocol.parseDevice "custom(Pusher)" with Ok x -> x | Error e -> failwith e))
    Assert.True((match ModelProtocol.parseDevice "한글" with Error _ -> true | _ -> false))  // ASCII only
    Assert.True((match ModelProtocol.parseDevice "custom()" with Error _ -> true | _ -> false))  // type 인자 없음

// ─── sanitizeName: '.' 거부 (Phase 1 추가) ──────────────────────────────────

[<Fact>]
let ``sanitizeName: '.' 포함 이름 거부`` () =
    let result = ToolOperations.sanitizeName "Z1.C1" "system" 128
    Assert.NotEqual<string>("", result)
    Assert.Contains("'.'", result)

// ─── queueAddCallAllowDup — concurrent 중복 ApiDef Call 지원 ────────────────

[<Fact>]
let ``concurrent path — 같은 ApiDef N회 등장 OK (queueAddCallAllowDup)`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV, Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml

    // Adv work 안 Call 2개 (Cyl1.ADV ×2)
    let projects = Queries.allProjects store
    let ctrl = (Queries.activeSystemsOf projects.Head.Id store) |> List.head
    let runFlow = (Queries.flowsOf ctrl.Id store) |> List.head
    let advWork = (Queries.worksOf runFlow.Id store) |> List.find (fun w -> w.LocalName = "Adv")
    let calls = Queries.callsOf advWork.Id store
    Assert.Equal(2, calls.Length)

// ─── Review 후속: 누락 parser subset 부정 케이스 (SSOT §2.0) ────────────────

[<Fact>]
let ``YAML custom tag (!tag) → 거부`` () =
    // !!str (YAML 2002 표준) 은 implicit 통과. custom !foo 는 거부 대상.
    let yamlCustom = "protocol: !mytag promaker/v0\nproject: M1\nsystems: []\n"
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yamlCustom
        ())
    Assert.Contains("custom tag", ex.Message)

[<Fact>]
let ``YAML merge key (<<) → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
base:
  device: cylinder
systems:
  - <<: *base
    system: Cyl1
    kind: passive
"""
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yaml
        ())
    // anchor (*base) 가 먼저 트리거되거나 merge key (<<) 둘 중 하나 — 둘 다 거부 대상.
    Assert.True(ex.Message.Contains("merge key") || ex.Message.Contains("anchor"))

[<Fact>]
let ``YAML duplicate map key → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
project: M2
systems: []
"""
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yaml
        ())
    Assert.Contains("uplicate", ex.Message)  // case-insensitive (YamlDotNet 의 "Duplicate" + 본 module 의 "duplicate" 모두 매칭)

[<Fact>]
let ``YAML multi-document (---) → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems: []
---
protocol: promaker/v0
project: M2
systems: []
"""
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocolYaml.yamlToJson yaml
        ())
    Assert.Contains("multi-document", ex.Message)

// ─── §2.7 룰 #6 — kind 와 키 정합성 ─────────────────────────────────────────

[<Fact>]
let ``kind=passive 인데 flow 키 존재 → validate 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: P1
    kind: passive
    device: cylinder
    flows:
      Bad: {}
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("flows 키 존재", diag.Format())

[<Fact>]
let ``kind=active 인데 device 키 존재 → validate 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Bad
    kind: active
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("device 키 존재", diag.Format())

// ─── Critical 2 회귀 — apis: [] 빈 명시 시 default 적용 ─────────────────────

[<Fact>]
let ``apis: [] 빈 list 명시 시 cylinder default ([ADV;RET]) 적용`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Cyl1
    kind: passive
    device: cylinder
    apis: []
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let projects = Queries.allProjects store
    let cyl = (Queries.passiveSystemsOf projects.Head.Id store) |> List.head
    let apis = Queries.apiDefsOf cyl.Id store |> List.map (fun d -> d.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["ADV"; "RET"], apis)

// ─── Critical 3 회귀 — 중복 flow 키 시 diagnostic + (review C1) plan 전체 rollback ────

[<Fact>]
let ``중복 flow 키 → diagnostic + plan 전체 rollback (review C1)`` () =
    // 새 구조 — flows: mapping 안 같은 flow 이름이 JSON dup key 로 두 번 등장하는 케이스.
    // YAML 은 duplicate map key 를 파서가 거부하므로 (별도 테스트 존재), wire 의 dup key 검증은
    // JSON 직접 입력 사용. System.Text.Json 은 dup key 둘 다 enumerate → dispatchActiveFlows 가
    // 두 번째 등장 시 FlowIds.ContainsKey 로 "flow 'Run' 키 중복" diagnostic 발행.
    //
    // review C1 (partial-commit transactional leak): HasErrors 시 부분 op (첫 등장 flow 1번)
    // 가 plan 에 남아 EndTurn 시 store 에 silent commit 되던 회귀 — apply 가 snapshotCount +
    // TruncateTo 로 전체 rollback 보장. 본 테스트 = HasErrors 시 *모든* op 가 0개임을 lock-in.
    let json = """
{
  "protocol": "promaker/v0",
  "project": "M1",
  "systems": [
    { "system": "Controller", "kind": "active",
      "flows": { "Run": {}, "Run": {} }
    }
  ]
}
"""
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, refs = ModelProtocol.apply plan store jdoc.RootElement
    // diagnostic 에 flow 키 중복 메시지 포함
    Assert.True(diag.HasErrors, "중복 flow 키는 HasErrors 발생해야")
    Assert.Contains("flow 'Run'", diag.Format())
    // C1 fix: plan 전체 rollback — 어떤 op 도 남아있으면 안 됨.
    Assert.Equal(0, plan.Operations |> Seq.length)
    // refs 도 invalidate.
    Assert.Empty(refs)

// ─── ArrowType 6종 round-trip (review m4 정합) ──────────────────────────────

[<Theory>]
[<InlineData("Start")>]
[<InlineData("Reset")>]
[<InlineData("StartReset")>]
[<InlineData("ResetReset")>]
[<InlineData("Group")>]
[<InlineData("Unspecified")>]
let ``parseArrowType 6종 모두 round-trip`` (typeName: string) =
    // exportToJson 경유 — 실제 emit 경로 (formatArrowType private) 까지 검증.
    // %A 의존 회피가 본 변경의 핵심이므로 회귀 테스트도 동일 경로 사용.
    let template = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        A: { flow: Run, calls: [] }
        B: { flow: Run, calls: [] }
    arrows:
        - A -> B : __TYPE__
"""
    let yaml = template.Replace("__TYPE__", typeName)
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.GetRawText()
    let expected = "A -\\u003E B : " + typeName
    Assert.Contains(expected, json)

// ─── §3.4 patch round-trip — patch.add + patch.arrows.add ───────────────────

[<Fact>]
let ``§3.4 patch round-trip — Zone 4 추가 시나리오`` () =
    // 1단계: 베이스 모델 (single zone) apply
    let baseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Z1_Adv: { flow: Run, calls: [Z1_C1.ADV] }
        Z1_Ret: { flow: Run, calls: [Z1_C1.RET] }
    arrows:
        - Z1_Adv -> Z1_Ret : Start
  - { system: Z1_C1, kind: passive, device: cylinder }
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml

    // 2단계: patch — Zone 4 system 추가 + 같은 System 에 arrow 추가 (work 간 arrow 는 system-scope, D2)
    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - { system: Z4_C1, kind: passive, device: cylinder }
  arrows:
    add:
      - in: Controller
        entries:
          - Z1_Adv -> Z1_Ret : Reset
"""
    let _ = parseApplyCommit store patchYaml

    // 검증: passive 2개 (Z1_C1 + Z4_C1)
    let projects = Queries.allProjects store
    let passives = Queries.passiveSystemsOf projects.Head.Id store
    let names = passives |> List.map (fun s -> s.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["Z1_C1"; "Z4_C1"], names)
    // arrow 2개 (기존 Start + 신규 Reset)
    let ctrl = (Queries.activeSystemsOf projects.Head.Id store) |> List.head
    let arrows = Queries.arrowWorksOf ctrl.Id store
    Assert.Equal(2, arrows.Length)
    let types = arrows |> List.map (fun a -> a.ArrowType) |> Set.ofList
    Assert.Equal<Set<ArrowType>>(Set.ofList [ArrowType.Start; ArrowType.Reset], types)

[<Fact>]
let ``patch.add — 기존 store 의 같은 이름 system 추가 시 친절 에러 (Major 1)`` () =
    let baseYaml = """
protocol: promaker/v0
project: M1
systems:
  - { system: Cyl1, kind: passive, device: cylinder }
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml

    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - { system: Cyl1, kind: passive, device: cylinder }
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    Assert.Contains("이미 존재", diag.Format())

// ─── patch.remove — dotted-path 1~5 segment (Project/System/Flow/Work/Call/ApiDef) ────

[<Fact>]
let ``patch.remove — System dotted-path (Project.System)`` () =
    // Phase A: single-segment system 호환 폐기. SSOT §2.5.1 정합 — segs[0] = Project name.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  remove:
    - M1.Cyl1
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove cyl1", plan.Build())
    let passives =
        Queries.allProjects store
        |> List.collect (fun p -> Queries.passiveSystemsOf p.Id store)
    Assert.Empty(passives)

[<Fact>]
let ``patch.remove — Flow 경로 → 자식 Work / Call cascade`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let beforeWorkCount = store.Works.Count
    let beforeCallCount = store.Calls.Count
    Assert.True(beforeWorkCount > 0)
    Assert.True(beforeCallCount > 0)
    let patchYaml = """
protocol: promaker/v0
patch:
  remove:
    - M1.Controller.Run
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove flow", plan.Build())
    // Controller 의 Flow Run 제거 + 자식 Adv/Ret Work + Call 자식들도 cascade
    let projects = Queries.allProjects store
    let controller =
        Queries.activeSystemsOf projects.Head.Id store
        |> List.find (fun s -> s.Name = "Controller")
    Assert.Empty(Queries.flowsOf controller.Id store)
    // Adv/Ret Work 2개가 cascade 로 사라져야 함 (singleCylinderYaml 의 Controller.Run flow 자식 Work 2개).
    // Cyl1 의 자체 Work 가 별도로 남을 수 있어 store 전체 count 차이 (-2) 로 검증.
    Assert.Equal(beforeWorkCount - 2, store.Works.Count)

[<Fact>]
let ``patch.remove — Work 경로 → 자기 자신 + 자식 Call cascade`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  remove:
    - M1.Controller.Run.Adv
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove work", plan.Build())
    // Flow Run 은 살아있지만 Work Adv 만 사라지고 Ret 은 유지
    let projects = Queries.allProjects store
    let controller =
        Queries.activeSystemsOf projects.Head.Id store
        |> List.find (fun s -> s.Name = "Controller")
    let runFlow = Queries.flowsOf controller.Id store |> List.head
    let workNames =
        Queries.worksOf runFlow.Id store
        |> List.map (fun w -> w.LocalName)
        |> Set.ofList
    Assert.DoesNotContain("Adv", workNames)
    Assert.Contains("Ret", workNames)

[<Fact>]
let ``patch.remove — ApiDef 경로 (3-segment, System 직접 자식)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    // Cyl1 에는 ADV / RET ApiDef 가 있음
    let patchYaml = """
protocol: promaker/v0
patch:
  remove:
    - M1.Cyl1.ADV
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove apidef", plan.Build())
    let projects = Queries.allProjects store
    let cyl1 =
        Queries.passiveSystemsOf projects.Head.Id store
        |> List.find (fun s -> s.Name = "Cyl1")
    let apiNames =
        Queries.apiDefsOf cyl1.Id store
        |> List.map (fun d -> d.Name)
        |> Set.ofList
    Assert.DoesNotContain("ADV", apiNames)
    Assert.Contains("RET", apiNames)

[<Fact>]
let ``patch.remove — 존재하지 않는 path 는 친절 진단`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  remove:
    - M1.NoSuchSystem
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    Assert.Contains("store 에 없습니다", diag.Format())

// ─── patch.arrows.remove — Flow 단위 entries (arrows.add 대칭) ──────────────────

let private arrowsRemoveBaseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        A: { flow: Run, calls: [] }
        B: { flow: Run, calls: [] }
    arrows:
        - A -> B : Start
"""

[<Fact>]
let ``patch.arrows.remove — Flow 의 arrow 제거`` () =
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    Assert.Equal(1, store.ArrowWorks.Count)
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller
        entries:
          - A -> B
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove arrow", plan.Build())
    Assert.Equal(0, store.ArrowWorks.Count)
    // Work A / B 는 그대로 (arrow 만 제거)
    Assert.Equal(2, store.Works.Count)

[<Fact>]
let ``patch.arrows.remove — Type 지정 제거`` () =
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller
        entries:
          - "A -> B : Start"
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("remove arrow with type", plan.Build())
    Assert.Equal(0, store.ArrowWorks.Count)

[<Fact>]
let ``patch.arrows.remove — 존재하지 않는 from Work`` () =
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller
        entries:
          - NoSuch -> B
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    let fmt = diag.Format()
    Assert.Contains("Work 'NoSuch'", fmt)

[<Fact>]
let ``patch.arrows.remove — 존재하지 않는 System path`` () =
    // 새 구조 — work 간 arrow 는 system-scope (D2). patch.arrows 의 `in:` = system path.
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: NoSuchSystem
        entries:
          - A -> B
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    Assert.Contains("System 'NoSuchSystem' 가 store 에 없습니다", diag.Format())

[<Fact>]
let ``patch.arrows.remove — 매칭되는 Arrow 없음`` () =
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    // A -> B 는 존재. B -> A 는 미존재.
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller
        entries:
          - B -> A
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    Assert.Contains("Arrow 'B -> A'", diag.Format())

[<Fact>]
let ``patch.arrows.remove — 잘못된 arrow 표기 (parseArrowSpec error)`` () =
    // ArrowSpec 형식 위반 (-> 부재) 시 parseArrowSpec 가 Error 반환 → diagnostics 친절 메시지.
    let store = DsStore()
    let _ = parseApplyCommit store arrowsRemoveBaseYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller
        entries:
          - "A B"
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    // 정확한 메시지는 parseArrowSpec 가 결정 — 핵심은 entries 자리에서 진단 발생.
    Assert.Contains("entries[0]", diag.Format())

// ─── Major 2 회귀 — workDuration / opposing override export round-trip ──────

[<Fact>]
let ``workDuration override (Active Work) round-trip`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Slow:
          flow: Run
          calls: [Cyl1.ADV]
          workDuration: 2s
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.GetRawText()
    Assert.Contains("workDuration", json)
    Assert.Contains("2s", json)

[<Fact>]
let ``opposing override (Passive robot = chain) round-trip`` () =
    // robot 의 default opposing 은 none — chain 으로 override 시 export 가 inferOpposing 으로 chain detect.
    // (cylinder/clamp 는 sugar 가 opposing 인자 받지 않아 always chain — override 자체 의미 없음.)
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: R1
    kind: passive
    device: robot
    apis: [PICK, PLACE]
    opposing: chain
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.GetRawText()
    Assert.Contains("\"opposing\":\"chain\"", json)

// ─── Phase 2 §3.1 #1 — WithCyl.json (GUI canonical) round-trip SSOT ──────────
//
// 본 테스트는 *GUI 가 만든 store dump* 가 export → apply 후 *의미-동등* 인지 검증.
// fixture 의 Passive internal Flow 이름 ("cyl_Flow") 은 cylinder sugar 의 default
// 이름과 다르므로 *완전 동등* (FlowNames 포함) 은 깨짐 — short-form emit 정책 trade-off.
//
// **검증 범위 = 완화 shape** (HelperGuiParityTests 와 동등): 카운트 + 관계 + Active 측 이름.
// Passive cascade 의 internal Flow/Work/ApiDef 이름은 cylinder sugar canonical 에 위임.

let private withCylFixturePath =
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "WithCyl.json")

// RelaxedShape / captureRelaxed / roundTrip helper 는 `Helpers/ModelEquivalence.fs` 로 이동 (Phase 2.5 m1/m3).

[<Fact>]
let ``Phase 2 §3.1 #1 — WithCyl.json load → export → apply round-trip (완화 shape 동등)`` () =
    Assert.True(System.IO.File.Exists withCylFixturePath, sprintf "fixture missing: %s" withCylFixturePath)
    let json = System.IO.File.ReadAllText withCylFixturePath
    let loaded = Ds2.Serialization.JsonConverter.deserialize<DsStore> json
    Assert.NotNull(box loaded)

    let shape1 = ModelEquivalence.captureRelaxed loaded
    Assert.True(shape1.SystemNames.Count >= 2, sprintf "loaded store 의 system 추출 실패: %A" shape1.SystemNames)

    // Phase 2.5 m3: round-trip pattern 은 helper 로 단순화 (export → apply → captureRelaxed).
    let _, shape2 = ModelEquivalence.roundTripRelaxed loaded

    Assert.Equal<string option>(shape1.ProjectName, shape2.ProjectName)
    Assert.Equal<Set<string>>(shape1.SystemNames, shape2.SystemNames)
    Assert.Equal<Map<string, Set<string>>>(shape1.ActiveSystemFlowNames, shape2.ActiveSystemFlowNames)
    Assert.Equal<Map<string, Set<string>>>(shape1.PassiveSystemApiDefNames, shape2.PassiveSystemApiDefNames)
    Assert.Equal<Map<string, Set<string>>>(shape1.WorkLocalNames, shape2.WorkLocalNames)
    Assert.Equal<Map<string, int>>(shape1.WorkArrowsByType, shape2.WorkArrowsByType)

[<Fact>]
let ``Phase 2 §3.1 #1b — WithCyl.json export 결과가 cylinder sugar 로 short-form emit`` () =
    // export 결과에서 cylinder sugar 매핑 정확성 lock-in.
    Assert.True(System.IO.File.Exists withCylFixturePath)
    let json = System.IO.File.ReadAllText withCylFixturePath
    let loaded = Ds2.Serialization.JsonConverter.deserialize<DsStore> json
    use exported = ModelProtocol.exportToJson loaded
    let raw = exported.RootElement.GetRawText()
    // SystemType="Unit" + apis=[ADV,RET] → cylinder sugar emit + apis 키 생략
    Assert.Contains("\"device\":\"cylinder\"", raw)
    // workDuration override 없음 (모든 work = 500ms default)
    Assert.DoesNotContain("\"workDuration\"", raw)
    // opposing override 없음 (cylinder default = chain, fixture 도 chain N-1)
    Assert.DoesNotContain("\"opposing\"", raw)

// ─── Phase 2 §3.1 #3 — short-form round-trip sugar deterministic lock-in ────
//
// LLM 이 작성한 short-form doc → apply → exportToJson → apply (새 store) 시
// *완전* 동등 (Passive internal Flow 이름 포함) 보장. sugar (cylinder/clamp/robot)
// 매핑이 deterministic 이라는 정책의 직접 증인.
//
// **기존 §3.1 단일 cylinder round-trip** (line 86) 가 cylinder 1종 cover.
// 본 테스트는 multi-sugar 조합 + override (workDuration / opposing) 까지 cover.

let private multiSugarYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Step1:
          flow: Run
          calls: [Cyl1.ADV, Clm1.CLP]
        Step2:
          flow: Run
          calls: [R1.PICK, R1.PLACE]
          workDuration: 2s
        Step3:
          flow: Run
          calls: [Cyl1.RET, Clm1.UNCLP]
    arrows:
        - Step1 -> Step2 : Start
        - Step2 -> Step3 : Start

  - system: Cyl1
    kind: passive
    device: cylinder

  - system: Clm1
    kind: passive
    device: clamp

  - system: R1
    kind: passive
    device: robot
    apis: [PICK, PLACE]
    opposing: chain
"""

[<Fact>]
let ``Phase 2 §3.1 #3 — multi-sugar short-form round-trip 완전 동등 (cylinder + clamp + robot)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store multiSugarYaml
    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "multi-sugar round-trip mismatch: %A" diffs)

// ─── Phase 2 §3.1 #4 — YAML plain scalar 안정화 ─────────────────────────────

[<Fact>]
let ``Phase 2 §3.1 #4 — ASCII identifier 는 plain scalar 로 emit`` () =
    // JSON: {"device": "cylinder", "kind": "active"} → YAML 에서 quoted 없이 emit.
    let json = """{"device":"cylinder","kind":"active","name":"Cyl1"}"""
    use doc = JsonDocument.Parse(json)
    let yaml = ModelProtocolYaml.jsonElementToYaml doc.RootElement
    // ASCII identifier 가 plain (no double quote) 으로 출력.
    Assert.Contains("device: cylinder", yaml)
    Assert.Contains("kind: active", yaml)
    Assert.Contains("name: Cyl1", yaml)
    Assert.DoesNotContain("device: \"cylinder\"", yaml)
    Assert.DoesNotContain("\"active\"", yaml)

[<Fact>]
let ``Phase 2 §3.1 #4 — dotted-path / 공백 포함 string 은 quoted 유지`` () =
    let json = """{"call":"Cyl1.ADV","arrow":"Adv -> Ret : Start"}"""
    use doc = JsonDocument.Parse(json)
    let yaml = ModelProtocolYaml.jsonElementToYaml doc.RootElement
    // dotted-path (`.`) / 공백 포함 string 은 ASCII identifier 패턴 미매칭 → quoted.
    Assert.Contains("\"Cyl1.ADV\"", yaml)
    Assert.Contains("\"Adv -> Ret : Start\"", yaml)

[<Theory>]
[<InlineData("true")>]
[<InlineData("false")>]
[<InlineData("True")>]
[<InlineData("yes")>]
[<InlineData("on")>]
[<InlineData("null")>]
[<InlineData("Null")>]
[<InlineData("~")>]
// YAML 1.1 단축 boolean (m1, 외부 review) — y/Y/n/N 4종 defensive cover
[<InlineData("y")>]
[<InlineData("Y")>]
[<InlineData("n")>]
[<InlineData("N")>]
let ``Phase 2 §3.1 #4 — YAML reserved token 은 plain 으로 emit 금지 (quoted 유지)`` (value: string) =
    let json = sprintf """{"k":"%s"}""" value
    use doc = JsonDocument.Parse(json)
    let yaml = ModelProtocolYaml.jsonElementToYaml doc.RootElement
    Assert.Contains(sprintf "\"%s\"" value, yaml)

// ─── Phase 2 §3.1 #5 — device fingerprint 강화 ──────────────────────────────

[<Fact>]
let ``Phase 2 §3.1 #5 — Unit + 비표준 apis 는 custom(Unit) + apis long-form + 순서 보존`` () =
    // Unit SystemType 인데 apis 가 cylinder/clamp fingerprint 와 다른 경우 — custom(Unit) fallback.
    // 단, SSOT §3.4.4 정책상 LLM doc 입력은 known sugar 3종만 허용 → custom(Unit) 은
    // *export 결과* 에서만 등장 (사용자 GUI 수정 케이스). 본 테스트는 helper 직접 호출로 시뮬레이션.
    let store = DsStore()
    store.AddProject("M1") |> ignore
    let plan = ImportPlanBuilder()
    let _ = ToolOperations.queueAddDevice plan store "X1" "Unit" [ "OPEN"; "CLOSE"; "STOP" ] "none" None
    store.ApplyImportPlan("fingerprint test", plan.Build())

    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    Assert.Contains("\"device\":\"custom(Unit)\"", raw)
    // m6 (외부 review): apis 순서 보존 — substring 정확 매칭으로 확인.
    Assert.Contains("\"apis\":[\"OPEN\",\"CLOSE\",\"STOP\"]", raw)

[<Fact>]
let ``Phase 2 §3.1 #5b — custom(Unit) export 결과가 다시 apply 시 동등 (m4 round-trip 보강)`` () =
    // m4 (외부 review): custom(Unit) emit 결과가 round-trip 가능한지 검증.
    let store = DsStore()
    store.AddProject("M1") |> ignore
    let plan = ImportPlanBuilder()
    let _ = ToolOperations.queueAddDevice plan store "X1" "Unit" [ "OPEN"; "CLOSE"; "STOP" ] "none" None
    store.ApplyImportPlan("fingerprint test", plan.Build())

    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "custom(Unit) round-trip mismatch: %A" diffs)

[<Fact>]
let ``Phase 2 §3.1 #5 — robot 은 SystemType=Robot + apis 항상 명시`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: R1
    kind: passive
    device: robot
    apis: [PICK, PLACE]
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    Assert.Contains("\"device\":\"robot\"", raw)
    Assert.Contains("\"apis\":[", raw)
    Assert.Contains("\"PICK\"", raw)
    Assert.Contains("\"PLACE\"", raw)

[<Fact>]
let ``Phase 2 §3.1 #5 — clamp fingerprint round-trip (Unit + CLP/UNCLP)`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Clm1
    kind: passive
    device: clamp
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    // clamp fingerprint 매칭 → device:clamp 만 emit, apis 키 부재 (short-form).
    Assert.Contains("\"device\":\"clamp\"", raw)
    // clamp 의 apis 는 sugar default — 생략됨.
    Assert.DoesNotContain("\"apis\"", raw)

[<Fact>]
let ``Phase 2 §3.1 #5 — custom DU literal (custom(Pusher)) round-trip`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: P1
    kind: passive
    device: "custom(Pusher)"
    apis: [PUNCH]
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    Assert.Contains("\"device\":\"custom(Pusher)\"", raw)
    Assert.Contains("\"PUNCH\"", raw)

[<Fact>]
let ``Phase 2 §3.1 #4 — exportToJson 결과를 YAML 변환 시 device 키가 plain emit`` () =
    // 실제 export 경로 통합 검증 — exportToJson 의 device 값이 YAML view 에서 plain.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    use exported = ModelProtocol.exportToJson store
    let yaml = ModelProtocolYaml.jsonElementToYaml exported.RootElement
    // Active "Controller" / kind "active" / device "cylinder" 등 ASCII identifier 가 plain.
    Assert.Contains("device: cylinder", yaml)
    Assert.Contains("kind: active", yaml)
    Assert.Contains("kind: passive", yaml)

[<Fact>]
let ``Phase 2 §3.1 #3b — 동일 short-form doc 2회 apply (서로 다른 store) 시 cascade 자식 이름 동일`` () =
    // sugar 매핑이 *deterministic* 이라는 의미: 같은 input → 같은 cascade 자식 이름.
    let store1 = DsStore()
    let _ = parseApplyCommit store1 multiSugarYaml
    let shape1 = ModelEquivalence.captureShape store1

    let store2 = DsStore()
    let _ = parseApplyCommit store2 multiSugarYaml
    let shape2 = ModelEquivalence.captureShape store2

    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "deterministic 검증 실패 — 같은 doc 가 다른 cascade 생성: %A" diffs)

[<Fact>]
let ``Phase 2 §3.1 #3c — robot opposing=chain 가 apply 측에서 ResetReset N-1 wiring (m3 lock-in)`` () =
    // m3 자가 검열: multiSugarYaml 의 R1 (robot + apis 2개 + opposing chain) 이 apply 시점에
    // ResetReset arrow 1개 (= apis.Length - 1) wiring 인지 명시적 검증.
    let store = DsStore()
    let _ = parseApplyCommit store multiSugarYaml
    let projects = Queries.allProjects store
    let r1 = Queries.passiveSystemsOf projects.Head.Id store |> List.find (fun s -> s.Name = "R1")
    let resetResets =
        Queries.arrowWorksOf r1.Id store
        |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
        |> List.length
    Assert.Equal(1, resetResets)  // chain: N-1 = 2-1 = 1

[<Fact>]
let ``Phase 2 §3.1 #1c — WithCyl.json export 가 DevicesAlias 가 아닌 systemName 으로 calls emit (SSOT §1.7 lock-in)`` () =
    // SSOT §1.7 정책 변경의 직접 증인 — alias≠systemName 인 fixture 에서 systemName 으로 정정 emit 되는지.
    // WithCyl.json: devicesAlias="cyl" / systemName="NewFlow_cyl" — round-trip 시 "NewFlow_cyl.ADV/RET" 로 emit 되어야 함.
    Assert.True(System.IO.File.Exists withCylFixturePath)
    let json = System.IO.File.ReadAllText withCylFixturePath
    let loaded = Ds2.Serialization.JsonConverter.deserialize<DsStore> json
    use exported = ModelProtocol.exportToJson loaded
    let raw = exported.RootElement.GetRawText()
    // systemName 기반 emit (정책 채택 결과)
    Assert.Contains("\"NewFlow_cyl.ADV\"", raw)
    Assert.Contains("\"NewFlow_cyl.RET\"", raw)
    // alias 기반 emit 금지 (정책 deprecation)
    Assert.DoesNotContain("\"cyl.ADV\"", raw)
    Assert.DoesNotContain("\"cyl.RET\"", raw)

// ─── Phase 2.5 cycle2 M4 — KnownSugars.tryMatchFingerprint 단위 테스트 (5인 review) ─

[<Fact>]
let ``KnownSugars — Unit + [ADV;RET] → cylinder`` () =
    let m = KnownSugars.tryMatchFingerprint "Unit" [ "ADV"; "RET" ]
    Assert.Equal(Some "cylinder", m |> Option.map (fun s -> s.DeviceCase))

[<Fact>]
let ``KnownSugars — Unit + [RET;ADV] → cylinder (순서 무관)`` () =
    let m = KnownSugars.tryMatchFingerprint "Unit" [ "RET"; "ADV" ]
    Assert.Equal(Some "cylinder", m |> Option.map (fun s -> s.DeviceCase))

[<Fact>]
let ``KnownSugars — Unit + [CLP;UNCLP] → clamp`` () =
    let m = KnownSugars.tryMatchFingerprint "Unit" [ "CLP"; "UNCLP" ]
    Assert.Equal(Some "clamp", m |> Option.map (fun s -> s.DeviceCase))

[<Fact>]
let ``KnownSugars — Robot + 임의 apis → robot (apis 자유)`` () =
    let m = KnownSugars.tryMatchFingerprint "Robot" [ "PICK"; "PLACE"; "HOME" ]
    Assert.Equal(Some "robot", m |> Option.map (fun s -> s.DeviceCase))

[<Fact>]
let ``KnownSugars — Unit + [FOO] → None (sugar 미적용 fallback)`` () =
    let m = KnownSugars.tryMatchFingerprint "Unit" [ "FOO" ]
    Assert.True(m.IsNone)

[<Fact>]
let ``KnownSugars — 미지 SystemType (Conveyor) + [] → None (확장 sugar 미정의)`` () =
    let m = KnownSugars.tryMatchFingerprint "Conveyor" []
    Assert.True(m.IsNone)

// ─── Phase 2.5 cycle2 M5 — formatArrowType enum 전수 cover (5인 review) ────────

[<Fact>]
let ``formatArrowType — 모든 ArrowType enum 값이 SSOT 명시 케이스로 직렬화 (Unknown fallback 진입 0)`` () =
    // SSOT §2.4 에 명시된 6 케이스 — fallback `Unknown(<n>)` 진입 시 silent divergence.
    // 신규 ArrowType 추가 시 본 테스트 실패 → SSOT 명시 + formatArrowType 분기 추가 강제.
    let expected = Set.ofList [ "Start"; "Reset"; "StartReset"; "ResetReset"; "Group"; "Unspecified" ]
    let actual =
        System.Enum.GetValues(typeof<ArrowType>)
        :?> ArrowType array
        |> Array.map ModelProtocol.formatArrowType
        |> Set.ofArray
    let unknown = actual |> Set.filter (fun s -> s.StartsWith("Unknown("))
    Assert.True(unknown.IsEmpty, sprintf "formatArrowType Unknown fallback 진입: %A" (Set.toList unknown))
    Assert.True(Set.isSubset actual expected, sprintf "SSOT 외 직렬화: %A" (Set.toList (Set.difference actual expected)))

// ─── patch.add child 추가 — 기존 active system 재사용 ─────────────────────────

[<Fact>]
let ``patch.add — in System 의 flow 키는 기존 active 아래 Flow 를 추가`` () =
    let baseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Existing: { flow: Run, calls: [] }
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml

    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - in: Controller
      flows:
        Inspection: {}
      works:
        Check: { flow: Inspection, calls: [] }
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, diag.Format())

    let addSystemCount =
        plan.Operations
        |> Seq.filter (function AddSystem _ -> true | _ -> false)
        |> Seq.length
    let activeLinkCount =
        plan.Operations
        |> Seq.filter (function LinkSystemToProject(_, _, true) -> true | _ -> false)
        |> Seq.length
    Assert.Equal(0, addSystemCount)
    Assert.Equal(0, activeLinkCount)

    store.ApplyImportPlan("patch add flow", plan.Build())
    let project = Queries.allProjects store |> List.head
    let activeSystems = Queries.activeSystemsOf project.Id store
    Assert.Single(activeSystems) |> ignore
    Assert.Equal("Controller", activeSystems.Head.Name)
    let flows = Queries.flowsOf activeSystems.Head.Id store |> List.map (fun f -> f.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList [ "Run"; "Inspection" ], flows)

[<Fact>]
let ``patch.add — in Flow.works 는 기존 Flow 아래 Work 와 Call 을 추가`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml

    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - in: Controller.Run.works
      Inspect:
        calls: [Cyl1.ADV]
"""
    let _ = parseApplyCommit store patchYaml

    let project = Queries.allProjects store |> List.head
    let activeSystems = Queries.activeSystemsOf project.Id store
    Assert.Single(activeSystems) |> ignore
    let run = Queries.flowsOf activeSystems.Head.Id store |> List.find (fun f -> f.Name = "Run")
    let inspect = Queries.worksOf run.Id store |> List.find (fun w -> w.LocalName = "Inspect")
    let calls = Queries.callsOf inspect.Id store
    Assert.Single(calls) |> ignore
    Assert.Equal("Cyl1.ADV", calls.Head.Name)

// ─── review C3 회귀 — useAllowDup 가 arrow parse 실패와 부재 구분 ────────────

[<Fact>]
let ``useAllowDup — arrows 키 명시 + parse error 라도 concurrent 분기 진입 금지 (review C3)`` () =
    // review C3: 사용자가 arrows: 키를 명시했음에도 모든 entry 가 parse error 면
    // (workArrowStrings.IsEmpty 가 true 가 되어) useAllowDup = true → concurrent 의도로 silent 분기.
    // fix: workArrowsList.IsEmpty (키 자체 존재 여부) 로 판정. parse error 는 별도 diagnostic.
    //
    // 본 테스트: 중복 ApiDef call (`[A, A]`) + arrows 키 명시 (단 parse error 인 broken arrow) →
    // sequential path 진입 시도 → 중복 call 으로 hasCallNameClash → 별도 에러.
    // 핵심 검증: arrows parse error 의 diagnostic 이 누적되어야 함 (silent drop 회피).
    let json = """
{
  "protocol": "promaker/v0",
  "project": "M1",
  "systems": [
    { "system": "Cyl1", "kind": "passive", "device": "cylinder" },
    { "system": "Controller", "kind": "active",
      "flows": { "Run": {} },
      "works": {
        "W1": {
          "flow": "Run",
          "calls": ["Cyl1.ADV", "Cyl1.ADV"],
          "arrows": ["BROKEN_ARROW_NO_TYPE"]
        }
      }
    }
  ]
}
"""
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    // arrows parse error 가 diagnostic 으로 누적되어야 (silent drop 회피).
    Assert.True(diag.HasErrors, "arrows entry parse error 가 diagnostic 으로 누적되어야")
    // C1 rollback 도 동반 — plan 비어있음.
    Assert.Equal(0, plan.Operations |> Seq.length)

// ─── review M1 회귀 — doc-level entity 이름 sanitize 가드 ──────────────────

[<Theory>]
[<InlineData("Active System @ prefix",
    """{"protocol":"promaker/v0","project":"M1","systems":[{"system":"@Bad","kind":"active"}]}""")>]
[<InlineData("Passive System $ prefix",
    """{"protocol":"promaker/v0","project":"M1","systems":[{"system":"$Bad","kind":"passive","device":"cylinder"}]}""")>]
[<InlineData("Work localName 에 '.' 포함",
    """{"protocol":"promaker/v0","project":"M1","systems":[{"system":"Ctl","kind":"active","flows":{"Run":{}},"works":{"A.B":{"flow":"Run","calls":[]}}}]}""")>]
let ``doc-level entity 이름 sanitize — 3 진입점 차단 + 전체 rollback (review M1)`` (label: string) (json: string) =
    // Phase 5 op-layer cleanup 으로 SanitizeOrThrow 가 일소 — doc-level dispatcher 의 sanitize
    // 가드가 entry 이름 (Active/Passive System, Work localName) 의 `@`/`$` prefix / '.' / Cc/Cf
    // 등을 모두 차단해야 함. `ToolOperations.sanitizeName` 위임.
    // Flow 키는 `flowKeyRegex` (`[A-Za-z0-9_\-]+`) 가 sanitize 보다 strict 라 별도 fact 불요
    // — regex 가 먼저 reject. Rename newName 의 sanitize 도 가드되나 store 가 비어있으면 분리 검증 어려움.
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    Assert.True(diag.HasErrors, sprintf "%s: HasErrors 발생해야" label)
    Assert.Contains("VALIDATION_ERROR", diag.Format())
    // C1 rollback 동반.
    Assert.Equal(0, plan.Operations |> Seq.length)

// ─── review M5 회귀 — patch.add 성공 + patch.arrows.add 실패 시 patch.add 까지 rollback ─

[<Fact>]
let ``multi-stage rollback — patch.add 성공 후 후속 단계 실패 시 patch.add 까지 rollback (review M5)`` () =
    // C1 의 "HasErrors 시 plan 전체 TruncateTo" 가 *복합 patch* (add + arrows.add 등 다단계) 에도
    // 적용됨을 lock-in. 본 테스트: patch.add 로 system 생성 성공 + patch.arrows.add 로 존재하지 않는
    // flow path 에 arrow 시도 → arrows.add diag 누적 → 전체 rollback (system add 포함).
    let json = """
{
  "protocol": "promaker/v0",
  "patch": {
    "add": [
      { "system": "NewSys", "kind": "passive", "device": "cylinder" }
    ],
    "arrows": {
      "add": [
        { "in": "NoSuchSystem.NoSuchFlow", "arrows": ["A -> B : Start"] }
      ]
    }
  }
}
"""
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    Assert.True(diag.HasErrors, "후속 단계 실패 시 HasErrors")
    // M5 fix lock-in: patch.add 의 system op 도 rollback — plan 비어있음.
    Assert.Equal(0, plan.Operations |> Seq.length)

// ─── Phase 7 §4.2 C-3 — Condition tree + ContactKind dual format ────────
//
// SSOT yaml-protocol-v0.md §2.2.1 dual format. enhanced calls 의 emit/apply round-trip 검증.

let private conditionYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              contactKind: NcContact
              condition:
                type: ComAux
                isOR: true
                conditions:
                  - ref: Cyl1.RET
                    contactKind: RisingPulse
                  - Cyl1.ADV
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-3 — Condition + ContactKind dual format round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store conditionYaml

    // Adv work 의 Call (Cyl1.ADV) — ContactKind + Condition 적용 확인
    let projects = Queries.allProjects store
    let controller = Queries.activeSystemsOf projects.Head.Id store |> List.head
    let runFlow = Queries.flowsOf controller.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let advCall = Queries.callsOf advWork.Id store |> List.head

    // ContactKind (1:1 invariant — ApiCalls[0])
    Assert.NotEmpty(advCall.ApiCalls)
    Assert.Equal(ContactKind.NcContact, advCall.ApiCalls.[0].ContactKind)

    // Condition root 1개
    Assert.Equal(1, advCall.Conditions.Count)
    let cond = advCall.Conditions.[0]
    Assert.Equal(Some ConditionType.ComAux, cond.Type)
    Assert.True(cond.IsOR)
    Assert.False(cond.IsInverted)
    Assert.Equal(2, cond.ApiCalls.Count)
    Assert.Equal(ContactKind.RisingPulse, cond.ApiCalls.[0].ContactKind)
    Assert.Equal(ContactKind.NoContact, cond.ApiCalls.[1].ContactKind)  // string scalar = default

    // emit 시 enhanced 형태 (object) 확인
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"contactKind\":\"NcContact\"", compact)
    Assert.Contains("\"type\":\"ComAux\"", compact)
    Assert.Contains("\"isOR\":true", compact)
    Assert.Contains("\"contactKind\":\"RisingPulse\"", compact)

    // round-trip 의미 보존: 다시 새 store 에 apply
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("C-3 round-trip", plan2.Build())

    let controller2 = Queries.activeSystemsOf (Queries.allProjects store2).Head.Id store2 |> List.head
    let runFlow2 = Queries.flowsOf controller2.Id store2 |> List.head
    let advWork2 = Queries.worksOf runFlow2.Id store2 |> List.find (fun w -> w.LocalName = "Adv")
    let advCall2 = Queries.callsOf advWork2.Id store2 |> List.head
    Assert.Equal(ContactKind.NcContact, advCall2.ApiCalls.[0].ContactKind)
    Assert.Equal(1, advCall2.Conditions.Count)
    let cond2 = advCall2.Conditions.[0]
    Assert.Equal(Some ConditionType.ComAux, cond2.Type)
    Assert.True(cond2.IsOR)
    Assert.Equal(2, cond2.ApiCalls.Count)
    Assert.Equal(ContactKind.RisingPulse, cond2.ApiCalls.[0].ContactKind)

[<Fact>]
let ``Phase 7 §4.2 C-3 — default case 는 string scalar 유지 (legacy 호환)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml  // 보강 0 — 기존 fixture
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.ToString()
    // 보강 0 인 경우 object 승격 없음 — 신규 키 등장 0건
    Assert.DoesNotContain("\"ref\"", json)
    Assert.DoesNotContain("\"contactKind\"", json)
    Assert.DoesNotContain("\"condition\"", json)
    // 기존 string scalar emit 유지 — calls 가 array of string
    Assert.Contains("\"calls\"", json)

// v10: SkipInputSensor (ApiCall) + ApiDefActionType (Push/Pulse/TimeTotal) 의존 Fact 들은
// v10 적용으로 *grammar / 필드 자체 deprecated* — 본 파일 내 C-4 / C-5 Fact 통째 제거.
// 신규 v10 ActionType / SensingType round-trip Fact 는 별도 작업 단위로 추가 예정.

// ─── Phase 7 §4.2 C-6 — Project meta + DsSystem.IRI + Work.TokenRole ────────

let private c6Yaml = """
protocol: promaker/v0
project: M1
author: kwak
version: "1.0.5"

systems:
  - system: Controller
    kind: active
    iri: "urn:dualsoft:ctrl1"
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          tokenRole: Source
          calls: [Cyl1.ADV]
        Ret:
          flow: Run
          tokenRole: Sink
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    iri: "urn:dualsoft:cyl1"
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-6 — Project meta + IRI + TokenRole round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store c6Yaml

    let project = Queries.allProjects store |> List.head
    Assert.Equal("kwak", project.Author)
    Assert.Equal("1.0.5", project.Version)

    let ctrl = Queries.activeSystemsOf project.Id store |> List.head
    Assert.Equal(Some "urn:dualsoft:ctrl1", ctrl.IRI)
    let cyl = Queries.passiveSystemsOf project.Id store |> List.head
    Assert.Equal(Some "urn:dualsoft:cyl1", cyl.IRI)

    let runFlow = Queries.flowsOf ctrl.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let retWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Ret")
    Assert.Equal(TokenRole.Source, advWork.TokenRole)
    Assert.Equal(TokenRole.Sink, retWork.TokenRole)

    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"author\":\"kwak\"", compact)
    Assert.Contains("\"version\":\"1.0.5\"", compact)
    Assert.Contains("\"iri\":\"urn:dualsoft:ctrl1\"", compact)
    Assert.Contains("\"iri\":\"urn:dualsoft:cyl1\"", compact)
    Assert.Contains("\"tokenRole\":\"Source\"", compact)
    Assert.Contains("\"tokenRole\":\"Sink\"", compact)

    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "C-6 round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("C-6 round-trip", plan2.Build())

    let project2 = Queries.allProjects store2 |> List.head
    Assert.Equal("kwak", project2.Author)
    Assert.Equal("1.0.5", project2.Version)
    let ctrl2 = Queries.activeSystemsOf project2.Id store2 |> List.head
    Assert.Equal(Some "urn:dualsoft:ctrl1", ctrl2.IRI)
    let runFlow2 = Queries.flowsOf ctrl2.Id store2 |> List.head
    let advWork2 = Queries.worksOf runFlow2.Id store2 |> List.find (fun w -> w.LocalName = "Adv")
    Assert.Equal(TokenRole.Source, advWork2.TokenRole)

// ─── 외부 review M-E / M-B / m-5 / m-2 반영 — round-trip / 음성 / lock-in ───

let private findAdvCall (store: DsStore) : Call =
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let f = Queries.flowsOf ctrl.Id store |> List.head
    let w = Queries.worksOf f.Id store |> List.find (fun w -> w.LocalName = "Adv")
    Queries.callsOf w.Id store |> List.head

let private nestedCallConditionYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                type: ComAux
                isInverted: true
                conditions:
                  - Cyl1.RET
                children:
                  - type: SkipAction
                    isOR: true
                    conditions:
                      - ref: Cyl1.ADV
                        contactKind: NcContact

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``외부 review M-E — nested Condition children round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store nestedCallConditionYaml
    let advCall = findAdvCall store
    let root = advCall.Conditions.[0]
    Assert.Equal(Some ConditionType.ComAux, root.Type)
    Assert.True(root.IsInverted)
    Assert.False(root.IsOR)
    Assert.Equal(1, root.ApiCalls.Count)
    Assert.Equal(1, root.Children.Count)
    let child = root.Children.[0]
    Assert.Equal(Some ConditionType.SkipAction, child.Type)
    Assert.True(child.IsOR)
    Assert.Equal(1, child.ApiCalls.Count)
    Assert.Equal(ContactKind.NcContact, child.ApiCalls.[0].ContactKind)

    // round-trip: nested children 보존 확인
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "nested round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("nested CC round-trip", plan2.Build())
    let advCall2 = findAdvCall store2
    let root2 = advCall2.Conditions.[0]
    Assert.Equal(Some ConditionType.ComAux, root2.Type)
    Assert.True(root2.IsInverted)
    Assert.Equal(1, root2.Children.Count)
    Assert.Equal(Some ConditionType.SkipAction, root2.Children.[0].Type)
    Assert.True(root2.Children.[0].IsOR)
    Assert.Equal(ContactKind.NcContact, root2.Children.[0].ApiCalls.[0].ContactKind)

[<Fact>]
let ``외부 review M-B — 빈 IOTag (Some empty) 는 emit / enhancement 모두 무시`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let advCall = findAdvCall store
    advCall.ApiCalls.[0].InTag <- Some (IOTag())  // Name="" / Address="" empty instance

    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.ToString()
    // 빈 IOTag 은 callHasEnhancement 평가에서 무시 → object 승격 없음, inTag 키 emit 0건
    Assert.DoesNotContain("\"inTag\"", json)
    Assert.DoesNotContain("\"ref\"", json)

let private emptyCallConditionYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition: {}

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``외부 review m-5 — 빈 condition object 는 None 정규화`` () =
    let store = DsStore()
    let _ = parseApplyCommit store emptyCallConditionYaml
    let advCall = findAdvCall store
    // 빈 condition: {} 는 의미 0 의 Condition 추가 회피 → Conditions 0건
    Assert.Equal(0, advCall.Conditions.Count)

[<Fact>]
let ``외부 review m-2 — 모든 default 만 있을 때 신규 키 emit 0건 (lock-in)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.ToString()
    // Phase 7 §4.2 C-1~C-6 신규 키 모두 default-skip
    Assert.DoesNotContain("\"author\"", json)
    Assert.DoesNotContain("\"version\"", json)
    Assert.DoesNotContain("\"iri\"", json)
    Assert.DoesNotContain("\"tokenRole\"", json)
    Assert.DoesNotContain("\"contactKind\"", json)
    Assert.DoesNotContain("\"skipInputSensor\"", json)
    Assert.DoesNotContain("\"inTag\"", json)
    Assert.DoesNotContain("\"outTag\"", json)
    Assert.DoesNotContain("\"callType\"", json)
    Assert.DoesNotContain("\"condition\"", json)
    Assert.DoesNotContain("\"apiDetails\"", json)

// ─── Phase 1 (Condition 리팩터링) — AutoAux 기본 타입 보정 + unknown-key + Work 정책 ────
//
// SSOT: todo-refactor-condition.md "박제 결정" / Phase 1 명세 / 남은작업 1.
// parseCondition 의 top-level Call/Work context 분리, type 생략 보정, unknown-key whitelist 검증.

/// Runtime ConditionExpression tree 안 Leaf 노드 개수 — AutoAux condition 이
/// CallAutoAuxConditions 평가 대상에 *실제로* 포함되는지 (= leaf 가 나타나는지) 측정.
/// type 보정 누락 시 Build.fs 의 `cc.Type = Some AutoAux` 필터에서 빠져 leaf 0.
let rec private countLeaves (expr: Ds2.Runtime.Engine.Core.ConditionExpression) : int =
    match expr with
    | Ds2.Runtime.Engine.Core.Const _ -> 0
    | Ds2.Runtime.Engine.Core.Leaf _ -> 1
    | Ds2.Runtime.Engine.Core.And xs
    | Ds2.Runtime.Engine.Core.Or xs -> xs |> List.sumBy countLeaves
    | Ds2.Runtime.Engine.Core.Not x -> countLeaves x

// type 키 없는 top-level call condition — Cyl1.ADV 가 Cyl1.RET 조건을 가짐.
let private autoAuxOmittedTypeYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - Cyl1.RET
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 1 — top-level call condition 의 type 생략은 Some AutoAux 로 보정`` () =
    let store = DsStore()
    let _ = parseApplyCommit store autoAuxOmittedTypeYaml
    let advCall = findAdvCall store
    Assert.Equal(1, advCall.Conditions.Count)
    // type 키 없었지만 top-level Call → Some AutoAux 보정.
    Assert.Equal(Some ConditionType.AutoAux, advCall.Conditions.[0].Type)

[<Fact>]
let ``Phase 1 — type 생략 condition export->apply 후 Runtime CallAutoAuxConditions 평가 대상 포함`` () =
    // 선행 버그 회귀 lock-in: emit 이 AutoAux type 키를 생략하므로, 보정 없으면
    // export->apply 후 Type=None 이 되어 Runtime AutoAux 평가에서 누락됨.
    let store = DsStore()
    let _ = parseApplyCommit store autoAuxOmittedTypeYaml

    // export → 새 store 에 apply (round-trip).
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("AutoAux round-trip", plan2.Build())

    // round-trip 후에도 Type=Some AutoAux 보존 (Build.fs `cc.Type = Some AutoAux` 필터 통과 조건).
    let advCall2 = findAdvCall store2
    Assert.Equal(1, advCall2.Conditions.Count)
    Assert.Equal(Some ConditionType.AutoAux, advCall2.Conditions.[0].Type)

    // Runtime SimIndex build — CallAutoAuxConditions 에 leaf 가 실제로 포함되는지 직접 검증.
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store2 10
    let autoAuxExpr = index.CallAutoAuxConditions.[advCall2.Id]
    Assert.True(countLeaves autoAuxExpr >= 1,
        sprintf "AutoAux condition 이 Runtime 평가 대상에서 누락 (leaf 0): %A" autoAuxExpr)
    // ComAux/SkipAction 평가에는 안 들어가야 함 (AutoAux 전용).
    Assert.Equal(0, countLeaves index.CallComAuxConditions.[advCall2.Id])
    Assert.Equal(0, countLeaves index.CallSkipActionConditions.[advCall2.Id])

[<Fact>]
let ``Phase 1 — legacy nested child 의 type 생략은 None 유지 (top-level 보정과 분리)`` () =
    // child 는 topLevel=false → type 생략 시 보정 없음 (None 유지). explicit child type 은 보존 (별도 M-E 테스트).
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - Cyl1.RET
                children:
                  - conditions:
                      - Cyl1.ADV

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let root = (findAdvCall store).Conditions.[0]
    // top-level 은 AutoAux 보정.
    Assert.Equal(Some ConditionType.AutoAux, root.Type)
    // child 의 type 생략 → None 유지 (legacy 호환).
    Assert.Equal(1, root.Children.Count)
    Assert.Equal<ConditionType option>(None, root.Children.[0].Type)

// ─── Phase 1 — condition object unknown-key diagnostics ─────────────────────

[<Fact>]
let ``Phase 1 — condition object 의 알 수 없는 키는 diagnostics`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                type: ComAux
                bogusKey: 1
                conditions:
                  - Cyl1.RET

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("알 수 없는 condition 키 'bogusKey'", diag.Format())

[<Fact>]
let ``Phase 1 — callCondition 키 입력은 unknown-key diagnostics (alias parse 미지원)`` () =
    // 박제 결정: canonical key 는 condition 만. callCondition alias parse 추가 금지 → call object 의
    // callCondition 키는 calls object enhancement whitelist 에 없어 unknown-key 로 거부.
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              callCondition:
                type: AutoAux
                conditions:
                  - Cyl1.RET

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("callCondition", diag.Format())

// ─── Phase 1 — Work context top-level type 정책 (AutoAux/ComAux fail) ────────

[<Fact>]
let ``Phase 1 — Work condition 의 top-level AutoAux 는 fail diagnostics`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
          condition:
            type: AutoAux
            conditions:
              - Cyl1.RET

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("Work condition 은 SkipAction 만 허용", diag.Format())

[<Fact>]
let ``Phase 1 — Work condition 의 top-level ComAux 는 fail diagnostics`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
          condition:
            type: ComAux
            conditions:
              - Cyl1.RET

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("Work condition 은 SkipAction 만 허용", diag.Format())

[<Fact>]
let ``Phase 1 — Work condition 의 top-level SkipAction 은 허용`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
          condition:
            type: SkipAction
            conditions:
              - Cyl1.RET

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let projects = Queries.allProjects store
    let ctrl = Queries.activeSystemsOf projects.Head.Id store |> List.head
    let runFlow = Queries.flowsOf ctrl.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    Assert.Equal(1, advWork.Conditions.Count)
    Assert.Equal(Some ConditionType.SkipAction, advWork.Conditions.[0].Type)

// ─── Phase 2 — Condition leaf eq / typed inputSpec (ValueSpec sugar) ─────────
//
// SSOT todo-refactor-condition.md Phase 2 / 박제 결정:
//   * leaf object `{ ref, contactKind?, eq?, inputSpec? }`.
//   * eq 값 ValueSpec case 는 대상 ApiDef 데이터 타입 metadata(참조 ApiCall InputSpec/OutputSpec)
//     기준 결정. bool/string 은 token 자체로 확정, 숫자는 metadata 필수 (없으면 diagnostics).
//   * Multiple/Ranges/bound 는 eq 로 환원 불가 → typed inputSpec(raw ValueSpec DU) fallback.

/// Cyl1.ADV / Cyl1.RET 의 ApiDef 를 참조하는 Adv Call leaf 에 eq 를 단 단일 모델.
let private eqBoolYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    eq: true
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 2 — bool eq parse: BoolValue(Single true) 매핑`` () =
    let store = DsStore()
    let _ = parseApplyCommit store eqBoolYaml
    let cond = (findAdvCall store).Conditions.[0]
    Assert.Equal(1, cond.ApiCalls.Count)
    Assert.Equal(BoolValue (Single true), cond.ApiCalls.[0].InputSpec)

[<Fact>]
let ``Phase 2 — bool eq emit: object 승격 + eq:true 출력`` () =
    let store = DsStore()
    let _ = parseApplyCommit store eqBoolYaml
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"eq\":true", compact)

[<Fact>]
let ``Phase 2 — bool eq round-trip: export->apply 후 InputSpec 보존`` () =
    let store = DsStore()
    let _ = parseApplyCommit store eqBoolYaml
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("eq bool round-trip", plan2.Build())
    let cond2 = (findAdvCall store2).Conditions.[0]
    Assert.Equal(BoolValue (Single true), cond2.ApiCalls.[0].InputSpec)

let private eqStringYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    eq: "OPEN"
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 2 — string eq parse/emit/round-trip: StringValue(Single) 보존`` () =
    let store = DsStore()
    let _ = parseApplyCommit store eqStringYaml
    let cond = (findAdvCall store).Conditions.[0]
    // metadata(hint) 없는 string → StringValue (token 자체로 타입 확정).
    Assert.Equal(StringValue (Single "OPEN"), cond.ApiCalls.[0].InputSpec)
    // emit: eq scalar string.
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"eq\":\"OPEN\"", compact)
    // round-trip.
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("eq string round-trip", plan2.Build())
    Assert.Equal(StringValue (Single "OPEN"), (findAdvCall store2).Conditions.[0].ApiCalls.[0].InputSpec)

[<Fact>]
let ``Phase 2 — 숫자 eq 는 ApiDef 타입 metadata 부재 시 diagnostics (Int32/Float64 임의 고정 금지)`` () =
    // device sugar 의 Call.ApiCalls[0].InputSpec = UndefinedValue → 숫자 eq 타입 결정 불가.
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    eq: 5

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("정수/실수 타입을 결정할 수 없습니다", diag.Format())

[<Fact>]
let ``Phase 2 — 숫자 eq 는 대상 ApiDef Int32 metadata 기준 Int32Value 매핑 (실수 case 고정 안 됨)`` () =
    // 1단계: 모델 생성. Adv Call 이 Cyl1.ADV 를 호출 → 그 Call.ApiCalls[0] 가 ADV ApiDef 참조.
    let store = DsStore()
    let _ = parseApplyCommit store eqBoolYaml  // Cyl1.ADV 호출 Call 존재 (Adv work)
    // 2단계: 그 Call.ApiCalls[0].InputSpec 을 Int32 로 직접 set → ADV ApiDef 의 타입 metadata 출처.
    let advCall = findAdvCall store
    advCall.ApiCalls.[0].InputSpec <- Int32Value (Single 0)
    // 3단계: 같은 ADV 를 condition leaf eq 로 참조하는 patch 적용 (기존 Controller 에 신규 Chk work 추가).
    //   eqBoolYaml 이 이미 Adv/Ret work 를 만들었으므로 동명 중복(D1) 을 피해 신규 work 이름 Chk 사용.
    //   eq 숫자 token 5 → hint=Int32 → Int32Value(Single 5) 매핑 (Float64 로 고정되지 않음).
    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - in: Controller
      works:
          Chk:
            flow: Run
            calls:
              - ref: Cyl1.RET
                condition:
                  conditions:
                    - ref: Cyl1.ADV
                      eq: 5
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "patch diag: %s" (diag.Format()))
    store.ApplyImportPlan("eq int metadata", plan.Build())
    // Chk work 의 Call condition leaf 의 InputSpec 확인.
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let f = Queries.flowsOf ctrl.Id store |> List.head
    let chkWork = Queries.worksOf f.Id store |> List.find (fun w -> w.LocalName = "Chk")
    let chkCall = Queries.callsOf chkWork.Id store |> List.head
    Assert.Equal(1, chkCall.Conditions.Count)
    Assert.Equal(Int32Value (Single 5), chkCall.Conditions.[0].ApiCalls.[0].InputSpec)

[<Fact>]
let ``Phase 2 — typed inputSpec fallback: Multiple parse/emit/round-trip`` () =
    // eq 로 표현 못하는 Multiple → raw ValueSpec DU inputSpec.
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    inputSpec:
                      Case: Int32Value
                      Fields:
                        - Case: Multiple
                          Fields:
                            - [ 1, 2, 3 ]

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let cond = (findAdvCall store).Conditions.[0]
    Assert.Equal(Int32Value (Multiple [ 1; 2; 3 ]), cond.ApiCalls.[0].InputSpec)
    // emit: Multiple 은 eq 환원 불가 → inputSpec raw DU.
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"inputSpec\"", compact)
    Assert.Contains("\"Multiple\"", compact)
    Assert.DoesNotContain("\"eq\"", compact)
    // round-trip.
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("inputSpec Multiple round-trip", plan2.Build())
    Assert.Equal(Int32Value (Multiple [ 1; 2; 3 ]), (findAdvCall store2).Conditions.[0].ApiCalls.[0].InputSpec)

[<Fact>]
let ``Phase 2 — typed inputSpec fallback: Ranges round-trip`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    inputSpec:
                      Case: Int32Value
                      Fields:
                        - Case: Ranges
                          Fields:
                            - - Lower: [ 1, { Case: Closed } ]
                                Upper: [ 10, { Case: Open } ]

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let cond = (findAdvCall store).Conditions.[0]
    let expected = Int32Value (Ranges [ { Lower = Some (1, Closed); Upper = Some (10, Open) } ])
    Assert.Equal(expected, cond.ApiCalls.[0].InputSpec)
    // round-trip.
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("inputSpec Ranges round-trip", plan2.Build())
    Assert.Equal(expected, (findAdvCall store2).Conditions.[0].ApiCalls.[0].InputSpec)

[<Fact>]
let ``Phase 2 — eq 와 inputSpec 동시 지정은 diagnostics (혼용 금지)`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    eq: true
                    inputSpec:
                      Case: BoolValue
                      Fields:
                        - Case: Single
                          Fields:
                            - true

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("동시 지정 불가", diag.Format())

[<Fact>]
let ``Phase 2 — condition leaf 의 알 수 없는 키는 diagnostics`` () =
    let yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                conditions:
                  - ref: Cyl1.RET
                    bogusLeafKey: 1

  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("알 수 없는 condition leaf 키 'bogusLeafKey'", diag.Format())

[<Fact>]
let ``Phase 2 — eq 없는 leaf string/object 회귀: InputSpec UndefinedValue 유지`` () =
    // 기존 leaf string/object (eq 미지정) 회귀 — InputSpec 은 default UndefinedValue, emit 시 eq/inputSpec 미등장.
    let store = DsStore()
    let _ = parseApplyCommit store conditionYaml  // contactKind 만 보강, eq 없음
    let advCall = findAdvCall store
    let cond = advCall.Conditions.[0]
    for ac in cond.ApiCalls do
        Assert.Equal(UndefinedValue, ac.InputSpec)
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.DoesNotContain("\"eq\"", compact)
    Assert.DoesNotContain("\"inputSpec\"", compact)

// ─── 외부 review M-F — shape 위반 7분기 진단 발행 (Phase 7 §4.2 후속) ───

/// 진단 발행 확인 helper — yaml 적용 시 에러 진단이 *expected substring* 을 포함하는지 검증.
/// SSOT §2.7 룰 #16/#21/#22/#23/#24 의 silent skip 금지 정책 정합 (외부 reviewer M-F).
let private assertDiagContains (yaml: string) (expected: string) : unit =
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors, sprintf "에러 진단 발행 기대 (expected=%s, yaml=%s)" expected yaml)
    let formatted = diag.Format()
    Assert.True(formatted.Contains(expected), sprintf "기대 substring '%s' 미포함. 실제 진단:\n%s" expected formatted)

let private tokenRoleNonStringYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          tokenRole: 123
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private inTagNonObjectYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              inTag: "should-be-object"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private skipInputSensorNonBoolYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              skipInputSensor: "yes"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private apiDetailsNonObjectYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
    apiDetails: "should-be-object"
"""

let private apiDetailsUnknownApiYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
    apiDetails:
      NoSuchApi:
        actionType: pulse
"""

let private conditionApiCallsNonArrayYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                type: ComAux
                conditions: "should-be-array"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private conditionChildrenNonArrayYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                type: ComAux
                children: "should-be-array"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

// todo §10.2 #4 — outTag non-object (inTagNonObject 대칭 분기).
let private outTagNonObjectYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              outTag: "should-be-object"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

/// Phase 7 외부 review M-F — shape 위반 진단 8분기. 각 case 가 별도 테스트 ID 를 갖도록
/// `[<Theory>] + [<InlineData>]` 로 분리 (todo §10.2 #10 — 1 실패 시 나머지 결과 가시성 ↑).
/// 케이스 이름은 컴파일 타임 상수 만 가능하므로 `tag` 로 매핑 dispatch.
[<Theory>]
[<InlineData("tokenRoleNonString",          "string 기대")>]              // SSOT §2.7 룰 #23
[<InlineData("inTagNonObject",              "IOTag object 기대")>]         // SSOT §2.7 룰 #22
[<InlineData("outTagNonObject",             "IOTag object 기대")>]         // SSOT §2.7 룰 #22 (todo §10.2 #4)
// v10: skipInputSensor 키 자체 폐기 (SensingType=Virtual 흡수) — bool 위반 진단 무의미 → case 제거.
[<InlineData("apiDetailsNonObject",         "object 기대")>]              // SSOT §2.7 룰 #24
[<InlineData("apiDetailsUnknownApi",        "system 의 apis 목록에 없음")>] // SSOT §2.7 룰 #18 (M-C)
[<InlineData("conditionApiCallsNonArray", "array 기대")>]           // SSOT §2.7 룰 #16 (M-F)
[<InlineData("conditionChildrenNonArray",   "array 기대")>]           // SSOT §2.7 룰 #16 (M-F)
let ``Phase 7 외부 review M-F — shape 위반 진단 발행`` (tag: string) (expectedSubstr: string) =
    let yaml =
        match tag with
        | "tokenRoleNonString"              -> tokenRoleNonStringYaml
        | "inTagNonObject"                  -> inTagNonObjectYaml
        | "outTagNonObject"                 -> outTagNonObjectYaml
        | "apiDetailsNonObject"             -> apiDetailsNonObjectYaml
        | "apiDetailsUnknownApi"            -> apiDetailsUnknownApiYaml
        | "conditionApiCallsNonArray" -> conditionApiCallsNonArrayYaml
        | "conditionChildrenNonArray"   -> conditionChildrenNonArrayYaml
        | _ -> failwithf "unknown tag '%s'" tag
    assertDiagContains yaml expectedSubstr

// ─── todo §10.2 #9 — enum parser-error / IRI non-string negative test ─────────
//
// `applyEnumProp` Error 분기 + `applyStringProp` non-string 분기의 회귀 보호.
// SSOT §2.7 룰 #19 (enum 라벨 위반) / #20 (ApiDefActionType grammar) / #11 (iri 등 leaf string 키).

let private tokenRoleInvalidLabelYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          tokenRole: NoSuchRole
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private apiDefActionTypeInvalidYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
    apiDetails:
      ADV:
        actionType: NoSuchType
"""

let private iriNonStringYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    iri: 42
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

// todo §10.2 #4 — 추가 enum 라벨 위반 3건 (parseConditionType / parseContactKind / parseCallType).
let private conditionTypeInvalidLabelYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              condition:
                type: NoSuchType
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private contactKindInvalidLabelYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              contactKind: NoSuchKind
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private callTypeInvalidLabelYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              callType: NoSuchCallType
  - system: Cyl1
    kind: passive
    device: cylinder
"""

/// todo §10.2 #9/#4 — enum parser Error 분기 / IRI non-string 회귀 보호.
/// 각 case 가 별도 테스트 ID 를 갖도록 `[<Theory>] + [<InlineData>]` 분리.
[<Theory>]
[<InlineData("tokenRoleInvalidLabel",         "tokenRole 'NoSuchRole' 미지원")>]                // SSOT §2.7 룰 #19
[<InlineData("apiDefActionTypeInvalid",       "case 이름과 인자 개수 불일치")>]                  // SSOT §2.7 룰 #20
[<InlineData("iriNonString",                  "string 기대")>]                                  // applyStringProp non-string
[<InlineData("conditionTypeInvalidLabel", "condition type 'NoSuchType' 미지원")>]       // SSOT §2.7 룰 #19 (todo §10.2 #4)
[<InlineData("contactKindInvalidLabel",       "contactKind 'NoSuchKind' 미지원")>]              // SSOT §2.7 룰 #19 (todo §10.2 #4)
[<InlineData("callTypeInvalidLabel",          "callType 'NoSuchCallType' 미지원")>]             // SSOT §2.7 룰 #19 (todo §10.2 #4)
let ``todo §10.2 #9 — parser-error / non-string 진단 발행`` (tag: string) (expectedSubstr: string) =
    let yaml =
        match tag with
        | "tokenRoleInvalidLabel"         -> tokenRoleInvalidLabelYaml
        | "apiDefActionTypeInvalid"       -> apiDefActionTypeInvalidYaml
        | "iriNonString"                  -> iriNonStringYaml
        | "conditionTypeInvalidLabel" -> conditionTypeInvalidLabelYaml
        | "contactKindInvalidLabel"       -> contactKindInvalidLabelYaml
        | "callTypeInvalidLabel"          -> callTypeInvalidLabelYaml
        | _ -> failwithf "unknown tag '%s'" tag
    assertDiagContains yaml expectedSubstr

// ─── Phase 7 §4.2 C-7.1 — PLC metadata round-trip + negative test ───────────

let private plcSystemYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    plc:
      plcVendor: Siemens
      plcIpAddress: 10.0.0.42
      plcPort: 5020
      communicationTimeout: 00:00:10
      retryAttempts: 5
      enableSafetyInterlock: false
      safetyTimeoutSeconds: 45.5
      tagPrefix: PLC1_
      systemType: Unit
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-7.1 — ControlSystemProperties round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store plcSystemYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let cp = ctrl.GetControlProperties() |> Option.get
    // 적용 확인
    Assert.Equal("Siemens", cp.PlcVendor)
    Assert.Equal("10.0.0.42", cp.PlcIpAddress)
    Assert.Equal(5020, cp.PlcPort)
    Assert.Equal(System.TimeSpan.FromSeconds(10.0), cp.CommunicationTimeout)
    Assert.Equal(5, cp.RetryAttempts)
    Assert.False(cp.EnableSafetyInterlock)
    Assert.Equal(45.5, cp.SafetyTimeoutSeconds)
    Assert.Equal(Some "PLC1_", cp.TagPrefix)
    Assert.Equal(Some "Unit", cp.SystemType)
    // round-trip 의미-동등
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)

let private plcFlowYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run:
        plc:
          flowControlEnabled: true
          flowPriority: 7
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-7.1 — ControlFlowProperties round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store plcFlowYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let f = Queries.flowsOf ctrl.Id store |> List.head
    let cp = f.GetControlProperties() |> Option.get
    Assert.True(cp.FlowControlEnabled)
    Assert.Equal(7, cp.FlowPriority)
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)

let private plcWorkYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          plc:
            enableHardwareControl: true
            controlMode: Parallel
            workTimeout: 00:00:05
            enableTimeout: true
            timeoutAction: Retry
            enableMotionControl: true
            targetPosition: 100.5
            acceleration: 2.5
            usePulseControl: true
            pulseWidthMs: 50
            pulseCount: 10
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-7.1 — ControlWorkProperties round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store plcWorkYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let f = Queries.flowsOf ctrl.Id store |> List.head
    let w = Queries.worksOf f.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let cp = w.GetControlProperties() |> Option.get
    Assert.True(cp.EnableHardwareControl)
    Assert.Equal("Parallel", cp.ControlMode)
    Assert.Equal(Some (System.TimeSpan.FromSeconds(5.0)), cp.WorkTimeout)
    Assert.True(cp.EnableTimeout)
    Assert.Equal("Retry", cp.TimeoutAction)
    Assert.True(cp.EnableMotionControl)
    Assert.Equal(Some 100.5, cp.TargetPosition)
    Assert.Equal(Some 2.5, cp.Acceleration)
    Assert.True(cp.UsePulseControl)
    Assert.Equal(Some 50, cp.PulseWidthMs)
    Assert.Equal(Some 10, cp.PulseCount)
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)

let private plcCallYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              plc:
                enableRetry: true
                maxRetryCount: 5
                retryDelayMs: 2000
                callTimeout: 00:00:03
                waitForCompletion: false
                enableConditional: true
                conditionExpression: "sensor1 AND NOT sensor2"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-7.1 — ControlCallProperties round-trip + dual format`` () =
    let store = DsStore()
    let _ = parseApplyCommit store plcCallYaml
    let call = findAdvCall store
    let cp = call.GetControlProperties() |> Option.get
    Assert.True(cp.EnableRetry)
    Assert.Equal(5, cp.MaxRetryCount)
    Assert.Equal(2000, cp.RetryDelayMs)
    Assert.Equal(Some (System.TimeSpan.FromSeconds(3.0)), cp.CallTimeout)
    Assert.False(cp.WaitForCompletion)
    Assert.True(cp.EnableConditional)
    Assert.Equal(Some "sensor1 AND NOT sensor2", cp.ConditionExpression)
    // emit 측이 dual format object 승격 — `"ref":` 와 `"plc":` 키 모두 등장.
    use json = ModelProtocol.exportToJson store
    let compact = json.RootElement.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", "")
    Assert.Contains("\"ref\":\"Cyl1.ADV\"", compact)
    Assert.Contains("\"plc\":{", compact)
    Assert.Contains("\"enableRetry\":true", compact)
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)

/// 모든 leaf default 시 plc 키 emit 0건 — silent emit drift 차단.
[<Fact>]
let ``Phase 7 §4.2 C-7.1 — all-default 시 plc 키 emit 0건`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    use json = ModelProtocol.exportToJson store
    let raw = json.RootElement.GetRawText()
    Assert.DoesNotContain("\"plc\":", raw)

// negative — plc shape 위반 진단 발행 검증

let private plcNonObjectYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    plc: "not-object"
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

/// #24 (m3 외부 review) — System 차원 `plc:` 안 단일 키 negative theory case 의 공통 frame 압축.
/// Controller(active) + Cyl1(passive cylinder) + Adv→Cyl1.ADV 호출 골조 hardcode. fixture 별
/// 차이는 `plc:` 안 한 줄 키/값뿐 — `plcKeyValue` 인자로만 전달. 6 fixture × ~14 line 중복 제거.
/// 비교 대상이 *키 자체* 임을 더 또렷이 가시화 (frame 잡음 제거).
/// sprintf 대신 Replace 사용 — yaml 안 임의 character 의 `%X` placeholder 오인 회피.
let private plcSystemNegFrame = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    plc:
      __KEY__
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private mkPlcSystemNegYaml (plcKeyValue: string) : string =
    plcSystemNegFrame.Replace("__KEY__", plcKeyValue)

let private plcUnknownKeyYaml      = mkPlcSystemNegYaml "noSuchKey: 42"
let private plcPortNonIntYaml      = mkPlcSystemNegYaml "plcPort: not-a-number"
let private plcTimeSpanInvalidYaml = mkPlcSystemNegYaml "communicationTimeout: not-a-timespan"

let private plcRuntimeKeyYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          plc:
            currentState: Running
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

// #19: bool / float / string|null type 위반 추가 (Theory case 3건). #24 frame helper 활용.
let private plcEnableSafetyNonBoolYaml      = mkPlcSystemNegYaml "enableSafetyInterlock: not-a-bool"
let private plcSafetyTimeoutNonFloatYaml    = mkPlcSystemNegYaml "safetyTimeoutSeconds: not-a-float"
let private plcTagPrefixNonStringOrNullYaml = mkPlcSystemNegYaml "tagPrefix: 42"

[<Theory>]
[<InlineData("plcNonObject",     "plc: Object 기대")>]                          // §2.7 룰 #25
[<InlineData("plcUnknownKey",    "알 수 없는 plc 키 'noSuchKey'")>]              // §2.7 룰 #26
[<InlineData("plcPortNonInt",    "int 기대")>]                                  // §2.7 룰 #27
[<InlineData("plcTimeSpanInvalid", "TimeSpan 형식 위반")>]                       // §2.7 룰 #28
[<InlineData("plcRuntimeKey",    "알 수 없는 plc 키 'currentState'")>]           // 派 분류 강제 (§2.7 룰 #26)
[<InlineData("plcEnableSafetyNonBool", "bool 기대")>]                           // #19 — bool type 위반
[<InlineData("plcSafetyTimeoutNonFloat", "float 기대")>]                         // #19 — float type 위반
[<InlineData("plcTagPrefixNonStringOrNull", "string | null 기대")>]              // #19 — string|null type 위반
let ``Phase 7 §4.2 C-7.1 — plc shape / type / runtime 진단 발행`` (tag: string) (expectedSubstr: string) =
    let yaml =
        match tag with
        | "plcNonObject"               -> plcNonObjectYaml
        | "plcUnknownKey"              -> plcUnknownKeyYaml
        | "plcPortNonInt"              -> plcPortNonIntYaml
        | "plcTimeSpanInvalid"         -> plcTimeSpanInvalidYaml
        | "plcRuntimeKey"              -> plcRuntimeKeyYaml
        | "plcEnableSafetyNonBool"     -> plcEnableSafetyNonBoolYaml
        | "plcSafetyTimeoutNonFloat"   -> plcSafetyTimeoutNonFloatYaml
        | "plcTagPrefixNonStringOrNull" -> plcTagPrefixNonStringOrNullYaml
        | _ -> failwithf "unknown tag '%s'" tag
    assertDiagContains yaml expectedSubstr

// ─── #17 — Passive system plc round-trip 직접 테스트 (C-7.1 follow-up) ──────

let private plcPassiveSystemYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
    plc:
      plcVendor: Mitsubishi
      plcIpAddress: 10.0.0.99
      systemType: Actuator
      tagPrefix: AUX_
"""

[<Fact>]
let ``#17 — Passive system plc round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store plcPassiveSystemYaml
    let proj = (Queries.allProjects store).Head
    let cyl = Queries.passiveSystemsOf proj.Id store |> List.find (fun s -> s.Name = "Cyl1")
    let cp = cyl.GetControlProperties() |> Option.get
    Assert.Equal("Mitsubishi", cp.PlcVendor)
    Assert.Equal("10.0.0.99", cp.PlcIpAddress)
    Assert.Equal(Some "Actuator", cp.SystemType)
    Assert.Equal(Some "AUX_", cp.TagPrefix)
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)

// ─── #18 — string|null leaf `Some ""` vs `None` 명시 round-trip (C-7.1) ────

/// `Some ""` 와 `None` 의 명시 round-trip — capturer `optStr` 가 `""` ↔ `"<null>"` 구별.
/// readStringOptKey 가 `""` → Some "" / null → None / 부재 → 부재. emit 측 writeStringOpt 는
/// `Some _, when cur <> defv -> emit` (None default → `Some ""` 도 emit `""`). 양방향 정합.
[<Fact>]
let ``#18 — string|null leaf Some "" vs None 명시 round-trip`` () =
    let yamlSomeEmpty = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    plc:
      tagPrefix: ""
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yamlSomeEmpty
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let cp = ctrl.GetControlProperties() |> Option.get
    // apply 결과 `Some ""` 보존 (PoC — readStringOptKey 정규화 없음)
    Assert.Equal(Some "", cp.TagPrefix)
    // round-trip — emit 측이 `Some ""` 를 `tagPrefix: ""` 로 다시 emit
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)
    // 비교 대조군: `tagPrefix` 키 부재 → entity default None → emit 시 skip
    let yamlNone = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store2 = DsStore()
    let _ = parseApplyCommit store2 yamlNone
    let proj2 = (Queries.allProjects store2).Head
    let ctrl2 = Queries.activeSystemsOf proj2.Id store2 |> List.head
    // ControlSystemProperties instance 자체가 생성 안 됨 (모든 plc 키 default) → None
    Assert.Equal(None, ctrl2.GetControlProperties() |> Option.bind (fun p -> p.TagPrefix))
    let a2, b2 = ModelEquivalence.roundTripShape store2
    Assert.Equal<ModelEquivalence.StoreShape>(a2, b2)

// ─── M1 외부 review — applyNonEmptyStringProp IRI 정책 (3 case) ─────────────

/// IRI 의 `iri: ""` / `iri: null` / 키 부재 3 case 모두 IRI = None 으로 normalize 되는지 검증.
/// applyNonEmptyStringProp 정책: wire 정규화 — null/빈 string/부재 모두 setter 미 호출 → entity default 유지.
let private iriEmptyStringYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    iri: ""
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private iriNullYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    iri: null
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

let private iriAbsentYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Theory>]
[<InlineData("iriEmptyString")>]   // iri: "" → None (wire 정규화)
[<InlineData("iriNull")>]          // iri: null → None (reset semantic)
[<InlineData("iriAbsent")>]        // 키 부재 → None (entity default)
let ``M1 외부 review — IRI 3 case 모두 None 으로 normalize`` (tag: string) =
    let yaml =
        match tag with
        | "iriEmptyString" -> iriEmptyStringYaml
        | "iriNull"        -> iriNullYaml
        | "iriAbsent"      -> iriAbsentYaml
        | _ -> failwithf "unknown tag '%s'" tag
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    // 3 case 모두 IRI = None
    Assert.Equal(None, ctrl.IRI)
    // round-trip — emit 측이 IRI = None 이면 키 미 발행 → re-apply 결과도 None
    let a, b = ModelEquivalence.roundTripShape store
    Assert.Equal<ModelEquivalence.StoreShape>(a, b)
    // emit 결과에 "iri" 키 부재 검증 (silent drift 차단)
    use json = ModelProtocol.exportToJson store
    let raw = json.RootElement.GetRawText()
    Assert.DoesNotContain("\"iri\"", raw)

// ─── Phase 7 §10.2 #31 (S4) — modeling level emit / apply round-trip ──────
//
// **scope**: Read level + modeling-level patch merge SSOT 의 round-trip 검증.
// - 의미: modeling export → wire 에 A_Modeling 만 등장 (B/C/D + workDuration + apiDetails.description 생략).
// - 의미: modeling apply → 기존 store entity reuse + missing 키 = no-op (B/C/D 보존, silent destructive 차단).
// - 의미: B/C/D 키 wire 등장 시 사전 거부 (룰 #30) / level: <other> 사전 거부 (룰 #29).
// - 의미: view: partial 거부 우선 검사 (C2) — partial + modeling 조합 시 view 메시지 우선.

let private enhancedYaml = """
protocol: promaker/v0
project: M1
author: kwak
version: 2.0.0
systems:
  - system: Controller
    kind: active
    iri: http://example.com/controller
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          tokenRole: Source
          workDuration: 250ms
          calls:
            - ref: Cyl1.ADV
              contactKind: NcContact
              callType: SkipIfCompleted
              inTag: { name: ADV_LMT, address: '%X10' }
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start
  - system: Cyl1
    kind: passive
    device: cylinder
    iri: http://example.com/cyl1
    apiDetails:
      ADV:
        actionType: set
        description: cylinder advance
"""

[<Fact>]
let ``#31 S4-T1 — modeling export 시 A_Modeling 만 emit (B/C/D + workDuration + description 생략)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml

    use json = ModelProtocol.exportToJsonWithLevel store ModelingCategory.Modeling
    let raw = json.RootElement.GetRawText()

    // level: modeling 키 명시 (self-tagged)
    Assert.Contains("\"level\":\"modeling\"", raw)

    // B_Addressing 키 부재
    Assert.DoesNotContain("\"inTag\"", raw)
    Assert.DoesNotContain("\"outTag\"", raw)

    // C_Meta 키 부재 (author / version / iri / description / workDuration)
    Assert.DoesNotContain("\"author\"", raw)
    Assert.DoesNotContain("\"version\"", raw)
    Assert.DoesNotContain("\"iri\"", raw)
    Assert.DoesNotContain("\"workDuration\"", raw)
    Assert.DoesNotContain("\"description\"", raw)

    // A_Modeling 키 등장 검증
    Assert.Contains("\"tokenRole\":\"Source\"", raw)
    Assert.Contains("\"contactKind\":\"NcContact\"", raw)
    Assert.Contains("\"callType\":\"SkipIfCompleted\"", raw)
    Assert.Contains("\"actionType\":\"set\"", raw)

[<Fact>]
let ``#31 S4-T2 — modeling export 의 callHasEnhancement 분기 (B/C/D-only Call 은 string scalar 유지)`` () =
    // Cyl1.RET 은 보강 0 → string scalar. Cyl1.ADV 는 A_Modeling 보강 (contactKind/callType) → object.
    // wire 에 modeling 시 inTag/outTag/plc 만 있는 Call 이 있다면 string scalar 로 정상 fallback 검증.
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              inTag: { name: ADV_LMT, address: '%X10' }   # B_Addressing 만 — modeling 시 emit 0
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    use json = ModelProtocol.exportToJsonWithLevel store ModelingCategory.Modeling
    let raw = json.RootElement.GetRawText()
    // modeling 시 Adv 의 Call 도 string scalar (보강 키가 B 만이라 modeling 에선 enhancement 0)
    Assert.Contains("\"Cyl1.ADV\"", raw)
    Assert.DoesNotContain("\"inTag\"", raw)
    // ref: 키도 없어야 — object 승격 없음
    Assert.DoesNotContain("\"ref\"", raw)

[<Fact>]
let ``#31 S4-T3 — modeling apply silent destructive 차단 (기존 B/C/D 보존)`` () =
    // 1. Full apply 로 enhanced store 생성 (B/C/D 모두 set)
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    Assert.Equal("kwak", proj.Author)
    Assert.Equal("2.0.0", proj.Version)
    Assert.Equal(Some "http://example.com/controller", ctrl.IRI)

    // 2. modeling export → modeling wire
    use modelingDoc = ModelProtocol.exportToJsonWithLevel store ModelingCategory.Modeling
    let modelingWire = ModelProtocolYaml.jsonElementToYaml modelingDoc.RootElement

    // 3. modeling wire 를 *기존 store* 에 다시 apply — silent destructive 가 일어나는지 확인
    let _ = parseApplyCommit store modelingWire

    // 4. B/C/D 보존 검증 — silent destructive 안 일어났어야 함
    let projAfter = (Queries.allProjects store).Head
    let ctrlAfter = Queries.activeSystemsOf projAfter.Id store |> List.head
    Assert.Equal("kwak", projAfter.Author)
    Assert.Equal("2.0.0", projAfter.Version)
    Assert.Equal(Some "http://example.com/controller", ctrlAfter.IRI)

[<Fact>]
let ``#31 S4-T4 — modeling wire 의 B/C/D 키 등장 시 사전 거부 (룰 #30)`` () =
    let yaml = """
protocol: promaker/v0
level: modeling
project: M1
author: kwak                    # C_Meta — modeling 에서 등장 금지
systems:
  - system: Controller
    kind: active
    iri: http://example.com     # C_Meta — modeling 에서 등장 금지
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls: [Cyl1.ADV]
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors, "B/C/D 키 등장 시 diag 발행 기대")
    let msg = diag.Format()
    Assert.Contains("author", msg)
    Assert.Contains("iri", msg)
    Assert.Contains("level: modeling", msg)

[<Fact>]
let ``#31 S4-T5 — level 키 unknown 값 사전 거부 (룰 #29)`` () =
    let yaml = """
protocol: promaker/v0
level: bogus
project: M1
systems: []
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors, "unknown level 값 거부 기대")
    let msg = diag.Format()
    Assert.Contains("'bogus'", msg)
    Assert.Contains("'full' 또는 'modeling'", msg)

[<Fact>]
let ``#31 S4-T6 — view: partial + level: modeling 조합 시 view 거부 우선 (C2)`` () =
    let yaml = """
protocol: promaker/v0
view: partial
level: modeling
project: M1
systems: []
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    let msg = diag.Format()
    // partial 거부 메시지가 먼저 (룰 #7) — level 검사 skip 으로 메시지 중복 회피
    Assert.Contains("partial export 결과는 view-only", msg)

[<Fact>]
let ``#31 S4-T7 — exportToJson (Full default) 시 level 키 부재 (회귀 가드)`` () =
    // 기존 호출처 (exportToJson wrapper) 가 Full delegate — wire payload 에 level 키 부재 (legacy 호환)
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    use json = ModelProtocol.exportToJson store
    let raw = json.RootElement.GetRawText()
    Assert.DoesNotContain("\"level\"", raw)
    // 기존 B/C/D 키는 정상 emit (Full level)
    Assert.Contains("\"author\":\"kwak\"", raw)
    Assert.Contains("\"iri\":\"http://example.com/controller\"", raw)

[<Fact>]
let ``#31 S4-T8 — modeling round-trip 멱등성 (modeling → apply → modeling export 동일)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    use modelingDoc1 = ModelProtocol.exportToJsonWithLevel store ModelingCategory.Modeling
    let raw1 = modelingDoc1.RootElement.GetRawText()
    let modelingWire = ModelProtocolYaml.jsonElementToYaml modelingDoc1.RootElement
    // re-apply
    let _ = parseApplyCommit store modelingWire
    use modelingDoc2 = ModelProtocol.exportToJsonWithLevel store ModelingCategory.Modeling
    let raw2 = modelingDoc2.RootElement.GetRawText()
    // modeling wire 의 A 키 등장 동등 (멱등성 — 2번 apply 후에도 export 결과 같음)
    Assert.Equal(raw1, raw2)

[<Fact>]
let ``#31 S4-T9 — modeling 시 entity kind 변경 금지 (Passive → Active wire) 진단`` () =
    // 1. 빈 store 에 enhancedYaml 로 base (Cyl1=passive cylinder)
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    // 2. modeling wire 에 Cyl1 을 active 로 명시 (kind 변경 시도) → diag
    let badYaml = """
protocol: promaker/v0
level: modeling
project: M1
systems:
  - system: Cyl1
    kind: active
    flows:
      Run: {}
    works:
        X:
          flow: Run
          calls: []
"""
    let diag, _, _ = parseAndApply store badYaml
    Assert.True(diag.HasErrors, "kind 변경 시 diag 발행 기대")
    let msg = diag.Format()
    Assert.Contains("Cyl1", msg)
    Assert.Contains("entity kind 변경 금지", msg)

[<Fact>]
let ``#31 S4-T10 — modeling apply 로 apiDetails.actionType 변경 (A_Modeling round-trip)`` () =
    // 1. enhancedYaml 로 base — Cyl1 의 ADV.actionType = set (v10 grammar)
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    let proj = (Queries.allProjects store).Head
    let cyl1 = Queries.passiveSystemsOf proj.Id store |> List.find (fun s -> s.Name = "Cyl1")
    let adv = Queries.apiDefsOf cyl1.Id store |> List.find (fun d -> d.Name = "ADV")
    Assert.Equal(ActionType.Real (Latched, None), adv.ActionType)

    // 2. modeling wire 로 actionType: set → pulse 변경 (v10 grammar)
    let mutationYaml = """
protocol: promaker/v0
level: modeling
project: M1
systems:
  - system: Cyl1
    kind: passive
    device: cylinder
    apiDetails:
      ADV:
        actionType: pulse
"""
    let _ = parseApplyCommit store mutationYaml
    // 3. store 의 ADV.ActionType 가 Real(OneShot, None) 로 변경 검증
    let advAfter = Queries.apiDefsOf cyl1.Id store |> List.find (fun d -> d.Name = "ADV")
    Assert.Equal(ActionType.Real (OneShot, None), advAfter.ActionType)

[<Fact>]
let ``#31 TC-3 — enhancedYaml 전수 property full-level round-trip (ModelEquivalence)`` () =
    // 보강 항목 (author / version / iri / tokenRole / workDuration / contactKind / callType /
    // inTag / apiDetails.actionType / apiDetails.description) 모두 default 와 다른 값 세팅.
    // exportToJson → apply → captureShape 양쪽 동등 검증.
    // **TC-3 의 핵심 의도** (todo §4.3): genSampleStore + roundTripShape 가 lossy 4-set 외 모든
    // mutable property 를 cover — 본 phase 의 보강 키가 captureShape 에 반영되므로 round-trip 누락 자동 발견.
    let store = DsStore()
    let _ = parseApplyCommit store enhancedYaml
    let a, b = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff a b
    Assert.True(diffs.IsEmpty, sprintf "round-trip shape mismatch: %A" diffs)

[<Fact>]
let ``#31 TC-4 — modeling 키 부재 wire 는 full level 의 정상 round-trip 통과 (legacy 호환)`` () =
    // Phase 7 의 modeling level 도입이 *modeling 키 없는 legacy wire* 의 동작을 변경하지 않음을 보장.
    // singleCylinderYaml (Phase 7 보강 키 0건) apply → 정상 commit + roundTripShape 동등.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let a, b = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff a b
    Assert.True(diffs.IsEmpty, sprintf "legacy shape mismatch: %A" diffs)
    // emit wire 에 Phase 7 의 *기본값* 으로 등장하는 키 (level 자체) 부재 검증
    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    Assert.DoesNotContain("\"level\"", raw)

[<Fact>]
let ``#32 — modeling level cross-system call resolve (plan-only ApiDef reuse 시나리오)`` () =
    // **#32 reproducer (사용자 review 권장)**: 빈 store + modeling level wire 한 안에서
    // Passive (Cyl1) 와 Active (Controller) 동시 정의 + Controller.work.calls 가 Cyl1.ADV 참조.
    // 처리 순서: buildSystems → Cyl1 cascade (plan 에 ApiDef add) → buildActiveFlows → Controller
    // work.calls resolve → resolveApiDef 가 ctx.Systems."Cyl1".ApiDefIds 에서 ADV.Id lookup.
    // 본 시나리오는 plan-by-name fallback 의 직접 hit 은 아니나 (entry.ApiDefIds 가 누적된 결과 사용),
    // modeling level 의 cross-system call resolve 정합성을 보장 — #32 변경 후에도 정상 동작.
    let yaml = """
protocol: promaker/v0
level: modeling
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          calls:
            - ref: Cyl1.ADV
              contactKind: NcContact
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store yaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let cyl1 = Queries.passiveSystemsOf proj.Id store |> List.head
    let run = Queries.flowsOf ctrl.Id store |> List.head
    let adv = Queries.worksOf run.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let advCall = Queries.callsOf adv.Id store |> List.head
    let cyl1Adv = Queries.apiDefsOf cyl1.Id store |> List.find (fun d -> d.Name = "ADV")
    // Call 의 ApiCalls[0].ApiDefId 가 Cyl1 의 ADV ApiDef Id 와 일치 (cross-system resolve 성공)
    Assert.True(advCall.ApiCalls.Count > 0, "Adv Call 의 ApiCalls 비어있음")
    Assert.Equal(Some cyl1Adv.Id, advCall.ApiCalls.[0].ApiDefId)
    // A_Modeling 보강 (contactKind) 도 적용
    Assert.Equal(ContactKind.NcContact, advCall.ApiCalls.[0].ContactKind)

[<Fact>]
let ``#31 TC-5 — singleCylinderYaml fixture emit 에 Phase 7 신규 키 부재 (silent emit drift 차단)`` () =
    // 기존 legacy fixture (Phase 7 보강 키 0건) 가 round-trip 후에도 *새 키 emit 0* 보장.
    // 보강 키 누군가가 default 변경 시 본 테스트 즉시 실패 — silent drift 차단 회귀 가드.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    use exported = ModelProtocol.exportToJson store
    let raw = exported.RootElement.GetRawText()
    // Phase 7 §10.2 #31 신규 키 부재
    Assert.DoesNotContain("\"level\"", raw)
    // Phase 7 §4.2 C-3~C-6 신규 키 부재 — singleCylinderYaml 에서 보강 0 이므로 default-skip 정책 정합
    Assert.DoesNotContain("\"tokenRole\"", raw)
    Assert.DoesNotContain("\"contactKind\"", raw)
    Assert.DoesNotContain("\"skipInputSensor\"", raw)
    Assert.DoesNotContain("\"callType\"", raw)
    Assert.DoesNotContain("\"condition\"", raw)
    Assert.DoesNotContain("\"inTag\"", raw)
    Assert.DoesNotContain("\"outTag\"", raw)
    Assert.DoesNotContain("\"author\"", raw)
    Assert.DoesNotContain("\"version\"", raw)
    Assert.DoesNotContain("\"iri\"", raw)
    Assert.DoesNotContain("\"apiDetails\"", raw)
    Assert.DoesNotContain("\"plc\"", raw)
    // Call dual format 도 string scalar 유지 (object 승격 0)
    Assert.DoesNotContain("\"ref\"", raw)

[<Fact>]
let ``#31 S4-T11 — modeling apply 로 Work.tokenRole 변경 (A_Modeling round-trip)`` () =
    // 1. singleCylinderYaml 로 base — Adv.TokenRole = None (entity-default)
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let run = Queries.flowsOf ctrl.Id store |> List.head
    let adv = Queries.worksOf run.Id store |> List.find (fun w -> w.LocalName = "Adv")
    Assert.Equal(TokenRole.None, adv.TokenRole)

    // 2. modeling wire 로 tokenRole: None → Source 변경
    let mutationYaml = """
protocol: promaker/v0
level: modeling
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv:
          flow: Run
          tokenRole: Source
          calls: [Cyl1.ADV]
        Ret:
          flow: Run
          calls: [Cyl1.RET]
    arrows:
        - Adv -> Ret : Start
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let _ = parseApplyCommit store mutationYaml
    // 3. store 의 Adv.TokenRole 가 Source 로 변경 검증
    let advAfter = Queries.worksOf run.Id store |> List.find (fun w -> w.LocalName = "Adv")
    Assert.Equal(TokenRole.Source, advAfter.TokenRole)

// ════════════════════════════════════════════════════════════════════════════
// todo §4.5 — system-level work arrows 신규 구조 회귀 테스트 (14 항목 = 12 신규 fact + 2 전환)
// (#12 중복 flow 키 의미 전환 / #13 Mermaid 14 fact 이식 은 기존 테스트 새 구조 전환으로 충족 —
//  ModelProtocolMermaidTests.fs + '중복 flow 키' 테스트. 본 파일 신규 [<Fact>]/[<Theory>] = #1~#11,#14.)
// ════════════════════════════════════════════════════════════════════════════

// ─── #1 cross-flow work arrow round-trip (핵심 버그 수정 증명) ───────────────

/// 두 flow(St201/St202) 에 각각 work(BprSeal/ReinfSeal), system arrows 에
/// `BprSeal -> ReinfSeal : Start` (cross-flow). apply→commit→export→재apply 후에도
/// 이 cross-flow arrow 가 shape 동등하게 보존되는지 검증.
let private crossFlowArrowYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      St201: {}
      St202: {}
    works:
        BprSeal:   { flow: St201, calls: [Cyl1.ADV] }
        ReinfSeal: { flow: St202, calls: [Cyl1.RET] }
    arrows:
        - BprSeal -> ReinfSeal : Start
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``§4.5 #1 — cross-flow work arrow round-trip 보존 (shape 동등)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store crossFlowArrowYaml
    // 원본 store 에 cross-flow arrow 존재 확인
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let arrows = Queries.arrowWorksOf ctrl.Id store
    Assert.Equal(1, arrows.Length)
    Assert.Equal(ArrowType.Start, arrows.Head.ArrowType)
    // round-trip — export→재apply 후에도 shape 동등 (cross-flow arrow 보존 = 핵심 버그 수정 증명)
    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "cross-flow arrow round-trip mismatch: %A" diffs)
    // Controller 의 cross-flow arrow (St201.BprSeal → St202.ReinfSeal) 가 shape 에 정확히 1건 보존.
    // (Cyl1 cylinder cascade 의 internal arrow 는 별도 — Controller scope 만 필터)
    let crossFlow =
        shape1.WorkArrows
        |> Set.filter (fun a -> a.SourceLabel.StartsWith("Controller.") && a.TargetLabel.StartsWith("Controller."))
    Assert.Equal(1, crossFlow.Count)
    let arrow = Set.toList crossFlow |> List.head
    Assert.Equal("Controller.St201.BprSeal", arrow.SourceLabel)
    Assert.Equal("Controller.St202.ReinfSeal", arrow.TargetLabel)

// ─── #2 depth 경계 Theory (entity-level depth 의미) ─────────────────────────

/// depthCap 경계 검증 — `exportToJsonScoped store None (Some d)`.
/// d=1 → system identity (flows/works/arrows 없음) / d=2 → flows 유지·works·arrows 제거 /
/// d=3 → works skeleton (calls 없음) / d=4 → calls 포함.
[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<InlineData(4)>]
let ``§4.5 #2 — depth 경계 entity-level 절단`` (d: int) =
    let store = DsStore()
    let _ = parseApplyCommit store crossFlowArrowYaml
    use doc = ModelProtocol.exportToJsonScoped store None (Some d)
    let raw = doc.RootElement.GetRawText()
    match d with
    | 1 ->
        // system identity 만 — flows/works/arrows 모두 제거
        Assert.DoesNotContain("\"flows\"", raw)
        Assert.DoesNotContain("\"works\"", raw)
        Assert.DoesNotContain("\"arrows\"", raw)
        // system identity 키는 유지
        Assert.Contains("\"system\":\"Controller\"", raw)
    | 2 ->
        // flows 유지, works·arrows 제거
        Assert.Contains("\"flows\"", raw)
        Assert.DoesNotContain("\"works\"", raw)
        Assert.DoesNotContain("\"arrows\"", raw)
    | 3 ->
        // works skeleton 유지 (flow: 등), calls 제거
        Assert.Contains("\"works\"", raw)
        Assert.Contains("\"flow\":\"St201\"", raw)
        Assert.DoesNotContain("\"calls\"", raw)
    | 4 ->
        // calls 까지 모두 유지
        Assert.Contains("\"works\"", raw)
        Assert.Contains("\"calls\"", raw)
    | _ -> failwithf "unexpected depth %d" d

// ─── #3 path scope (work 단위) ──────────────────────────────────────────────

/// `exportToJsonScoped store (Some "<flow path>") None` — 해당 flow 의 works 만 유지,
/// 타 flow works / cross-flow arrows 필터됨.
[<Fact>]
let ``§4.5 #3 — path scope flow 단위 (타 flow works / arrows 필터)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store crossFlowArrowYaml
    // St201 flow scope — BprSeal 만 유지, ReinfSeal (St202) 제거.
    use doc = ModelProtocol.exportToJsonScoped store (Some ".M1.Controller.St201") None
    let raw = doc.RootElement.GetRawText()
    Assert.Contains("\"BprSeal\"", raw)
    Assert.DoesNotContain("\"ReinfSeal\"", raw)
    // cross-flow arrow 는 한쪽 끝(ReinfSeal) 이 scope 밖 → 필터됨
    Assert.DoesNotContain("BprSeal -\\u003E ReinfSeal", raw)
    // partial view 표기
    Assert.Contains("\"view\":\"partial\"", raw)

// ─── #4 countEntities 카운트 불변 (export view:full 평탄화 합) ───────────────

/// EntityKind 5종(System/Flow/Work/Call/ApiDef) 합이 기대값인지.
/// countEntities 가 private 이므로 export 결과 JSON 을 파싱해서 직접 카운트.
[<Fact>]
let ``§4.5 #4 — entity 카운트 불변 (export 결과 파싱 합)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store crossFlowArrowYaml
    use doc = ModelProtocol.exportToJson store
    let systems = doc.RootElement.GetProperty("systems")
    let mutable count = 0
    for sys in systems.EnumerateArray() do
        count <- count + 1   // System
        match sys.TryGetProperty("flows") with
        | true, flows -> count <- count + (flows.EnumerateObject() |> Seq.length)   // Flow
        | _ -> ()
        match sys.TryGetProperty("apis") with
        | true, apis -> count <- count + (apis.EnumerateArray() |> Seq.length)   // ApiDef
        | _ -> ()
        match sys.TryGetProperty("works") with
        | true, works ->
            for w in works.EnumerateObject() do
                count <- count + 1   // Work
                match w.Value.TryGetProperty("calls") with
                | true, calls -> count <- count + (calls.EnumerateArray() |> Seq.length)   // Call
                | _ -> ()
        | _ -> ()
    // 기대: Controller(1) + flows St201/St202(2) + works BprSeal/ReinfSeal(2)
    //       + calls Cyl1.ADV / Cyl1.RET(2) + Cyl1(1) = 8.
    // (Cyl1 은 cylinder sugar short-form 으로 export — apis 키 생략되어 countEntities 의 ApiDef 합 0.
    //  countEntities 가 export JSON 의 apis[] 키를 세는 정책이므로 sugar 생략분은 카운트 제외.)
    Assert.Equal(8, count)

// ─── #5 빈 flow {} 보존 round-trip ──────────────────────────────────────────

/// work 없는 빈 flow (`flows: { St233: {} }`) 가 export→import 후 보존.
let private emptyFlowYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      St233: {}
"""

[<Fact>]
let ``§4.5 #5 — 빈 flow {} 보존 round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store emptyFlowYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let flows = Queries.flowsOf ctrl.Id store |> List.map (fun f -> f.Name) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["St233"], flows)
    // round-trip — 빈 flow 가 export→재apply 후에도 보존
    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "empty flow round-trip mismatch: %A" diffs)

// ─── #6 flow: 키 부재 work → validate 에러 ──────────────────────────────────

[<Fact>]
let ``§4.5 #6 — works entry 에 flow 속성 누락 → validate 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv: { calls: [] }
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("flow' 속성 누락", diag.Format())

// ─── #7 flow: 미존재 flow 참조 → 에러 + nearest 후보 ────────────────────────

[<Fact>]
let ``§4.5 #7 — work 가 미존재 flow 참조 → 에러`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv: { flow: NoSuch, calls: [] }
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("flow 'NoSuch' 가 발견되지 않음", diag.Format())

// ─── #8 동명 work import 충돌 → validate 에러 (JSON dup key 경로) ────────────

/// YAML 은 dup map key 를 거부하므로 JSON 직접 구성. System.Text.Json 은 dup key 둘 다
/// enumerate → 같은 works mapping 에 동명 work 두 개 → system-unique 위반 → validate 에러.
[<Fact>]
let ``§4.5 #8 — 동명 work import 충돌 (works dup key) → validate 에러`` () =
    let json = """
{
  "protocol": "promaker/v0",
  "project": "M1",
  "systems": [
    { "system": "Controller", "kind": "active",
      "flows": { "Run": {} },
      "works": {
        "Dup": { "flow": "Run", "calls": [] },
        "Dup": { "flow": "Run", "calls": [] }
      }
    }
  ]
}
"""
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    Assert.True(diag.HasErrors, "동명 work 두 개는 system-unique 위반 → HasErrors")
    Assert.Contains("안에서 중복 정의", diag.Format())
    // C1 rollback 동반 — plan 비어있음.
    Assert.Equal(0, plan.Operations |> Seq.length)

// ─── #9 동명 work export 충돌 → exception (D7) ──────────────────────────────

/// store 에 같은 system 의 두 flow 에 동일 LocalName work 를 직접 생성(queueAddWork) 후
/// exportToJson 호출 시 system-unique works mapping 직렬화 불가 → invalidOp exception.
[<Fact>]
let ``§4.5 #9 — 동명 work export 충돌 → exception (D7)`` () =
    let store = DsStore()
    store.AddProject("M1") |> ignore
    let plan = ImportPlanBuilder()
    let sysId = ToolOperations.queueAddActiveSystem plan store "Controller"
    let flow1 = ToolOperations.queueAddFlow plan store "F1" sysId
    let flow2 = ToolOperations.queueAddFlow plan store "F2" sysId
    // 두 다른 flow 에 같은 LocalName "Dup" — queueAddWork 는 flow별 unique 라 둘 다 통과.
    ToolOperations.queueAddWork plan store "Dup" flow1 None |> ignore
    ToolOperations.queueAddWork plan store "Dup" flow2 None |> ignore
    store.ApplyImportPlan("dup work export 충돌", plan.Build())
    // export 시 system-unique works mapping 직렬화 불가 → invalidOp.
    let ex = Assert.Throws<System.InvalidOperationException>(fun () ->
        use _ = ModelProtocol.exportToJson store
        ())
    Assert.Contains("system-unique", ex.Message)

// ─── #10 call-arrow (work 안 arrows) 보존 round-trip ────────────────────────

/// work 안 `arrows:` (call 간 ArrowBetweenCalls) 가 새 구조 변환 후에도 round-trip 무손실.
let private callArrowPreserveYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Seq:
          flow: Run
          calls: [Cyl1.ADV, Cyl1.RET]
          arrows:
            - "Cyl1.ADV -> Cyl1.RET : Start"
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``§4.5 #10 — work 안 call-arrow 보존 round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store callArrowPreserveYaml
    let original = Queries.allArrowCalls store |> List.length
    Assert.True(original > 0, "원본 store 에 ArrowBetweenCalls 가 있어야 함")
    let shape1, shape2 = ModelEquivalence.roundTripShape store
    let diffs = ModelEquivalence.diff shape1 shape2
    Assert.True(diffs.IsEmpty, sprintf "call-arrow round-trip mismatch: %A" diffs)
    Assert.Equal(1, shape1.CallArrows.Count)

// ─── #11 flows mapping value unknown 키(plc 외) 거부 ────────────────────────

[<Fact>]
let ``§4.5 #11 — flows value 에 plc 외 키 → 거부`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run:
        foo: 1
    works:
        Adv: { flow: Run, calls: [] }
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("flows value 허용 키 = plc", diag.Format())

// ─── #14 patch.arrows.add in: <System> 로 cross-flow arrow 추가 round-trip ──

/// patch.arrows.add 의 `in: <Sys>` (system-scope) 로 기존 store 의 서로 다른 flow 에 속한
/// 두 work 사이 cross-flow arrow 를 추가 — system-scope arrow 의 cross-flow resolve 검증.
[<Fact>]
let ``§4.5 #14 — patch.arrows.add in System 으로 cross-flow arrow 추가`` () =
    // 1단계: 두 flow(St201/St202) 에 각각 work, arrow 없음.
    let baseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      St201: {}
      St202: {}
    works:
        BprSeal:   { flow: St201, calls: [Cyl1.ADV] }
        ReinfSeal: { flow: St202, calls: [Cyl1.RET] }
  - system: Cyl1
    kind: passive
    device: cylinder
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    Assert.Empty(Queries.arrowWorksOf ctrl.Id store)
    // 2단계: patch.arrows.add — in: Controller (system-scope) 로 cross-flow arrow 추가.
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    add:
      - in: Controller
        entries:
          - BprSeal -> ReinfSeal : Start
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "예상치 못한 diagnostics: %s" (diag.Format()))
    store.ApplyImportPlan("cross-flow arrow patch", plan.Build())
    // cross-flow arrow 1건 추가됨 (system-scope resolve 성공)
    let arrows = Queries.arrowWorksOf ctrl.Id store
    Assert.Equal(1, arrows.Length)
    Assert.Equal(ArrowType.Start, arrows.Head.ArrowType)

// ─── C-2/M-1 회귀 — modeling patch 가 seed된 기존 work reuse (D1 가드 over-fire 차단) ──

/// 메타 리뷰 C-2: seedStoreSystems(patch 경로)가 기존 work 를 WorkIdsByName 에 미리 채우는데
/// D1 가드가 *reuse* 까지 '중복'으로 차단하던 회귀. modeling level 은 lookup-first reuse 가 계약
/// (SSOT §4 level: modeling) 이므로, seed 된 기존 entity 재참조는 충돌이 아니어야 함.
let private c2BaseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flows:
      Run: {}
    works:
        Adv: { flow: Run, calls: [Cyl1.ADV] }
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``메타리뷰 C-2 — modeling patch 가 seed된 기존 work reuse (가드 over-fire 차단)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store c2BaseYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let run = Queries.flowsOf ctrl.Id store |> List.head
    Assert.Equal(1, Queries.worksOf run.Id store |> List.length)
    // modeling level patch — 기존 Adv 재참조. OLD: D1 가드가 seed된 Adv 차단 → HasErrors. NEW: reuse 라 통과.
    let patchYaml = """
protocol: promaker/v0
level: modeling
patch:
  add:
    - in: Controller
      works:
        Adv: { flow: Run }
"""
    let diag, _, plan = parseAndApply store patchYaml
    Assert.False(diag.HasErrors, sprintf "modeling reuse 가 D1 가드로 차단됨 (C-2 회귀): %s" (diag.Format()))
    store.ApplyImportPlan("modeling reuse", plan.Build())
    // work 중복 생성 없이 그대로 1개 (reuse — 새 entity 미생성).
    Assert.Equal(1, Queries.worksOf run.Id store |> List.length)

// ─── Phase 3 (Condition 리팩터링) — Multi-root emit (같은 ConditionType implicit AND 보존) ──
//
// SSOT: todo-refactor-condition.md "박제 결정" (같은 ConditionType 의 여러 top-level root 는 implicit AND) /
//       Phase 3 명세 / 남은작업 4 (emit 이 Conditions.[0] 만 내보내던 data loss 제거).
//
// 검증 전략: store 에 같은 타입 root 를 (parse 는 entity 당 1 root 만 생성하므로) 직접 주입 → export →
// 새 store 에 apply → Runtime SimIndex.build 의 ConditionExpression 을 *GUID 무관 leaf 시그니처 multiset*
// 으로 비교해 의미 동등성 입증. 첫 root 만 emit 되던 버그면 leaf 개수가 줄어 테스트가 실패한다.

/// ConditionExpression 의 모든 Leaf 를 GUID 무관 시그니처로 평탄 추출.
/// (RxWork.LocalName, ContactKind, InputSpec) — round-trip 으로 GUID 는 바뀌지만 모델 의미는 보존.
/// RxWorkGuid → store.Works → LocalName 역추적 (store 는 SimIndex 에 포함).
let rec private leafSignatures (store: DsStore) (expr: Ds2.Runtime.Engine.Core.ConditionExpression) : (string * ContactKind * ValueSpec) list =
    match expr with
    | Ds2.Runtime.Engine.Core.Const _ -> []
    | Ds2.Runtime.Engine.Core.Leaf e ->
        let rxName =
            Queries.getWork e.RxWorkGuid store
            |> Option.map (fun w -> w.LocalName)
            |> Option.defaultValue (string e.RxWorkGuid)
        [ (rxName, e.ContactKind, e.InputSpec) ]
    | Ds2.Runtime.Engine.Core.And xs
    | Ds2.Runtime.Engine.Core.Or xs -> xs |> List.collect (leafSignatures store)
    | Ds2.Runtime.Engine.Core.Not x -> leafSignatures store x

/// Cyl1.ADV / Cyl1.RET ApiDef 를 store 에서 찾아 ApiDefId 반환.
let private cylApiDefId (store: DsStore) (apiName: string) : Guid =
    let proj = (Queries.allProjects store).Head
    let cyl = Queries.passiveSystemsOf proj.Id store |> List.find (fun s -> s.Name = "Cyl1")
    (Queries.apiDefsOf cyl.Id store |> List.find (fun d -> d.Name = apiName)).Id

/// 주어진 ApiDefId 를 참조하는 AutoAux/ComAux/SkipAction leaf 1개짜리 top-level Condition root 생성.
let private mkRoot (condType: ConditionType) (apiDefId: Guid) : Condition =
    let ac = ApiCall("")
    ac.ApiDefId <- Some apiDefId
    let cond = Condition(Type = Some condType)
    cond.ApiCalls.Add(ac)
    cond

/// store → export → 새 store apply → 새 store 반환 (round-trip). diag 오류 시 fail.
let private roundTrip (store: DsStore) : DsStore =
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("Phase 3 round-trip", plan2.Build())
    store2

[<Fact>]
let ``Phase 3 — 같은 ConditionType(AutoAux) top-level root 2개 → export→apply 후 Runtime 의미 동등`` () =
    // 단일 cylinder 모델 apply 후, Adv Call 에 AutoAux root 2개 직접 주입 (Cyl1.RET 조건 + Cyl1.ADV 조건).
    // 첫 root 만 emit 되던 버그면 round-trip 후 leaf 1개로 줄어든다 (data loss).
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let advCall = findAdvCall store
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "RET"))
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "ADV"))

    // 원본 Runtime AutoAux 평가 leaf 시그니처 multiset.
    let idx1 = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let sig1 = leafSignatures store idx1.CallAutoAuxConditions.[advCall.Id] |> List.sort

    // round-trip.
    let store2 = roundTrip store
    let advCall2 = findAdvCall store2
    let idx2 = Ds2.Runtime.Engine.Core.SimIndex.build store2 10
    let sig2 = leafSignatures store2 idx2.CallAutoAuxConditions.[advCall2.Id] |> List.sort

    // 의미 동등: leaf 2개 (각 root 에 1개) 가 round-trip 후에도 보존 (RxWork 이름 / ContactKind / InputSpec 동일).
    Assert.Equal(2, List.length sig1)
    Assert.Equal<(string * ContactKind * ValueSpec) list>(sig1, sig2)

[<Fact>]
let ``Phase 3 — multi-root 는 legacy children 포맷으로 묶여 emit (op/items 미신설)`` () =
    // wire 포맷 lock-in: 같은 타입 root 2개 → 단일 condition object (children 배열) 로 emit.
    // op/items 신설 금지 (박제 결정) — wire 에 op/items 키가 없고 children/conditions 만 사용.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let advCall = findAdvCall store
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "RET"))
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "ADV"))

    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.GetRawText()
    // children 배열로 묶임 (wrapper) — 2개 root 가 children 으로 보존.
    Assert.Contains("\"children\"", json)
    // op/items 키는 신설하지 않음.
    Assert.DoesNotContain("\"op\"", json)
    Assert.DoesNotContain("\"items\"", json)

[<Fact>]
let ``Phase 3 — root 1개는 wrapper 없이 그대로 emit (회귀)`` () =
    // 같은 타입 root 1개만 있을 때는 AND wrapper 합성 없이 기존 emit 동작 그대로 (회귀 0).
    // singleCylinderYaml 의 Adv Call 에 AutoAux root 1개만 추가 → children wrapper 미발생.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let advCall = findAdvCall store
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "RET"))

    use exported = ModelProtocol.exportToJson store
    let advJson =
        // Adv work 의 calls 안 condition 만 검사 — 단일 root 는 conditions 배열만 (children wrapper 없음).
        exported.RootElement.GetRawText()
    Assert.Contains("\"condition\"", advJson)
    // 단일 root 는 wrapper(children) 로 감싸지 않음 — round-trip 으로 leaf 1개 보존도 확인.
    let store2 = roundTrip store
    let advCall2 = findAdvCall store2
    Assert.Equal(1, advCall2.Conditions.Count)
    Assert.Equal(Some ConditionType.AutoAux, advCall2.Conditions.[0].Type)
    // 단일 root 의 leaf 는 conditions(ApiCalls) 에 직접 — children 으로 내려가지 않음.
    Assert.Equal(1, advCall2.Conditions.[0].ApiCalls.Count)
    Assert.Equal(0, advCall2.Conditions.[0].Children.Count)
    let idx2 = Ds2.Runtime.Engine.Core.SimIndex.build store2 10
    Assert.Equal(1, leafSignatures store2 idx2.CallAutoAuxConditions.[advCall2.Id] |> List.length)

[<Fact>]
let ``Phase 3 — Work SkipAction top-level root 2개도 round-trip 의미 보존`` () =
    // Work 경로 (emitConditionRoots 두 번째 호출처) 도 multi-root AND 보존 검증.
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let proj = (Queries.allProjects store).Head
    let ctrl = Queries.activeSystemsOf proj.Id store |> List.head
    let run = Queries.flowsOf ctrl.Id store |> List.head
    let advWork = Queries.worksOf run.Id store |> List.find (fun w -> w.LocalName = "Adv")
    advWork.Conditions.Add(mkRoot ConditionType.SkipAction (cylApiDefId store "RET"))
    advWork.Conditions.Add(mkRoot ConditionType.SkipAction (cylApiDefId store "ADV"))

    let idx1 = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let sig1 = leafSignatures store idx1.WorkSkipActionConditions.[advWork.Id] |> List.sort

    let store2 = roundTrip store
    let ctrl2 = Queries.activeSystemsOf (Queries.allProjects store2).Head.Id store2 |> List.head
    let run2 = Queries.flowsOf ctrl2.Id store2 |> List.head
    let advWork2 = Queries.worksOf run2.Id store2 |> List.find (fun w -> w.LocalName = "Adv")
    let idx2 = Ds2.Runtime.Engine.Core.SimIndex.build store2 10
    let sig2 = leafSignatures store2 idx2.WorkSkipActionConditions.[advWork2.Id] |> List.sort

    Assert.Equal(2, List.length sig1)
    Assert.Equal<(string * ContactKind * ValueSpec) list>(sig1, sig2)

[<Fact>]
let ``Phase 3 — eq 기대값을 가진 같은 타입 root 2개도 round-trip 후 InputSpec 보존`` () =
    // Phase 2 의 eq(InputSpec) emit 분기가 multi-root wrapper(children) 재귀에서도 보존되는지.
    // root1: Cyl1.RET eq true / root2: Cyl1.ADV (조건만).
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml
    let advCall = findAdvCall store
    let root1 =
        let ac = ApiCall("")
        ac.ApiDefId <- Some (cylApiDefId store "RET")
        ac.InputSpec <- ValueSpec.singleBool true
        let c = Condition(Type = Some ConditionType.AutoAux)
        c.ApiCalls.Add(ac); c
    advCall.Conditions.Add(root1)
    advCall.Conditions.Add(mkRoot ConditionType.AutoAux (cylApiDefId store "ADV"))

    let store2 = roundTrip store
    let advCall2 = findAdvCall store2
    let idx2 = Ds2.Runtime.Engine.Core.SimIndex.build store2 10
    let sigs = leafSignatures store2 idx2.CallAutoAuxConditions.[advCall2.Id]
    Assert.Equal(2, List.length sigs)
    // RET leaf 의 InputSpec 이 BoolValue(Single true) 로 보존 (eq round-trip via children wrapper).
    let retSig = sigs |> List.tryFind (fun (n, _, _) -> n = "RET")
    Assert.True(retSig.IsSome, "RET leaf 누락")
    let (_, _, spec) = retSig.Value
    Assert.Equal<ValueSpec>(ValueSpec.singleBool true, spec)

[<Fact>]
let ``Phase 3 — legacy nested conditions/children parse 결과 불변 (회귀)`` () =
    // 기존 conditions/children 입력 (M-E 와 동일 fixture) 의 parse 결과가 Phase 3 변경 후에도 불변.
    // emit 변경만 했으므로 parse 경로는 그대로여야 한다.
    let store = DsStore()
    let _ = parseApplyCommit store nestedCallConditionYaml
    let root = (findAdvCall store).Conditions.[0]
    Assert.Equal(Some ConditionType.ComAux, root.Type)
    Assert.True(root.IsInverted)
    Assert.Equal(1, root.ApiCalls.Count)
    Assert.Equal(1, root.Children.Count)
    Assert.Equal(Some ConditionType.SkipAction, root.Children.[0].Type)
