module Ds2.OpcUa.Server.Tests.NamespaceAllocatorTests

open System
open System.IO
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds
open Xunit

let private mkTemp () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-uaserver-" + Guid.NewGuid().ToString("N"))
    let statePath = Path.Combine(dir, "nodeset-state.json")
    NamespaceAllocator(statePath) :> INamespaceAllocator, dir

[<Fact>]
let ``EnsureForAsset returns Added on first call`` () =
    let alloc, dir = mkTemp()
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:cnc01"
        match alloc.EnsureForAsset gaid with
        | NamespaceAllocationResult.Added _ -> ()
        | _ -> Assert.Fail "expected Added"
    finally Directory.Delete(dir, true)

[<Fact>]
let ``EnsureForAsset returns Existed on repeat`` () =
    let alloc, dir = mkTemp()
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:cnc01"
        let _ = alloc.EnsureForAsset gaid
        match alloc.EnsureForAsset gaid with
        | NamespaceAllocationResult.Existed idx -> Assert.True(idx >= 2)
        | _ -> Assert.Fail "expected Existed"
    finally Directory.Delete(dir, true)

[<Fact>]
let ``Namespace URI includes Base64url of GlobalAssetId`` () =
    let alloc, dir = mkTemp()
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:cnc01"
        let uri = alloc.GlobalAssetIdToUri gaid
        Assert.StartsWith("urn:ds:asset:", uri)
        // Value must be a valid Base64url segment.
        Assert.Matches(@"^urn:ds:asset:[A-Za-z0-9_-]+$", uri)
    finally Directory.Delete(dir, true)

[<Fact>]
let ``nodeset-state.json persists across allocator instances`` () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-uaserver-" + Guid.NewGuid().ToString("N"))
    let statePath = Path.Combine(dir, "nodeset-state.json")
    try
        let alloc1 = NamespaceAllocator(statePath) :> INamespaceAllocator
        let _ = alloc1.EnsureForAsset (GlobalAssetId "urn:test:a")
        let _ = alloc1.EnsureForAsset (GlobalAssetId "urn:test:b")
        Assert.True(File.Exists statePath)

        let alloc2 = NamespaceAllocator(statePath) :> INamespaceAllocator
        Assert.Equal(2, alloc2.GetAll() |> List.length)
        match alloc2.EnsureForAsset (GlobalAssetId "urn:test:a") with
        | NamespaceAllocationResult.Existed _ -> ()
        | _ -> Assert.Fail "expected Existed on second allocator"
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``MarkDeprecated flags namespace`` () =
    let alloc, dir = mkTemp()
    try
        let gaid = GlobalAssetId "urn:test:x"
        let _ = alloc.EnsureForAsset gaid
        let uri = alloc.GlobalAssetIdToUri gaid
        Assert.True(alloc.MarkDeprecated uri)
        match alloc.EnsureForAsset gaid with
        | NamespaceAllocationResult.Deprecated _ -> ()
        | _ -> Assert.Fail "expected Deprecated"
    finally Directory.Delete(dir, true)

[<Fact>]
let ``nsIndex not reused after deprecation`` () =
    let alloc, dir = mkTemp()
    try
        let a = GlobalAssetId "urn:test:a"
        let b = GlobalAssetId "urn:test:b"
        let idxA =
            match alloc.EnsureForAsset a with
            | NamespaceAllocationResult.Added n -> n
            | _ -> 0
        let uriA = alloc.GlobalAssetIdToUri a
        alloc.MarkDeprecated uriA |> ignore
        let idxB =
            match alloc.EnsureForAsset b with
            | NamespaceAllocationResult.Added n -> n
            | _ -> 0
        Assert.NotEqual<int>(idxA, idxB)
        Assert.True(idxB > idxA)
    finally Directory.Delete(dir, true)
