module Ds2.Core.Tests.ConceptDescriptionRegistryTests

open System
open Ds2.Core
open Ds2.Core.Services
open Xunit

// Phase 0 — In-memory ConceptDescription registry + ADR-008 id shape.

let private cd id : ConceptDescription = {
    Id = SemanticId id
    PreferredName = Map [ "ko", "테스트"; "en", "test" ]
    Definition = Map.empty
    Unit = None
    Status = Active
    EclassRef = None
    IecCddRef = None
    IssuedBy = Some "phase0-test"
    IssuedAt = DateTimeOffset.UtcNow
}

[<Fact>]
let ``ADR-008 id shape accepts urn dualsoft cd path major minor`` () =
    let valid = SemanticId "urn:dualsoft:cd:motion.spindle-speed/1/0"
    Assert.True(ConceptDescription.isValidId valid)

[<Fact>]
let ``ADR-008 id shape accepts kebab-case with hyphen`` () =
    Assert.True(ConceptDescription.isValidId (SemanticId "urn:dualsoft:cd:vibration-rms/1/0"))

[<Fact>]
let ``ADR-008 id shape rejects missing minor`` () =
    Assert.False(ConceptDescription.isValidId (SemanticId "urn:dualsoft:cd:motion.spindle-speed/1"))

[<Fact>]
let ``ADR-008 id shape rejects v-prefix (legacy Phase 5 draft)`` () =
    Assert.False(ConceptDescription.isValidId (SemanticId "urn:dualsoft:cd:motion.spindle-speed/v1/0"))

[<Fact>]
let ``ADR-008 id shape rejects wrong prefix`` () =
    Assert.False(ConceptDescription.isValidId (SemanticId "urn:foo:cd:motion.spindle-speed/1/0"))

[<Fact>]
let ``ADR-008 id shape rejects uppercase in path`` () =
    Assert.False(ConceptDescription.isValidId (SemanticId "urn:dualsoft:cd:Motion.SpindleSpeed/1/0"))

[<Fact>]
let ``Register succeeds for valid CD`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let entry = cd "urn:dualsoft:cd:motion.spindle-speed/1/0"
    match reg.Register entry with
    | Ok () -> ()
    | Error msg -> Assert.Fail(msg)

[<Fact>]
let ``Register rejects invalid id shape`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let entry = cd "urn:dualsoft:cd:BadName/1/0"
    match reg.Register entry with
    | Error _ -> ()
    | Ok () -> Assert.Fail "expected Error for uppercase path"

[<Fact>]
let ``Register refuses duplicate id`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let entry = cd "urn:dualsoft:cd:power.active-power/1/0"
    reg.Register entry |> ignore
    match reg.Register entry with
    | Error _ -> ()
    | Ok () -> Assert.Fail "expected duplicate rejection"

[<Fact>]
let ``Get returns registered CD`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let entry = cd "urn:dualsoft:cd:sensor.vibration-rms/1/0"
    reg.Register entry |> ignore
    match reg.Get entry.Id with
    | Some found -> Assert.Equal<ConceptDescription>(entry, found)
    | None -> Assert.Fail "expected Some"

[<Fact>]
let ``Deprecate marks status Deprecated`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let entry = cd "urn:dualsoft:cd:inspection.judgement/1/0"
    reg.Register entry |> ignore
    reg.Deprecate entry.Id |> ignore
    match reg.Get entry.Id with
    | Some found -> Assert.Equal(Deprecated, found.Status)
    | None -> Assert.Fail "expected CD present after deprecate"

[<Fact>]
let ``Deprecate returns Error for unknown id`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    let unknown = SemanticId "urn:dualsoft:cd:unknown.thing/1/0"
    match reg.Deprecate unknown with
    | Error _ -> ()
    | Ok () -> Assert.Fail "expected Error for unknown id"

[<Fact>]
let ``List filter substring matches id and preferredName`` () =
    let reg = InMemoryConceptDescriptionRegistry() :> IConceptDescriptionRegistry
    reg.Register (cd "urn:dualsoft:cd:motion.spindle-speed/1/0") |> ignore
    reg.Register (cd "urn:dualsoft:cd:sensor.vibration-rms/1/0") |> ignore
    let hits = reg.List (Some "motion") |> Seq.toList
    Assert.Single(hits) |> ignore
