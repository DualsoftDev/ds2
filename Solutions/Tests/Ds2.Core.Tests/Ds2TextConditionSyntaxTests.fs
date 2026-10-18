module Ds2.Core.Tests.Ds2TextConditionSyntaxTests

open System.Text.Json
open Ds2.Core
open Ds2.Core.Store
open Ds2.Serialization
open Ds2.Text
open Xunit

let private model () =
    let store = DsStore()
    let project = Project("Conditions")
    let system = DsSystem("S")
    let flow = Flow("F", system.Id)
    let work = Work("F", "W", flow.Id)
    let device = DsSystem("D")
    let hardware = Flow("Hardware", device.Id)
    let doneWork = Work("Hardware", "Complete", hardware.Id)
    let call = Call("D", "Run", work.Id)
    store.Projects.[project.Id] <- project
    for item in [ system; device ] do store.Systems.[item.Id] <- item
    for item in [ flow; hardware ] do store.Flows.[item.Id] <- item
    for item in [ work; doneWork ] do store.Works.[item.Id] <- item
    store.Calls.[call.Id] <- call
    project.ActiveSystemIds.Add system.Id
    project.PassiveSystemIds.Add device.Id
    for name in [ "Run"; "A"; "B"; "C" ] do
        let api = ApiDef(name, device.Id)
        api.RxGuid <- Some doneWork.Id
        if name = "Run" then
            api.TxGuid <- Some doneWork.Id
            api.ActionType <- ActionType.Normal None
        api.SensingType <- SensingType.Normal None
        store.ApiDefs.[api.Id] <- api
    store, project, call

let private leaf (store: DsStore) name =
    let api = store.ApiDefs.Values |> Seq.find (fun a -> a.Name = name)
    let reference = ApiCall("D." + name)
    reference.ApiDefId <- Some api.Id
    reference.InputSpec <- BoolValue(Single true)
    let condition = Condition()
    condition.ApiCalls.Add reference
    condition

let private complexCondition (store: DsStore) kind =
    // The overall NOT covers (A OR B) AND C. None of the leaves is a pulse.
    let either = Condition()
    either.IsOR <- true
    either.Children.Add(leaf store "A")
    either.Children.Add(leaf store "B")
    let condition = Condition()
    condition.Type <- Some kind
    condition.IsInverted <- true
    condition.Children.Add either
    condition.Children.Add(leaf store "C")
    condition

[<Theory>]
[<InlineData(0, false)>]
[<InlineData(0, true)>]
[<InlineData(1, false)>]
[<InlineData(1, true)>]
let ``condition SDF roundtrip and writer preserve whole Boolean expression and category`` kind identity =
    let store, project, call = model ()
    let condition = complexCondition store (enum<ConditionType> kind)
    let saved = JsonConverter.serialize condition
    let restored = JsonConverter.deserialize<Condition> saved
    call.Conditions.Add restored
    let output = Ds2TextWriter.writeWithOptions store project.Id { WriteIdentity = identity }
    let keyword = if kind = 0 then "when" else "while"

    Assert.Contains(keyword + " not ((D.A == true or D.B == true) and D.C == true);", output)
    Assert.DoesNotContain("start when", output)
    Assert.DoesNotContain("permit when", output)
    Assert.DoesNotContain("rise ", output)
    Assert.Equal(saved, JsonConverter.serialize restored)
    Assert.Equal(Some(enum<ConditionType> kind), restored.Type)
    Assert.True(restored.IsInverted)
    Assert.True(restored.Children.[0].IsOR)
    Assert.Equal(ContactKind.NoContact, restored.Children.[0].Children.[0].ApiCalls.[0].ContactKind)

[<Fact>]
let ``when while and product skip keep distinct prefixes in the same Call`` () =
    let store, project, call = model ()
    call.Conditions.Add(complexCondition store ConditionType.AutoAux)
    call.Conditions.Add(complexCondition store ConditionType.ComAux)
    let skip = Condition()
    skip.Type <- Some ConditionType.SkipAction
    skip.Token <- Some { Field = "optional"; Op = "eq"; Kind = "bool"; Values = [ JsonSerializer.SerializeToElement(true) ] }
    call.Conditions.Add skip
    let output = Ds2TextWriter.write store project.Id

    Assert.Contains("when not ((D.A == true or D.B == true) and D.C == true);", output)
    Assert.Contains("while not ((D.A == true or D.B == true) and D.C == true);", output)
    Assert.Contains("skip when token.optional == true;", output)
    Assert.DoesNotContain("while when", output)
    Assert.DoesNotContain("when when", output)

[<Fact>]
let ``while is reserved and retired condition prefix words are ordinary names`` () =
    Assert.Equal("\"when\"", Ds2TextLexeme.quote "when")
    Assert.Equal("\"while\"", Ds2TextLexeme.quote "while")
    for name in [ "start"; "permit" ] do
        Assert.Equal(name, Ds2TextLexeme.quote name)
