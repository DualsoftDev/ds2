module ExportModelDocPathDepthTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// Phase 6 chunk-3 — `export_model_doc` 의 path?/depth? 분기 + summary metadata + apply 거부 + find_by_name
/// orphan marker + tryPathOf unsupported kind 회귀 fact lock.
///
/// SSOT: `Apps/Promaker/Docs/yaml-protocol-v0.md §2.8` (partial export view-only spec) /
/// `Apps/Promaker/Docs/done-read-surface-guid-cleanup.md §0.5 v6 chunk-3` (fact a~k 명세).
///
/// 명명 컨벤션: 한글 backtick fact + prefix `6f-N` (Phase 6 → 6f).

// ─── fixture helpers ──────────────────────────────────────────────────────

/// 2-system store — path scope 시 sibling system pop → truncation 발생 fixture.
let private seedTwoSystemStore () =
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    let sysAId = store.AddSystem("SysA", projectId, true)
    let _flowA = store.AddFlow("FlowA", sysAId)
    let sysBId = store.AddSystem("SysB", projectId, true)
    let _flowB = store.AddFlow("FlowB", sysBId)
    store

/// 단일 project + 단일 system fixture — full export 시 truncation 0.
let private seedSingleSystemStore () =
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    let sysId = store.AddSystem("Sys", projectId, true)
    let _flowId = store.AddFlow("F", sysId)
    store

let private getViewLiteral (doc: JsonDocument) : string =
    match doc.RootElement.TryGetProperty("view") with
    | true, v -> v.GetString()
    | _ -> ""

let private tryGetSummary (doc: JsonDocument) : JsonElement option =
    match doc.RootElement.TryGetProperty("summary") with
    | true, v -> Some v
    | _ -> None

// ─── (a) view: partial — path scope 가 sibling system pop → truncation ────

[<Fact>]
let ``6f-1 path scope 가 sibling system pop 하면 view: partial emit`` () =
    let store = seedTwoSystemStore ()
    use doc =
        ModelProtocol.exportToJsonScoped
            store
            (Some ".Proj.SysA")
            None
    Assert.Equal("partial", getViewLiteral doc)

// ─── (b) view: full — single system + 큰 depth, truncation 0 ─────────────

[<Fact>]
let ``6f-2 path 미지정 + depth 미지정 = view: full`` () =
    let store = seedSingleSystemStore ()
    use doc = ModelProtocol.exportToJsonScoped store None None
    Assert.Equal("full", getViewLiteral doc)

[<Fact>]
let ``6f-3 single system store 에 큰 depth (999) = view: full (truncation 0)`` () =
    // path 없음 + depth=999 → applyDepthCap 가 cap 적용하나 실제 절단 없음.
    let store = seedSingleSystemStore ()
    use doc =
        ModelProtocol.exportToJsonScoped store None (Some 999)
    Assert.Equal("full", getViewLiteral doc)

// ─── (c) view: partial → apply 입력 거부 ─────────────────────────────────

[<Fact>]
let ``6f-4 view: partial doc 을 apply 재입력 → diagnostics view 에러`` () =
    let store = seedTwoSystemStore ()
    use partialDoc =
        ModelProtocol.exportToJsonScoped store (Some ".Proj.SysA") None
    Assert.Equal("partial", getViewLiteral partialDoc)

    // 새 store 에 apply — view: partial 라 ERROR 가 발행되어야 함.
    let target = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _refs = ModelProtocol.apply plan target partialDoc.RootElement
    Assert.True(diag.HasErrors, "view: partial doc 가 apply 에서 거부되지 않았음")
    // 메시지 substring lock — drift 방지 ("partial export 결과는 view-only").
    let formatted = diag.Format()
    Assert.Contains("partial export 결과는 view-only", formatted)

// ─── (d) path 없이 depth=999 (큰 정수, 전체 결과) ────────────────────────

