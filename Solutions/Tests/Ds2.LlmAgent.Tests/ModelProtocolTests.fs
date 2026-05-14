module ModelProtocolTests

open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

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
    flow Run:
      works:
        Adv:
          calls: [Cyl1.ADV]
        Ret:
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
    flow Run:
      works:
        Z1_Adv:    { calls: [Z1_C1.ADV, Z1_C2.ADV] }
        Z1_Punch:  { calls: [P1.PUNCH] }
        Z1_Ret:    { calls: [Z1_C1.RET, Z1_C2.RET] }
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
    flow Run:
      works:
        A: { calls: [] }
        B: { calls: [] }
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
    flow Run:
      works:
        Adv:
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
    flow Bad:
      works: {}
"""
    let store = DsStore()
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors)
    Assert.Contains("flow 키 존재", diag.Format())

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
    // 동일 raw 키 두 번은 YAML duplicate map key 로 잡힘 → JSON 변환 후 dispatcher 가 보지 못함.
    // 본 테스트는 *normalize 후 같은 이름* 이 되는 두 키 (e.g. "flow Run" 과 "flow  Run" — 공백 차이)
    // 가 들어오는 케이스를 시뮬레이션하기 위해 JSON 직접 입력 사용.
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
      "flow Run":  { "works": { "A": { "calls": [] } } },
      "flow  Run": { "works": { "B": { "calls": [] } } }
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
    Assert.Contains("flow Run", diag.Format())
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
    flow Run:
      works:
        A: { calls: [] }
        B: { calls: [] }
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
    flow Run:
      works:
        Z1_Adv: { calls: [Z1_C1.ADV] }
        Z1_Ret: { calls: [Z1_C1.RET] }
      arrows:
        - Z1_Adv -> Z1_Ret : Start
  - { system: Z1_C1, kind: passive, device: cylinder }
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml

    // 2단계: patch — Zone 4 system 추가 + 같은 Flow 에 arrow 추가
    let patchYaml = """
protocol: promaker/v0
patch:
  add:
    - { system: Z4_C1, kind: passive, device: cylinder }
  arrows:
    add:
      - in: Controller.Run
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

[<Fact>]
let ``patch.arrows.remove — PoC 미지원 친절 에러`` () =
    let baseYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        A: { calls: [] }
        B: { calls: [] }
      arrows:
        - A -> B : Start
"""
    let store = DsStore()
    let _ = parseApplyCommit store baseYaml
    let patchYaml = """
protocol: promaker/v0
patch:
  arrows:
    remove:
      - in: Controller.Run
        entries:
          - A -> B
