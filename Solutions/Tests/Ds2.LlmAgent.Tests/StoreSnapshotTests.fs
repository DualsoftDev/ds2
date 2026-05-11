module StoreSnapshotTests

open System
open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

// Round-trip 최적화 — doc: Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md §4.1
//
// 본 테스트의 목적 = `StoreSnapshot.render` grammar 와 `RenderSnapshotEnvelope` wire format SSOT 회귀 방어.
// 출력 변형 시 LLM 의 `3.tooling.md` snapshot 룰 (보이는 block 을 신뢰) 이 의미 불일치를 일으켜
// list_projects fallback 호출 또는 stale state 추론 → 1 RT 목표 손상.

[<Fact>]
let ``empty store renders projects (empty)`` () =
    let store = DsStore()
    let snapshot = store.RenderSnapshot()
    Assert.Equal("projects: (empty)", snapshot)

[<Fact>]
let ``single project is listed by name`` () =
    let store = DsStore()
    store.AddProject("MyProj") |> ignore
    let snapshot = store.RenderSnapshot()
    Assert.StartsWith("projects:", snapshot)
    Assert.Contains("MyProj:", snapshot)
    Assert.Contains("systems:", snapshot)
    Assert.Contains("flows:", snapshot)

[<Fact>]
let ``active system shows active role without kind suffix`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    store.AddSystem("Ctrl", projectId, true) |> ignore
    let snapshot = store.RenderSnapshot()
    Assert.Contains("Ctrl (active)", snapshot)
    // active 는 kind suffix 표기하지 않음 (doc §4.1 grammar)
    Assert.DoesNotContain("Ctrl (active/", snapshot)

[<Fact>]
let ``passive system without SystemType shows passive role only`` () =
    // SystemType=None → kindSuffix "" (Migration 은 Load/Replace 경로에서만 동작).
    let store = DsStore()
    let projectId = store.AddProject("P")
    store.AddSystem("Sys1", projectId, false) |> ignore
    let snapshot = store.RenderSnapshot()
    Assert.Contains("Sys1 (passive)", snapshot)
    Assert.DoesNotContain("Sys1 (passive/", snapshot)

[<Fact>]
let ``passive system with cylinder SystemType gets /cylinder kind suffix`` () =
    // doc §4.1 grammar — passive system 의 SystemType "Cylinder_*" → "/cylinder" suffix.
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("Cyl1", projectId, false)
    store.Systems.[sysId].SystemType <- Some "Cylinder_1"
    let snapshot = store.RenderSnapshot()
    Assert.Contains("Cyl1 (passive/cylinder)", snapshot)

[<Fact>]
let ``passive system with clamp SystemType gets /clamp kind suffix`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("Clm", projectId, false)
    store.Systems.[sysId].SystemType <- Some "Clamp_2"
    let snapshot = store.RenderSnapshot()
    Assert.Contains("Clm (passive/clamp)", snapshot)

[<Fact>]
let ``flow is rendered with owner system name`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("Sys", projectId, true)
    store.AddFlow("Run", sysId) |> ignore
    let snapshot = store.RenderSnapshot()
    Assert.Contains("Run @Sys:", snapshot)

[<Fact>]
let ``work appears under flow with empty call dag`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("Sys", projectId, true)
    let flowId = store.AddFlow("Run", sysId)
    store.AddWork("W1", flowId) |> ignore
    let snapshot = store.RenderSnapshot()
    // Work LocalName + 빈 Call DAG 의 `[ ]` 표기.
    Assert.Contains("W1 []", snapshot)

[<Fact>]
let ``XML meta characters in entity name are escaped`` () =
    // doc §StoreSnapshot 의 escapeXml — `<`, `>`, `&` 가 raw 노출되면 `<store-snapshot>` wrapper 깨짐
    // + prompt injection 위험. import / rename 으로 우회 가능 → 회귀 방어.
    let store = DsStore()
    store.AddProject("P&Q<>") |> ignore
    let snapshot = store.RenderSnapshot()
    Assert.Contains("P&amp;Q&lt;&gt;:", snapshot)
    Assert.DoesNotContain("P&Q<>", snapshot)

[<Fact>]
let ``RenderSnapshotEnvelope wraps body with revision attribute`` () =
    let store = DsStore()
    let envelope = store.RenderSnapshotEnvelope(42)
    Assert.StartsWith("<store-snapshot revision=\"42\">", envelope)
    Assert.EndsWith("</store-snapshot>", envelope)
    Assert.Contains("projects: (empty)", envelope)

[<Fact>]
let ``RenderSnapshotEnvelopeAtomic returns matching rev and body`` () =
    // doc §J6 — (rev, body) 동시 캡쳐로 다른 thread BumpRevision 사이에 attribute/body 불일치 차단.
    let store = DsStore()
    store.AddProject("P") |> ignore
    let struct(rev, envelope) = store.RenderSnapshotEnvelopeAtomic()
    Assert.Equal(store.Revision, rev)
    Assert.Contains(sprintf "revision=\"%d\"" rev, envelope)
    Assert.Contains("P:", envelope)

[<Fact>]
let ``Revision is reflected in envelope after mutation`` () =
    let store = DsStore()
    let env0 = store.RenderSnapshotEnvelope(store.Revision)
    store.AddProject("New") |> ignore
    let env1 = store.RenderSnapshotEnvelope(store.Revision)
    Assert.NotEqual<string>(env0, env1)
    Assert.Contains("New:", env1)
