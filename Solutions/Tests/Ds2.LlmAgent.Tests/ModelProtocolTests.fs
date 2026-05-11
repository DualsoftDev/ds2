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
    let shape1 = ModelEquivalence.captureShape store

    use exported = ModelProtocol.exportToJson store
    // exported JSON 을 YAML 로 변환해서 정상 변환 가능한지 확인
    let yaml = ModelProtocolYaml.jsonElementToYaml exported.RootElement
    Assert.False(System.String.IsNullOrWhiteSpace yaml, "export → YAML 변환이 비어있음")

    // exported JSON 을 새 store 에 적용 후 shape 일치
    let store2 = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store2 exported.RootElement
    Assert.False(diag.HasErrors, sprintf "round-trip 적용 실패: %s" (diag.Format()))
    store2.ApplyImportPlan("round-trip", plan.Build())
    let shape2 = ModelEquivalence.captureShape store2

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

// ─── Critical 3 회귀 — 중복 flow 키 시 한 번만 생성 ────────────────────────

[<Fact>]
let ``중복 flow 키 → diagnostic + 첫 등장 1번만 queueAddFlow`` () =
    // 동일 raw 키 두 번은 YAML duplicate map key 로 잡힘 → JSON 변환 후 dispatcher 가 보지 못함.
    // 본 테스트는 *normalize 후 같은 이름* 이 되는 두 키 (e.g. "flow Run" 과 "flow  Run" — 공백 차이)
    // 가 들어오는 케이스를 시뮬레이션하기 위해 JSON 직접 입력 사용.
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
    let diag, _ = ModelProtocol.apply plan store jdoc.RootElement
    // diagnostic 에 flow 키 중복 메시지 포함 + plan 에 AddFlow 1 회만
    Assert.Contains("flow Run", diag.Format())
    let flowAdds =
        plan.Operations
        |> Seq.filter (function AddFlow _ -> true | _ -> false)
        |> Seq.length
    Assert.Equal(1, flowAdds)

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
