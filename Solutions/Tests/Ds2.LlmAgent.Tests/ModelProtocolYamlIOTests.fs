module ModelProtocolYamlIOTests

open System.Text
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.LlmAgent

/// `.yaml` 파일 IO 의 wiring 책임 한정 테스트.
/// store↔JSON 의미 보장은 ModelProtocolTests 가 이미 검증 — 본 module 은 합성 wrapper 의
/// 책임 (view: full emit / view: partial 거부 / round-trip GUID-무시 semantic equivalence) 만 검증.

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

let private loadOk (yaml: string) =
    match ModelProtocolYamlIO.loadStoreFromYamlText yaml with
    | Ok store -> store
    | Error msg -> failwithf "loadStoreFromYamlText 예상치 못한 실패: %s" msg

// ─── view: full emit 검증 ────────────────────────────────────────────────────

[<Fact>]
let ``exportStoreToYamlText — view: full 키 emit`` () =
    let store = loadOk singleCylinderYaml
    let text = ModelProtocolYamlIO.exportStoreToYamlText store
    // SSOT §2.8 — 전체 export 는 항상 view: full.
    Assert.Contains("view: full", text)

[<Fact>]
let ``exportStoreToYamlText — protocol: promaker/v0 키 emit`` () =
    let store = loadOk singleCylinderYaml
    let text = ModelProtocolYamlIO.exportStoreToYamlText store
    // slash 가 ASCII identifier regex 미통과 → DoubleQuoted plain 분기. quoted/unquoted 둘 다 허용.
    Assert.True(
        text.Contains("protocol: promaker/v0") ||
        text.Contains("protocol: \"promaker/v0\""),
        sprintf "protocol 키 emit 실패 — 본문: %s" text)

// ─── BOM 없는 UTF-8 검증 (Save 호출자의 책임이지만 emit 함수 자체는 UTF-8 안전성 보장) ─────

[<Fact>]
let ``exportStoreToYamlText — UTF-8 byte sequence 가 BOM 없이 안전`` () =
    let store = loadOk singleCylinderYaml
    let text = ModelProtocolYamlIO.exportStoreToYamlText store
    let bytes = UTF8Encoding(false).GetBytes(text)
    // BOM = 0xEF 0xBB 0xBF — F# 측 string 자체는 BOM 무관, 저장 시 UTF8Encoding(false) 사용 정합.
    Assert.True(bytes.Length > 3)
    Assert.NotEqual<byte>(0xEFuy, bytes.[0])

// ─── view: partial 거부 검증 (apply 의 책임 — wiring 책임은 Error 전파 정확성) ────

let private partialYaml = """
protocol: promaker/v0
view: partial
project: M1
systems:
  - system: Controller
    kind: active
"""

[<Fact>]
let ``loadStoreFromYamlText — view: partial 거부 후 Error msg 전파`` () =
    match ModelProtocolYamlIO.loadStoreFromYamlText partialYaml with
    | Ok _ -> failwith "view: partial 은 거부되어야 함"
    | Error msg ->
        // SSOT §2.7 룰 #7 — partial export 결과는 view-only.
        Assert.Contains("partial", msg)

// ─── round-trip wiring 검증 (semantic 비교는 ModelProtocolTests 가 이미 보장) ──

// ─── work-level arrows (ArrowBetweenCalls) round-trip 보존 검증 ────────────────

let private cylinderWithWorkArrowsYaml = """
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:
          calls: [Cyl1.ADV, Cyl1.RET]
          arrows:
            - "Cyl1.ADV -> Cyl1.RET : Start"

  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``round-trip — work-level arrows (ArrowBetweenCalls) 보존`` () =
    let store1 = loadOk cylinderWithWorkArrowsYaml
    let original = Queries.allArrowCalls store1 |> List.length
    Assert.True(original > 0, "원본 store 에 ArrowBetweenCalls 가 있어야 검증 의미 있음")

    let yaml2 = ModelProtocolYamlIO.exportStoreToYamlText store1
    // export 결과의 work 안에 arrows 키가 emit 되었는지 확인 (사용자 보고 갭 직접 검증).
    Assert.Contains("arrows:", yaml2)

    let store2 = loadOk yaml2
    let roundTripped = Queries.allArrowCalls store2 |> List.length
    Assert.Equal(original, roundTripped)

[<Fact>]
let ``round-trip — load → export → load 가 동일 system 개수 보존`` () =
    let store1 = loadOk singleCylinderYaml
    let yaml2 = ModelProtocolYamlIO.exportStoreToYamlText store1
    let store2 = loadOk yaml2

    let p1 = Queries.allProjects store1
    let p2 = Queries.allProjects store2
    Assert.Equal(p1.Length, p2.Length)
    Assert.Equal(p1.Head.Name, p2.Head.Name)

    // GUID 는 재발행되므로 무시. system 이름 set 비교.
    let names1 =
        Queries.activeSystemsOf p1.Head.Id store1
        |> Seq.map (fun s -> s.Name)
        |> Set.ofSeq
    let names2 =
        Queries.activeSystemsOf p2.Head.Id store2
        |> Seq.map (fun s -> s.Name)
        |> Set.ofSeq
    Assert.Equal<Set<string>>(names1, names2)