"""
    let diag, _, _ = parseAndApply store patchYaml
    Assert.True(diag.HasErrors)
    Assert.Contains("PoC 미지원", diag.Format())

// ─── Major 2 회귀 — workDuration / opposing override export round-trip ──────

[<Fact>]
let ``workDuration override (Active Work) round-trip`` () =
    let yaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Slow:
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
    flow Run:
      works:
        Step1:
          calls: [Cyl1.ADV, Clm1.CLP]
        Step2:
          calls: [R1.PICK, R1.PLACE]
          workDuration: 2s
        Step3:
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

// ─── review C2 회귀 — patch.add 의 system 키 없는 entry silent drop ────────────

[<Fact>]
let ``patch.add 의 'in:' + 자식 키 entry → silent drop 대신 diagnostic (review C2)`` () =
    // review C2: prompt 는 LLM 에게 `in: Controller.Run.works / Zone4: {...}` 형식의 Work 추가를
    // 유효 schema 로 가르치고 있었으나, dispatcher 의 patch.add filter 가 system 키 없는 entry 를
    // silent drop 했음. LLM 이 prompt 그대로 발행 시 Zone4 가 store 에 미생성되고 [plan] queued 로
    // "성공" 응답 — silent no-op + 후속 arrows.add 가 Zone4 missing 으로 부분 실패. 본 fix 는
    // silent drop 대신 친절 에러로 안내 (PoC scope 명시).
    let json = """
{
  "protocol": "promaker/v0",
  "patch": {
    "add": [
      { "in": "Controller.Run.works", "Zone4": { "calls": [] } }
    ]
  }
}
"""
    use jdoc = System.Text.Json.JsonDocument.Parse(json)
    let store = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    Assert.True(diag.HasErrors, "system 키 없는 patch.add entry 는 친절 에러로 reject 되어야")
    let msg = diag.Format()
    Assert.Contains("patch.add", msg)
    Assert.Contains("PoC 미지원", msg)

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
      "flow Run": {
        "works": {
          "W1": {
            "calls": ["Cyl1.ADV", "Cyl1.ADV"],
            "arrows": ["BROKEN_ARROW_NO_TYPE"]
          }
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
    """{"protocol":"promaker/v0","project":"M1","systems":[{"system":"Ctl","kind":"active","flow Run":{"works":{"A.B":{"calls":[]}}}}]}""")>]
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

// ─── Phase 7 §4.2 C-3 — CallCondition tree + ContactKind dual format ────────
//
// SSOT yaml-protocol-v0.md §2.2.1 dual format. enhanced calls 의 emit/apply round-trip 검증.

let private callConditionYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              contactKind: NcContact
              callCondition:
                type: ComAux
                isOR: true
                conditions:
                  - ref: Cyl1.RET
                    contactKind: RisingPulse
                  - Cyl1.ADV
        Ret:
          calls: [Cyl1.RET]
      arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-3 — CallCondition + ContactKind dual format round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store callConditionYaml

    // Adv work 의 Call (Cyl1.ADV) — ContactKind + CallCondition 적용 확인
    let projects = Queries.allProjects store
    let controller = Queries.activeSystemsOf projects.Head.Id store |> List.head
    let runFlow = Queries.flowsOf controller.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let advCall = Queries.callsOf advWork.Id store |> List.head

    // ContactKind (1:1 invariant — ApiCalls[0])
    Assert.NotEmpty(advCall.ApiCalls)
    Assert.Equal(ContactKind.NcContact, advCall.ApiCalls.[0].ContactKind)

    // CallCondition root 1개
    Assert.Equal(1, advCall.CallConditions.Count)
    let cond = advCall.CallConditions.[0]
    Assert.Equal(Some CallConditionType.ComAux, cond.Type)
    Assert.True(cond.IsOR)
    Assert.False(cond.IsInverted)
    Assert.Equal(2, cond.Conditions.Count)
    Assert.Equal(ContactKind.RisingPulse, cond.Conditions.[0].ContactKind)
    Assert.Equal(ContactKind.NoContact, cond.Conditions.[1].ContactKind)  // string scalar = default

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
    Assert.Equal(1, advCall2.CallConditions.Count)
    let cond2 = advCall2.CallConditions.[0]
    Assert.Equal(Some CallConditionType.ComAux, cond2.Type)
    Assert.True(cond2.IsOR)
    Assert.Equal(2, cond2.Conditions.Count)
    Assert.Equal(ContactKind.RisingPulse, cond2.Conditions.[0].ContactKind)

[<Fact>]
let ``Phase 7 §4.2 C-3 — default case 는 string scalar 유지 (legacy 호환)`` () =
    let store = DsStore()
    let _ = parseApplyCommit store singleCylinderYaml  // 보강 0 — 기존 fixture
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.ToString()
    // 보강 0 인 경우 object 승격 없음 — 신규 키 등장 0건
    Assert.DoesNotContain("\"ref\"", json)
    Assert.DoesNotContain("\"contactKind\"", json)
    Assert.DoesNotContain("\"callCondition\"", json)
    // 기존 string scalar emit 유지 — calls 가 array of string
    Assert.Contains("\"calls\"", json)

// ─── Phase 7 §4.2 C-4 — SkipInputSensor + InTag/OutTag dual format ──────────

let private c4Yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              skipInputSensor: true
              inTag: { name: ADV_LMT, address: X10 }
              outTag: { name: ADV, address: Y10 }
        Ret:
          calls: [Cyl1.RET]
      arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-4 — SkipInputSensor + InTag/OutTag round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store c4Yaml

    let projects = Queries.allProjects store
    let controller = Queries.activeSystemsOf projects.Head.Id store |> List.head
    let runFlow = Queries.flowsOf controller.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let advCall = Queries.callsOf advWork.Id store |> List.head
    let ac = advCall.ApiCalls.[0]

    Assert.True(ac.SkipInputSensor)
    Assert.True(ac.InTag.IsSome)
    Assert.Equal("ADV_LMT", ac.InTag.Value.Name)
    Assert.Equal("X10", ac.InTag.Value.Address)
    Assert.True(ac.OutTag.IsSome)
    Assert.Equal("ADV", ac.OutTag.Value.Name)
    Assert.Equal("Y10", ac.OutTag.Value.Address)

    // emit 시 모든 키 보존
    use exported = ModelProtocol.exportToJson store
    let compact = exported.RootElement.ToString().Replace(" ", "")
    Assert.Contains("\"skipInputSensor\":true", compact)
    Assert.Contains("\"inTag\":{", compact)
    Assert.Contains("\"name\":\"ADV_LMT\"", compact)
    Assert.Contains("\"address\":\"X10\"", compact)
    Assert.Contains("\"outTag\":{", compact)

    // round-trip 의미 보존
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "C-4 round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("C-4 round-trip", plan2.Build())
    let ctrl2 = Queries.activeSystemsOf (Queries.allProjects store2).Head.Id store2 |> List.head
    let flow2 = Queries.flowsOf ctrl2.Id store2 |> List.head
    let work2 = Queries.worksOf flow2.Id store2 |> List.find (fun w -> w.LocalName = "Adv")
    let ac2 = (Queries.callsOf work2.Id store2 |> List.head).ApiCalls.[0]
    Assert.True(ac2.SkipInputSensor)
    Assert.Equal("X10", ac2.InTag.Value.Address)
    Assert.Equal("Y10", ac2.OutTag.Value.Address)

// ─── Phase 7 §4.2 C-5 — CallType + apiDetails (ApiDefActionType / Description) ──

let private c5Yaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              callType: SkipIfCompleted
        Ret:
          calls: [Cyl1.RET]
      arrows:
        - Adv -> Ret : Start

  - system: Cyl1
    kind: passive
    device: cylinder
    apiDetails:
      ADV:
        actionType: Pulse
        description: "Advance command"
      RET:
        actionType: TimeTotal(800)
"""

