module Ds2.Core.Tests.Ds2TextInterlockedTests

open System
open System.IO
open Ds2.Core
open Ds2.Core.Store
open Ds2.Serialization
open Ds2.Text
open Xunit

let private model () =
    let store = DsStore()
    let project = Project("CausalGraph")
    let system = DsSystem("S")
    let flow = Flow("F", system.Id)
    let work = Work("F", "W", flow.Id)
    let first = Call("D", "First", work.Id)
    let last = Call("D", "Last", work.Id)
    let arrow = ArrowBetweenCalls(work.Id, first.Id, last.Id, ArrowType.Start)
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    store.Flows.[flow.Id] <- flow
    store.Works.[work.Id] <- work
    store.Calls.[first.Id] <- first
    store.Calls.[last.Id] <- last
    store.ArrowCalls.[arrow.Id] <- arrow
    project.ActiveSystemIds.Add system.Id
    store, project, first, last, arrow

let private withoutProjectionNotice (text: string) =
    text.Split('\n')
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith("// DS2TEXT_INTERLOCKED_PROJECTION:")))
    |> String.concat "\n"

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(true, false)>]
[<InlineData(false, true)>]
[<InlineData(true, true)>]
let ``legacy interlocked value is not an authored instruction and leaves the graph intact`` interlocked identity =
    let store, project, first, _, _ = model ()
    let options = { WriteIdentity = identity }
    let baseline = Ds2TextWriter.writeWithOptions store project.Id options
    first.Interlocked <- interlocked
    let sourceJson = JsonConverter.serialize store
    let output = Ds2TextWriter.writeWithOptions store project.Id options

    Assert.Equal(baseline, withoutProjectionNotice output)
    Assert.Contains("D.First > D.Last;", output)
    Assert.DoesNotContain("interlocked true;", output)
    Assert.DoesNotContain("interlocked false;", output)
    Assert.Equal(interlocked, output.Contains("DS2TEXT_INTERLOCKED_PROJECTION:"))
    Assert.Equal(sourceJson, JsonConverter.serialize store)

[<Fact>]
let ``gzip SDF load and save preserve legacy interlocked true without changing the graph`` () =
    let store, _, first, last, arrow = model ()
    first.Interlocked <- true
    let path = Path.Combine(Path.GetTempPath(), "ds2-interlocked-" + Guid.NewGuid().ToString("N") + ".sdf")
    try
        JsonConverter.saveToFile path store
        let loaded = JsonConverter.loadFromFile<DsStore> path
        JsonConverter.saveToFile path loaded
        let restored = JsonConverter.loadFromFile<DsStore> path
        Assert.True(restored.Calls.[first.Id].Interlocked)
        Assert.False(restored.Calls.[last.Id].Interlocked)
        Assert.Equal(first.Id, restored.ArrowCalls.[arrow.Id].SourceId)
        Assert.Equal(last.Id, restored.ArrowCalls.[arrow.Id].TargetId)
        Assert.Equal(ArrowType.Start, restored.ArrowCalls.[arrow.Id].ArrowType)
    finally
        if File.Exists path then File.Delete path

[<Fact>]
let ``interlocked is an ordinary name`` () =
    Assert.Equal("interlocked", Ds2TextLexeme.quote "interlocked")
