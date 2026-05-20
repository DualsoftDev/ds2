module Ds2.Store.Editor.Tests.HubSourceTests

open Ds2.Backend.Common
open Xunit

[<Fact>]
let ``HubSource.WellKnownSources 는 literal 5개 모두 포함`` () =
    Assert.True(HubSource.isWellKnown HubSource.Control)
    Assert.True(HubSource.isWellKnown HubSource.VirtualPlant)
    Assert.True(HubSource.isWellKnown HubSource.Monitoring)
    Assert.True(HubSource.isWellKnown HubSource.Plc)
    Assert.True(HubSource.isWellKnown HubSource.Web)

[<Fact>]
let ``HubSource.isWellKnown 은 case-insensitive`` () =
    Assert.True(HubSource.isWellKnown "CONTROL")
    Assert.True(HubSource.isWellKnown "Plc")
    Assert.True(HubSource.isWellKnown "virtualPLANT")

[<Fact>]
let ``HubSource.isWellKnown 은 unknown 차단`` () =
    Assert.False(HubSource.isWellKnown "random_source")
    Assert.False(HubSource.isWellKnown "")
    Assert.False(HubSource.isWellKnown null)

[<Fact>]
let ``HubSource.DefaultAcceptedSources 는 Control + VirtualPlant + Plc`` () =
    let defaults = HubSource.DefaultAcceptedSources |> Set.ofArray
    Assert.Equal(3, defaults.Count)
    Assert.Contains(HubSource.Control, defaults)
    Assert.Contains(HubSource.VirtualPlant, defaults)
    Assert.Contains(HubSource.Plc, defaults)

[<Fact>]
let ``HubSource.DefaultAcceptedSources 는 Monitoring/Web 차단 (echo / 외부 UI 주입 방지)`` () =
    let defaults = HubSource.DefaultAcceptedSources |> Set.ofArray
    Assert.DoesNotContain(HubSource.Monitoring, defaults)
    Assert.DoesNotContain(HubSource.Web, defaults)