[<Fact>]
let ``Phase 7 §4.2 C-5 — CallType + apiDetails round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store c5Yaml

    let projects = Queries.allProjects store
    let controller = Queries.activeSystemsOf projects.Head.Id store |> List.head
    let runFlow = Queries.flowsOf controller.Id store |> List.head
    let advWork = Queries.worksOf runFlow.Id store |> List.find (fun w -> w.LocalName = "Adv")
    let advCall = Queries.callsOf advWork.Id store |> List.head

    // SimulationCallProperties.CallType
    let simCallType =
        advCall.Properties
        |> Seq.tryPick (function | SimulationCall p -> Some p.CallType | _ -> None)
    Assert.Equal(Some CallType.SkipIfCompleted, simCallType)

    // ApiDef.ApiDefActionType / Description
    let cyl = Queries.passiveSystemsOf projects.Head.Id store |> List.head
    let cylApiDefs = Queries.apiDefsOf cyl.Id store
    let adv = cylApiDefs |> List.find (fun d -> d.Name = "ADV")
    let ret = cylApiDefs |> List.find (fun d -> d.Name = "RET")
    Assert.Equal(ApiDefActionType.Pulse, adv.ApiDefActionType)
    Assert.Equal(Some "Advance command", adv.Description)
    Assert.Equal(ApiDefActionType.TimeTotal 800, ret.ApiDefActionType)

    // emit 시 모든 키 보존
    use exported = ModelProtocol.exportToJson store
    let json = exported.RootElement.ToString()
    let compact = json.Replace(" ", "")
    Assert.Contains("\"callType\":\"SkipIfCompleted\"", compact)
    Assert.Contains("\"apiDetails\":{", compact)
    Assert.Contains("\"actionType\":\"Pulse\"", compact)
    Assert.Contains("\"actionType\":\"TimeTotal(800)\"", compact)
    Assert.Contains("\"description\":\"Advance command\"", json)

    // round-trip 의미 보존
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "C-5 round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("C-5 round-trip", plan2.Build())
    let cyl2 = Queries.passiveSystemsOf (Queries.allProjects store2).Head.Id store2 |> List.head
    let adv2 = Queries.apiDefsOf cyl2.Id store2 |> List.find (fun d -> d.Name = "ADV")
    Assert.Equal(ApiDefActionType.Pulse, adv2.ApiDefActionType)
    Assert.Equal(Some "Advance command", adv2.Description)
    let ret2 = Queries.apiDefsOf cyl2.Id store2 |> List.find (fun d -> d.Name = "RET")
    Assert.Equal(ApiDefActionType.TimeTotal 800, ret2.ApiDefActionType)

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
    flow Run:
      works:
        Adv:
          tokenRole: Source
          calls: [Cyl1.ADV]
        Ret:
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
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              callCondition:
                type: ComAux
                isInverted: true
                conditions:
                  - Cyl1.RET
                children:
                  - type: SkipUnmatch
                    isOR: true
                    conditions:
                      - ref: Cyl1.ADV
                        contactKind: NcContact

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``외부 review M-E — nested CallCondition children round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store nestedCallConditionYaml
    let advCall = findAdvCall store
    let root = advCall.CallConditions.[0]
    Assert.Equal(Some CallConditionType.ComAux, root.Type)
    Assert.True(root.IsInverted)
    Assert.False(root.IsOR)
    Assert.Equal(1, root.Conditions.Count)
    Assert.Equal(1, root.Children.Count)
    let child = root.Children.[0]
    Assert.Equal(Some CallConditionType.SkipUnmatch, child.Type)
    Assert.True(child.IsOR)
    Assert.Equal(1, child.Conditions.Count)
    Assert.Equal(ContactKind.NcContact, child.Conditions.[0].ContactKind)

    // round-trip: nested children 보존 확인
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan2 = ImportPlanBuilder()
    let diag2, _ = ModelProtocol.apply plan2 store2 exported.RootElement
    Assert.False(diag2.HasErrors, sprintf "nested round-trip diag: %s" (diag2.Format()))
    store2.ApplyImportPlan("nested CC round-trip", plan2.Build())
    let advCall2 = findAdvCall store2
    let root2 = advCall2.CallConditions.[0]
    Assert.Equal(Some CallConditionType.ComAux, root2.Type)
    Assert.True(root2.IsInverted)
    Assert.Equal(1, root2.Children.Count)
    Assert.Equal(Some CallConditionType.SkipUnmatch, root2.Children.[0].Type)
    Assert.True(root2.Children.[0].IsOR)
    Assert.Equal(ContactKind.NcContact, root2.Children.[0].Conditions.[0].ContactKind)

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
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              callCondition: {}

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``외부 review m-5 — 빈 callCondition object 는 None 정규화`` () =
    let store = DsStore()
    let _ = parseApplyCommit store emptyCallConditionYaml
    let advCall = findAdvCall store
    // 빈 callCondition: {} 는 의미 0 의 CallCondition 추가 회피 → CallConditions 0건
    Assert.Equal(0, advCall.CallConditions.Count)

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
    Assert.DoesNotContain("\"callCondition\"", json)
    Assert.DoesNotContain("\"apiDetails\"", json)
