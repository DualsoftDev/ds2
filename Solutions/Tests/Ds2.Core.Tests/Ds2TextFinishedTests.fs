module Ds2.Core.Tests.Ds2TextFinishedTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Text
open Xunit

let private model () =
    let store = DsStore()
    let project = Project("RecordedBits")
    let system = DsSystem("S")
    let flow = Flow("F", system.Id)
    let work = Work("F", "W", flow.Id)
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    store.Flows.[flow.Id] <- flow
    store.Works.[work.Id] <- work
    project.ActiveSystemIds.Add system.Id
    store, project, work

let private recordedBit (work: Work) value =
    let properties = SimulationWorkProperties()
    properties.IsFinished <- value
    work.SetSimulationProperties properties

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``recorded true bit writes finished in human and identity text`` identity =
    let store, project, work = model ()
    recordedBit work true
    let text = Ds2TextWriter.writeWithOptions store project.Id { WriteIdentity = identity }

    Assert.Contains("finished;", text)
    Assert.DoesNotContain("initial", text)
    Assert.Equal(1, text.Split("finished;").Length - 1)
    Assert.True(work.GetSimulationProperties().Value.IsFinished)

[<Fact>]
let ``false bit and absent property have the same unmarked text`` () =
    let store, project, work = model ()
    let absent = Ds2TextWriter.write store project.Id
    recordedBit work false
    let explicitFalse = Ds2TextWriter.write store project.Id

    Assert.Equal(absent, explicitFalse)
    Assert.DoesNotContain("finished;", explicitFalse)
    Assert.DoesNotContain("ready;", explicitFalse)
    Assert.DoesNotContain("initial", explicitFalse)
    // Text normalizes the bit, but exporting must not delete other SDF properties.
    Assert.True(work.GetSimulationProperties().IsSome)

[<Fact>]
let ``export reads the currently recorded bit without caching its authored value`` () =
    let store, project, work = model ()
    recordedBit work false
    work.Duration <- Some(TimeSpan.FromMilliseconds(25.0))
    Assert.DoesNotContain("finished;", Ds2TextWriter.write store project.Id)

    // A caller can record a snapshot bit in the model before exporting. The writer
    // does not itself capture runtime state or create a completion event.
    work.GetSimulationProperties().Value.IsFinished <- true
    let captured = Ds2TextWriter.write store project.Id
    Assert.Contains("finished;", captured)
    Assert.Contains("duration 25ms;", captured)

    work.GetSimulationProperties().Value.IsFinished <- false
    Assert.DoesNotContain("finished;", Ds2TextWriter.write store project.Id)

[<Fact>]
let ``finished as an entity name is quoted and retired state words are ordinary names`` () =
    Assert.Equal("\"finished\"", Ds2TextLexeme.quote "finished")
    for name in [ "initial"; "ready"; "finish" ] do
        Assert.Equal(name, Ds2TextLexeme.quote name)
