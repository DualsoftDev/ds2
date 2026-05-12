module ModelProtocolMermaidTests

open System.Text.Json
open Xunit
open Ds2.LlmAgent

/// ModelProtocol.Mermaid emitter 검증.
/// SSOT: yaml-protocol-v0.md §2.2 active schema. passive doc / patch-only doc 에서 빈 결과 (UI tab 미생성 트리거).

let private yamlToJsonRoot (yaml: string) : JsonDocument =
    ModelProtocolYaml.yamlToJson yaml

// ─── §3.1 single cylinder — Work flow + Call flow per work ───────────────────

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
let ``single cylinder — Work flow mermaid 가 flowchart TD + subgraph + work 노드 + edge 포함`` () =
    use doc = yamlToJsonRoot singleCylinderYaml
    let m = ModelProtocolMermaid.jsonElementToWorkFlowMermaid doc.RootElement
    let text = match m with Some s -> s | None -> failwith "Work flow mermaid 비어있음"
    Assert.Contains("flowchart TD", text)
    Assert.Contains("subgraph", text)
    Assert.Contains("Controller.Run", text)
    Assert.Contains("\"Adv\"", text)
    Assert.Contains("\"Ret\"", text)
    Assert.Contains("-->", text)

[<Fact>]
let ``single cylinder — Call flow mermaid 가 work 2개 (Adv / Ret) 각각 emit`` () =
    use doc = yamlToJsonRoot singleCylinderYaml
    let blocks = ModelProtocolMermaid.jsonElementToCallFlowMermaids doc.RootElement
    Assert.Equal(2, blocks.Length)
    let titles = blocks |> List.map (fun b -> b.Title)
    Assert.Contains("Controller.Run.Adv", titles)
    Assert.Contains("Controller.Run.Ret", titles)
    let advBlock = blocks |> List.find (fun b -> b.Title = "Controller.Run.Adv")
    Assert.Contains("flowchart LR", advBlock.Mermaid)
    Assert.Contains("\"Cyl1.ADV\"", advBlock.Mermaid)
    // ADV 는 advance class
    Assert.Contains(":::advance", advBlock.Mermaid)

[<Fact>]
let ``single cylinder — jsonElementToBlocks 가 Work flow + Call flow ×2 = 3 block`` () =
    use doc = yamlToJsonRoot singleCylinderYaml
    let blocks = ModelProtocolMermaid.jsonElementToBlocks doc.RootElement
    Assert.Equal(3, blocks.Length)
    Assert.Equal("Work flow", blocks.[0].Title)
    Assert.StartsWith("Call flow — ", blocks.[1].Title)
    Assert.StartsWith("Call flow — ", blocks.[2].Title)

// ─── passive-only doc → 빈 결과 (UI tab 미생성 트리거) ────────────────────────

let private passiveOnlyYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Cyl1
    kind: passive
    device: cylinder
  - system: Cyl2
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``passive-only doc → Work flow None`` () =
    use doc = yamlToJsonRoot passiveOnlyYaml
    let m = ModelProtocolMermaid.jsonElementToWorkFlowMermaid doc.RootElement
    Assert.True(m.IsNone)

[<Fact>]
let ``passive-only doc → Call flow 빈 list`` () =
    use doc = yamlToJsonRoot passiveOnlyYaml
    let blocks = ModelProtocolMermaid.jsonElementToCallFlowMermaids doc.RootElement
    Assert.Empty blocks

[<Fact>]
let ``passive-only doc → jsonElementToBlocks 빈 list (UI Mermaid tab 미생성 트리거)`` () =
    use doc = yamlToJsonRoot passiveOnlyYaml
    let blocks = ModelProtocolMermaid.jsonElementToBlocks doc.RootElement
    Assert.Empty blocks

// ─── Reset connector / edge label 검증 ───────────────────────────────────────

