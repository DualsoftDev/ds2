module Ds2.Core.Tests.Ds2TextSequenceLabelTests

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
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith("// DS2TEXT_SEQUENCE_LABEL_PROJECTION:")))
    |> String.concat "\n"

[<Theory>]
[<InlineData(0, false)>]
[<InlineData(1, false)>]
[<InlineData(2, false)>]
[<InlineData(1, true)>]
[<InlineData(2, true)>]
let ``legacy labels do not change causal Text or mutate stored graph`` label identity =
    let store, project, first, _, _ = model ()
    let options = { WriteIdentity = identity }
    let baseline = Ds2TextWriter.writeWithOptions store project.Id options
    first.SequenceLabel <- enum<SequenceLabel> label
    let sourceJson = JsonConverter.serialize store
    let output = Ds2TextWriter.writeWithOptions store project.Id options

    Assert.Equal(baseline, withoutProjectionNotice output)
    Assert.Contains("D.First > D.Last;", output)
    for spelling in [ "label body;"; "label head;"; "label tail;" ] do
        Assert.DoesNotContain(spelling, output)
    Assert.Equal(label <> 0, output.Contains("DS2TEXT_SEQUENCE_LABEL_PROJECTION:"))
    Assert.Equal(sourceJson, JsonConverter.serialize store)

[<Fact>]
let ``gzip SDF load and save retain legacy Head Tail and the causal DAG`` () =
    let store, _, first, last, arrow = model ()
    first.SequenceLabel <- SequenceLabel.Head
    last.SequenceLabel <- SequenceLabel.Tail
    let path = Path.Combine(Path.GetTempPath(), "ds2-sequence-label-" + Guid.NewGuid().ToString("N") + ".sdf")
    try
        JsonConverter.saveToFile path store
        let loaded = JsonConverter.loadFromFile<DsStore> path
        JsonConverter.saveToFile path loaded
        let restored = JsonConverter.loadFromFile<DsStore> path
        Assert.Equal(SequenceLabel.Head, restored.Calls.[first.Id].SequenceLabel)
        Assert.Equal(SequenceLabel.Tail, restored.Calls.[last.Id].SequenceLabel)
        Assert.Equal(first.Id, restored.ArrowCalls.[arrow.Id].SourceId)
        Assert.Equal(last.Id, restored.ArrowCalls.[arrow.Id].TargetId)
        Assert.Equal(ArrowType.Start, restored.ArrowCalls.[arrow.Id].ArrowType)
    finally
        if File.Exists path then File.Delete path

[<Fact>]
let ``retired label words are ordinary names`` () =
    for name in [ "label"; "body"; "head"; "tail" ] do
        Assert.Equal(name, Ds2TextLexeme.quote name)
