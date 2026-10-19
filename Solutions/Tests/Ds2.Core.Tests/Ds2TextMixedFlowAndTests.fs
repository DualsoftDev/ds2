module Ds2.Core.Tests.Ds2TextMixedFlowAndTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Text
open Xunit

// FS1 S-17 — 한 진입(같은 과녁의 Start 원인 묶음)은 **한 문장**으로 적는다.
// 원인 하나라도 다른 Flow 면 그 진입 전체를 System 레벨에 적는다. 화살표마다 자리를 정하면
// `(F.A, H.C) > H.X;` 가 System 의 `F.A > H.X;` 와 Flow H 안의 `C > X;` 로 갈라지고,
// 다시 읽으면 독립 진입 둘(OR)이 된다.

let private model () =
    let store = DsStore()
    let project = Project("MixedAnd")
    let system = DsSystem("S")
    let f = Flow("F", system.Id)
    let h = Flow("H", system.Id)
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    project.ActiveSystemIds.Add system.Id
    for flow in [ f; h ] do store.Flows.[flow.Id] <- flow
    let work (flow: Flow) name =
        let w = Work(flow.Name, name, flow.Id)
        store.Works.[w.Id] <- w
        w
    let a, c, d, x, y = work f "A", work h "C", work h "D", work h "X", work h "Y"
    let arrow (src: Work) (tgt: Work) kind =
        let arr = ArrowBetweenWorks(system.Id, src.Id, tgt.Id, kind)
        store.ArrowWorks.[arr.Id] <- arr
    store, project, arrow, (a, c, d, x, y)

let private statements (text: string) =
    text.Split('\n') |> Array.map (fun l -> l.Trim()) |> Array.filter (fun l -> l.Contains(" > ") || l.Contains(" => ")) |> List.ofArray

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``mixed Flow AND entry is written as one System statement`` identity =
    let store, project, arrow, (a, c, _, x, _) = model ()
    arrow a x ArrowType.Start
    arrow c x ArrowType.Start
    let text = Ds2TextWriter.writeWithOptions store project.Id { WriteIdentity = identity }
    let lines = statements text
    Assert.Contains("(F.A, H.C) > H.X;", lines)
    Assert.DoesNotContain("C > X;", lines)
    Assert.DoesNotContain("F.A > H.X;", lines)

[<Fact>]
let ``mixed StartReset entry keeps both causes together`` () =
    let store, project, arrow, (a, c, _, x, _) = model ()
    arrow a x ArrowType.StartReset
    arrow c x ArrowType.StartReset
    let lines = statements (Ds2TextWriter.write store project.Id)
    Assert.Contains("(F.A, H.C) => H.X;", lines)
    Assert.Equal(1, lines |> List.filter (fun l -> l.EndsWith "X;") |> List.length)

[<Fact>]
let ``same Flow AND stays inside its Flow and a cross Flow single cause stays at System level`` () =
    let store, project, arrow, (a, c, d, x, y) = model ()
    arrow c x ArrowType.Start
    arrow d x ArrowType.Start
    arrow a y ArrowType.Start
    let lines = statements (Ds2TextWriter.write store project.Id)
    Assert.Contains("(C, D) > X;", lines)
    Assert.Contains("F.A > H.Y;", lines)

[<Fact>]
let ``mixed entry text is a fixed point of a second write`` () =
    let store, project, arrow, (a, c, _, x, _) = model ()
    arrow a x ArrowType.Start
    arrow c x ArrowType.Start
    let first = Ds2TextWriter.write store project.Id
    let second = Ds2TextWriter.write store project.Id
    Assert.Equal(first, second)