let private resetArrowYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Loop:
      works:
        A: { calls: [Cyl1.ADV] }
        B: { calls: [Cyl1.RET] }
      arrows:
        - A -> B : Start
        - B -> A : Reset

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Reset arrow → 점선 connector emit`` () =
    use doc = yamlToJsonRoot resetArrowYaml
    let m = ModelProtocolMermaid.jsonElementToWorkFlowMermaid doc.RootElement |> Option.get
    Assert.Contains("-.->", m)
    // Reset 은 라벨 표시 (Start 와 달리)
    Assert.Contains("|Reset|", m)
    // Start 라벨은 생략 (시각 노이즈 회피)
    Assert.DoesNotContain("|Start|", m)

// ─── §3.3 ArrowBetweenCalls (call-scope) 검증 ────────────────────────────────

let private callArrowYaml = """
protocol: promaker/v0
project: Jig1

systems:
  - system: Controller
    kind: active
    flow Test:
      works:
        Sequence:
          calls: [Jig.TILT_UP, Jig.HOLD, Jig.TILT_DOWN]
          arrows:
            - Jig.TILT_UP -> Jig.HOLD       : Start
            - Jig.HOLD    -> Jig.TILT_DOWN  : Start

  - system: Jig
    kind: passive
    device: custom(TiltingJig)
    apis: [TILT_UP, HOLD, TILT_DOWN]
"""

[<Fact>]
let ``Call flow 안 call-scope arrows emit (Sequence work)`` () =
    use doc = yamlToJsonRoot callArrowYaml
    let blocks = ModelProtocolMermaid.jsonElementToCallFlowMermaids doc.RootElement
    Assert.Equal(1, blocks.Length)
    let b = blocks.Head
    Assert.Equal("Controller.Test.Sequence", b.Title)
    // 3 call 노드 모두 정의
    Assert.Contains("\"Jig.TILT_UP\"", b.Mermaid)
    Assert.Contains("\"Jig.HOLD\"", b.Mermaid)
    Assert.Contains("\"Jig.TILT_DOWN\"", b.Mermaid)
    // chain edge
    Assert.Contains("Jig_TILT_UP --> Jig_HOLD", b.Mermaid)
    Assert.Contains("Jig_HOLD --> Jig_TILT_DOWN", b.Mermaid)
    // TILT_UP 은 advance, TILT_DOWN 은 retract class
    Assert.Contains(":::advance", b.Mermaid)
    Assert.Contains(":::retract", b.Mermaid)

// ─── multi-flow / multi-work — subgraph 개수 확인 ──────────────────────────────

let private multiFlowYaml = """
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
    flow Stop:
      works:
        Halt: { calls: [Z1_C1.RET] }

  - system: Z1_C1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``multi-flow — Work flow 안 flow 2개가 각각 subgraph 로 emit`` () =
    use doc = yamlToJsonRoot multiFlowYaml
    let m = ModelProtocolMermaid.jsonElementToWorkFlowMermaid doc.RootElement |> Option.get
    Assert.Contains("Controller.Run", m)
    Assert.Contains("Controller.Stop", m)
    // subgraph 키워드 2회 이상 등장
    let countSubgraph = (m.Split([| "subgraph" |], System.StringSplitOptions.None)).Length - 1
    Assert.True(countSubgraph >= 2, sprintf "subgraph 개수 < 2: %d" countSubgraph)

[<Fact>]
let ``multi-flow — Call flow 가 work 3개 emit (Z1_Adv / Z1_Ret / Halt)`` () =
    use doc = yamlToJsonRoot multiFlowYaml
    let blocks = ModelProtocolMermaid.jsonElementToCallFlowMermaids doc.RootElement
    Assert.Equal(3, blocks.Length)
    let titles = blocks |> List.map (fun b -> b.Title)
    Assert.Contains("Controller.Run.Z1_Adv", titles)
    Assert.Contains("Controller.Run.Z1_Ret", titles)
    Assert.Contains("Controller.Stop.Halt", titles)