[<Fact>]
let ``6f-5 path 없음 + depth=999 → 전체 emit + view: full`` () =
    let store = seedTwoSystemStore ()
    use doc = ModelProtocol.exportToJsonScoped store None (Some 999)
    Assert.Equal("full", getViewLiteral doc)
    // systems 배열은 2건 보존.
    match doc.RootElement.TryGetProperty("systems") with
    | true, sysArr ->
        Assert.Equal(JsonValueKind.Array, sysArr.ValueKind)
        Assert.Equal(2, sysArr.GetArrayLength())
    | _ -> Assert.Fail("systems 키 누락")

// ─── (e) C# layer (`ModelTools.ExportModelDoc`) 사전 거부 분기 drift fence ─

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath
let private modelToolsCsPath =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "Tools", "ModelTools.cs")

[<Fact>]
let ``6f-6 ModelTools.ExportModelDoc 안 depth less-than-zero 사전 거부 분기 존재`` () =
    // C# layer 가 음수 depth 를 사전 거부해야 함 — F# layer (`exportToJsonScoped`) 는 `Some d when d >= 0`
    // pattern 으로 silent ignore 만 함. C# 사전 거부 분기가 drift 로 제거되면 음수 depth 가 silent 처리되어
    // LLM 이 의도 외 결과 (전체 export) 수신. 분기 line 의 텍스트 존재로 drift fence.
    //
    // Note: F# test 가 LlmTurnContextProvider 셋업 비용 (IUiDispatcher mock + Promaker assembly 참조 추가)
    // 없이 fact 를 lock 하기 위한 grep-based fence. 분기 동작 자체는 ModelTools.cs:209 line 의 단순 비교 +
    // VALIDATION_ERROR 즉시 반환 — line 존재 = 동작 보장.
    Assert.True(File.Exists modelToolsCsPath, sprintf "ModelTools.cs not found: %s" modelToolsCsPath)
    let cs = File.ReadAllText modelToolsCsPath
    // 분기 패턴: depth.HasValue && depth.Value < 0  → VALIDATION_ERROR
    let guard = Regex.IsMatch(cs, @"depth\.HasValue\s*&&\s*depth\.Value\s*<\s*0")
    Assert.True(guard, "ModelTools.ExportModelDoc 의 'depth < 0' 사전 거부 분기 누락 — silent ignore drift 회귀 위험")

// ─── (f) summary metadata 절단 시 emit / view: full 시 부재 ──────────────

[<Fact>]
let ``6f-7 절단 발생 시 summary metadata 키 emit`` () =
    let store = seedTwoSystemStore ()
    use doc =
        ModelProtocol.exportToJsonScoped store (Some ".Proj.SysA") None
    Assert.Equal("partial", getViewLiteral doc)
    match tryGetSummary doc with
    | Some summary ->
        Assert.Equal(JsonValueKind.Object, summary.ValueKind)
        // 3 키 (totalEntities / emitted / budget) 모두 존재 — GetProperty 가 throw 안 나면 OK.
        let _total = summary.GetProperty("totalEntities").GetInt32()
        let _emitted = summary.GetProperty("emitted").GetInt32()
        let _budget = summary.GetProperty("budget").GetInt32()
        ()
    | None -> Assert.Fail("절단 발생인데 summary 키 부재")

[<Fact>]
let ``6f-8 view: full 결과에는 summary metadata 키 부재`` () =
    let store = seedSingleSystemStore ()
    use doc = ModelProtocol.exportToJsonScoped store None None
    Assert.Equal("full", getViewLiteral doc)
    Assert.True(tryGetSummary doc |> Option.isNone, "view: full 결과에 summary 가 등장")

// ─── (g) summary.totalEntities >= summary.emitted invariant ──────────────

