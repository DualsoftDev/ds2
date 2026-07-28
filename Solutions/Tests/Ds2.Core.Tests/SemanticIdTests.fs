module Ds2.Core.Tests.SemanticIdTests

open Ds2.Core
open Xunit

// Phase 0 — SemanticId shape probes.

[<Fact>]
let ``create accepts IRI and URN and IRDI`` () =
    let iri = SemanticId.create "https://admin-shell.io/idta/nameplate/3/0/Nameplate"
    let urn = SemanticId.create "urn:dualsoft:cd:motion.spindle-speed/1/0"
    let irdi = SemanticId.create "0173-1#02-AAO677#002"

    Assert.True(SemanticId.isIri iri)
    Assert.True(SemanticId.isUrn urn)
    Assert.True(SemanticId.isIrdi irdi)

[<Fact>]
let ``isIrdi discriminates URN and IRI`` () =
    let urn = SemanticId.create "urn:dualsoft:cd:motion.spindle-speed/1/0"
    let iri = SemanticId.create "https://example.com"
    Assert.False(SemanticId.isIrdi urn)
    Assert.False(SemanticId.isIrdi iri)

[<Fact>]
let ``tryCreate rejects empty`` () =
    Assert.True(match SemanticId.tryCreate "" with Error _ -> true | _ -> false)

[<Fact>]
let ``equality is structural`` () =
    let a = SemanticId.create "urn:x:y"
    let b = SemanticId.create "urn:x:y"
    Assert.Equal<SemanticId>(a, b)
