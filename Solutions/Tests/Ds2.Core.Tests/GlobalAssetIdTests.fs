module Ds2.Core.Tests.GlobalAssetIdTests

open Ds2.Core
open Xunit

// Phase 0 — GlobalAssetId shape validation.
// Uniqueness is a registry concern, not this type's contract.

[<Fact>]
let ``create accepts urn form`` () =
    let id = GlobalAssetId.create "urn:dualsoft:asset:cnc01"
    Assert.Equal("urn:dualsoft:asset:cnc01", id.Value)

[<Fact>]
let ``create accepts https form`` () =
    let id = GlobalAssetId.create "https://example.com/asset/cnc01"
    Assert.True(GlobalAssetId.isUri id)

[<Fact>]
let ``create accepts IRDI-shape`` () =
    let id = GlobalAssetId.create "0173-1#02-AAY811#001"
    Assert.True(GlobalAssetId.isIrdi id)

[<Fact>]
let ``tryCreate rejects null and empty`` () =
    Assert.True(match GlobalAssetId.tryCreate null with Error _ -> true | _ -> false)
    Assert.True(match GlobalAssetId.tryCreate "" with Error _ -> true | _ -> false)

[<Fact>]
let ``tryCreate rejects whitespace inside`` () =
    match GlobalAssetId.tryCreate "urn:dualsoft: asset:cnc01" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "whitespace must be rejected"

[<Fact>]
let ``isUri detects http https urn`` () =
    Assert.True(GlobalAssetId.isUri (GlobalAssetId.create "urn:x:y"))
    Assert.True(GlobalAssetId.isUri (GlobalAssetId.create "http://x/y"))
    Assert.True(GlobalAssetId.isUri (GlobalAssetId.create "https://x/y"))
    Assert.False(GlobalAssetId.isUri (GlobalAssetId.create "0173-1#02-AAY811#001"))

[<Fact>]
let ``isIrdi requires digit prefix`` () =
    Assert.False(GlobalAssetId.isIrdi (GlobalAssetId.create "urn:x:y"))
    Assert.True(GlobalAssetId.isIrdi (GlobalAssetId.create "0173-1#02-AAY811#001"))

[<Fact>]
let ``equality is structural`` () =
    let a = GlobalAssetId.create "urn:dualsoft:asset:cnc01"
    let b = GlobalAssetId.create "urn:dualsoft:asset:cnc01"
    Assert.Equal<GlobalAssetId>(a, b)