[<Fact>]
let ``6f-9 summary.totalEntities 가 summary.emitted 이상 (절단 invariant)`` () =
    let store = seedTwoSystemStore ()
    use doc =
        ModelProtocol.exportToJsonScoped store (Some ".Proj.SysA") None
    match tryGetSummary doc with
    | Some summary ->
        let total = summary.GetProperty("totalEntities").GetInt32()
        let emitted = summary.GetProperty("emitted").GetInt32()
        Assert.True(total >= emitted, sprintf "totalEntities (%d) < emitted (%d) — 절단 invariant 위반" total emitted)
    | None -> Assert.Fail("절단 결과인데 summary 부재")

// ─── (h) summary.budget == 500 (PartialBudget literal sync) ──────────────

[<Fact>]
let ``6f-10 summary.budget == 500 (PartialBudget literal sync 회귀 가드)`` () =
    let store = seedTwoSystemStore ()
    use doc =
        ModelProtocol.exportToJsonScoped store (Some ".Proj.SysA") None
    match tryGetSummary doc with
    | Some summary ->
        let budget = summary.GetProperty("budget").GetInt32()
        Assert.Equal(500, budget)
    | None -> Assert.Fail("절단 결과인데 summary 부재 — budget invariant 검증 불가")

// ─── (i) apply 입력에 summary 키 등장 → 거부 ─────────────────────────────

[<Fact>]
let ``6f-11 apply 입력에 summary 키 등장 → ERROR + 메시지 lock`` () =
    // SSOT §2.8 / apply (ModelProtocol.fs:1126): summary 는 partial export 진단 metadata 전용 —
    // apply/validate 재입력 불가. apply 입력 단에 등장하면 사전 거부.
    let json = """{
  "protocol": "promaker/v0",
  "project": "Proj",
  "summary": { "totalEntities": 10, "emitted": 5, "budget": 500 }
}"""
    use doc = JsonDocument.Parse(json)
    let target = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _refs = ModelProtocol.apply plan target doc.RootElement
    Assert.True(diag.HasErrors, "summary 키 입력이 apply 에서 거부되지 않았음")
    let formatted = diag.Format()
    Assert.Contains("summary 는 partial export 진단 metadata 전용", formatted)

// ─── (j) orphan System find_by_name path 마커 ────────────────────────────

[<Fact>]
let ``6f-12 orphan System (project 미부착) 의 tryPathOf 는 None — orphan marker 진입`` () =
    // System 을 만든 후 project active/passive 에서 모두 제거 → orphan. tryPathOf 가 None 을 반환해야
    // C# 측 ModelTools.FindByName 의 inline emit 가 `<orphan:Name>` marker 로 진입.
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    let orphanId = store.AddSystem("Detached", projectId, true)
    let project = store.Projects.[projectId]
    project.ActiveSystemIds.Remove(orphanId) |> Assert.True

    let pathOpt = ModelProtocol.tryPathOf store EntityKind.System orphanId
    Assert.True(Option.isNone pathOpt, "orphan System 의 tryPathOf 가 Some 반환 — 1-segment Project round-trip 오인 회귀")

// ─── (k) tryPathOf path-unsupported EntityKind → None ────────────────────

[<Fact>]
let ``6f-13 tryPathOf path-unsupported EntityKind (Button/Lamp/Condition/Action/ApiDefCategory/DeviceRoot) 는 모두 None`` () =
    // SSOT §2.5.1: dotted-path 어휘가 정의된 5 kind (Project/System/Flow/Work/Call/ApiDef) 외엔
    // 명시적 None. round-trip identity 회귀 차단.
    let store = DsStore()
    let dummyId = Guid.NewGuid()
    let unsupported =
        [ EntityKind.Button; EntityKind.Lamp; EntityKind.Condition
          EntityKind.Action; EntityKind.ApiDefCategory; EntityKind.DeviceRoot ]
    for kind in unsupported do
        let pathOpt = ModelProtocol.tryPathOf store kind dummyId
        Assert.True(Option.isNone pathOpt, sprintf "tryPathOf %A 가 Some 반환 — path-unsupported kind 누락" kind)
