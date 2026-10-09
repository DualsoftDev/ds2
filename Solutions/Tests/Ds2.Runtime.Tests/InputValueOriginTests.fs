module Ds2.Runtime.Tests.InputValueOriginTests

open System
open Ds2.Runtime.Model
open Xunit

let private fresh () = SimState.create 10 Seq.empty Seq.empty Seq.empty

[<Fact>]
let ``normal writes remain unspecified and synthesis is explicitly marked`` () =
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    let state = fresh () |> SimState.setIOValue a "3" |> SimState.setSynthesizedIOValue b "7"
    Assert.Equal(InputValueOrigin.Unspecified, state.IOValueOrigins[a])
    Assert.Equal(InputValueOrigin.EngineSynthesis, state.IOValueOrigins[b])
    Assert.Equal("3", state.IOValues[a])
    Assert.Equal("7", state.IOValues[b])

[<Fact>]
let ``same value changes origin but preserves epoch and stability clock`` () =
    let id = Guid.NewGuid()
    let before = fresh () |> SimState.setSynthesizedIOValue id "3"
    let later = { before with Clock = TimeSpan.FromMilliseconds 25.0 }
    let after = later |> SimState.setIOValue id "3"
    Assert.Equal(InputValueOrigin.Unspecified, after.IOValueOrigins[id])
    Assert.Equal(before.IOValueEpoch[id], after.IOValueEpoch[id])
    Assert.Equal(before.IOValueChangedAt[id], after.IOValueChangedAt[id])
    let synthesized = after |> SimState.setSynthesizedIOValue id "3"
    Assert.Equal(InputValueOrigin.EngineSynthesis, synthesized.IOValueOrigins[id])
    Assert.Equal(after.IOValueEpoch[id], synthesized.IOValueEpoch[id])
    Assert.Equal(after.IOValueChangedAt[id], synthesized.IOValueChangedAt[id])

[<Fact>]
let ``changed synthesized value follows existing epoch and time rules`` () =
    let id = Guid.NewGuid()
    let before = fresh () |> SimState.setIOValue id "3"
    let after = { before with Clock = TimeSpan.FromMilliseconds 25.0 } |> SimState.setSynthesizedIOValue id "7"
    Assert.Equal(before.IOValueEpoch[id] + 1, after.IOValueEpoch[id])
    Assert.Equal(TimeSpan.FromMilliseconds 25.0, after.IOValueChangedAt[id])
    Assert.Equal(InputValueOrigin.EngineSynthesis, after.IOValueOrigins[id])

[<Fact>]
let ``clear removes only selected origin and keeps existing epoch policy`` () =
    let a,b = Guid.NewGuid(),Guid.NewGuid()
    let before = fresh () |> SimState.setSynthesizedIOValue a "3" |> SimState.setIOValue b "7"
    let after = before |> SimState.clearIOValues [a]
    Assert.False(after.IOValueOrigins.ContainsKey a)
    Assert.False(after.IOValues.ContainsKey a)
    Assert.False(after.IOValueChangedAt.ContainsKey a)
    Assert.Equal(before.IOValueEpoch[a], after.IOValueEpoch[a])
    Assert.Equal(InputValueOrigin.Unspecified, after.IOValueOrigins[b])

[<Fact>]
let ``reset clears all diagnostic origins and old immutable snapshot survives`` () =
    let id=Guid.NewGuid()
    let before=fresh () |> SimState.setSynthesizedIOValue id "3"
    let after=SimState.reset before
    Assert.True(after.IOValueOrigins.IsEmpty)
    Assert.True(after.IOValues.IsEmpty)
    Assert.Equal(InputValueOrigin.EngineSynthesis,before.IOValueOrigins[id])
