module Ds2.OpcUa.Server.Tests.AasFileScannerTests

open System
open System.IO
open System.Threading
open Ds2.OpcUa.Server.AasClient
open Xunit

// Phase 3 · AasFileScanner 라운드트립.
//
// 목표:
//   - 최초 Reload → 모든 파일이 Added
//   - 변경 없는 Reload → []
//   - LastWriteUtc 갱신 → Modified
//   - 파일 삭제 → Removed
//   - Added → Modified → Removed 상태 사이클
//   - 4 종 (Shell / Submodel / ConceptDescription / Package) 모두 커버
//   - 파일 확장자: json (Shell/Submodel/CD), aasx (Package)
//
// 격리: 각 테스트마다 GUID temp 디렉토리 사용.

let private newRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "ds2-aid-store-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private extOf =
    function
    | Package -> ".aasx"
    | _       -> ".json"

let private subdirOf =
    function
    | Shell              -> "shells"
    | Submodel           -> "submodels"
    | ConceptDescription -> "concept-descriptions"
    | Package            -> "packages"

let private writeFile (root: string) kind (idBase64: string) (payload: byte[]) =
    let dir = Path.Combine(root, subdirOf kind)
    Directory.CreateDirectory dir |> ignore
    let p = Path.Combine(dir, idBase64 + extOf kind)
    File.WriteAllBytes(p, payload)
    p

let private deleteFile (root: string) kind (idBase64: string) =
    let p = Path.Combine(root, subdirOf kind, idBase64 + extOf kind)
    File.Delete p

let private cleanup (root: string) =
    try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``Empty root - first Reload returns empty list`` () =
    let root = newRoot ()
    try
        let scanner = AasFileScanner(root)
        let changes = scanner.Reload()
        Assert.Empty changes
    finally
        cleanup root

[<Fact>]
let ``First Reload returns Added for existing files across all kinds`` () =
    let root = newRoot ()
    try
        writeFile root Shell              "aas-1" [| 0uy |] |> ignore
        writeFile root Submodel           "sm-1"  [| 0uy |] |> ignore
        writeFile root ConceptDescription "cd-1"  [| 0uy |] |> ignore
        writeFile root Package            "pkg-1" [| 0uy |] |> ignore

        let scanner = AasFileScanner(root)
        let changes = scanner.Reload()

        Assert.Equal(4, List.length changes)
        Assert.Contains(Added(Shell,              "aas-1"), changes)
        Assert.Contains(Added(Submodel,           "sm-1"),  changes)
        Assert.Contains(Added(ConceptDescription, "cd-1"),  changes)
        Assert.Contains(Added(Package,            "pkg-1"), changes)
    finally
        cleanup root

[<Fact>]
let ``Second Reload with no changes returns empty list`` () =
    let root = newRoot ()
    try
        writeFile root Shell    "aas-1" [| 1uy |] |> ignore
        writeFile root Submodel "sm-1"  [| 1uy |] |> ignore

        let scanner = AasFileScanner(root)
        let first = scanner.Reload()
        Assert.Equal(2, List.length first)

        let second = scanner.Reload()
        Assert.Empty second
    finally
        cleanup root

[<Fact>]
let ``Adding files between Reloads returns only new Added deltas`` () =
    let root = newRoot ()
    try
        writeFile root Shell "aas-1" [| 1uy |] |> ignore
        let scanner = AasFileScanner(root)
        scanner.Reload() |> ignore

        writeFile root Shell    "aas-2" [| 1uy |] |> ignore
        writeFile root Submodel "sm-1"  [| 1uy |] |> ignore

        let changes = scanner.Reload()
        Assert.Equal(2, List.length changes)
        Assert.Contains(Added(Shell,    "aas-2"), changes)
        Assert.Contains(Added(Submodel, "sm-1"),  changes)
    finally
        cleanup root

[<Fact>]
let ``Modifying file LastWriteUtc returns Modified`` () =
    let root = newRoot ()
    try
        let p = writeFile root Shell "aas-1" [| 1uy |]
        let scanner = AasFileScanner(root)
        scanner.Reload() |> ignore

        // FS resolution 회피 — 명시적 backdate 후 재기록.
        let past = DateTime.UtcNow.AddSeconds(-10.0)
        File.SetLastWriteTimeUtc(p, past)
        // 스냅샷 재정렬을 위해 한번 더 Reload → 여기서 Modified 방출 (스캐너의 prev 갱신).
        scanner.Reload() |> ignore

        File.WriteAllBytes(p, [| 2uy |])
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow)

        let changes = scanner.Reload()
        Assert.Equal(1, List.length changes)
        Assert.Contains(Modified(Shell, "aas-1"), changes)
    finally
        cleanup root

[<Fact>]
let ``Deleting file returns Removed`` () =
    let root = newRoot ()
    try
        writeFile root Shell    "aas-1" [| 1uy |] |> ignore
        writeFile root Submodel "sm-1"  [| 1uy |] |> ignore
        let scanner = AasFileScanner(root)
        scanner.Reload() |> ignore

        deleteFile root Shell "aas-1"

        let changes = scanner.Reload()
        Assert.Equal(1, List.length changes)
        Assert.Contains(Removed(Shell, "aas-1"), changes)
    finally
        cleanup root

[<Fact>]
let ``Full state cycle - Added then Modified then Removed`` () =
    let root = newRoot ()
    try
        let scanner = AasFileScanner(root)

        // 1) Added
        let p = writeFile root Submodel "sm-cycle" [| 1uy |]
        let past = DateTime.UtcNow.AddSeconds(-5.0)
        File.SetLastWriteTimeUtc(p, past)
        let addedChanges = scanner.Reload()
        Assert.Equal(1, List.length addedChanges)
        Assert.Contains(Added(Submodel, "sm-cycle"), addedChanges)

        // 2) Modified
        File.WriteAllBytes(p, [| 2uy |])
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow)
        let modifiedChanges = scanner.Reload()
        Assert.Equal(1, List.length modifiedChanges)
        Assert.Contains(Modified(Submodel, "sm-cycle"), modifiedChanges)

        // 3) Removed
        deleteFile root Submodel "sm-cycle"
        let removedChanges = scanner.Reload()
        Assert.Equal(1, List.length removedChanges)
        Assert.Contains(Removed(Submodel, "sm-cycle"), removedChanges)
    finally
        cleanup root

[<Fact>]
let ``Mixed Add + Modify + Remove in single Reload`` () =
    let root = newRoot ()
    try
        let pShell = writeFile root Shell    "aas-modify" [| 1uy |]
        let _      = writeFile root Submodel "sm-remove"  [| 1uy |]
        let scanner = AasFileScanner(root)
        File.SetLastWriteTimeUtc(pShell, DateTime.UtcNow.AddSeconds(-5.0))
        scanner.Reload() |> ignore

        // modify existing shell
        File.WriteAllBytes(pShell, [| 2uy |])
        File.SetLastWriteTimeUtc(pShell, DateTime.UtcNow)
        // remove existing submodel
        deleteFile root Submodel "sm-remove"
        // add new package
        writeFile root Package "pkg-new" [| 3uy |] |> ignore

        let changes = scanner.Reload()
        Assert.Equal(3, List.length changes)
        Assert.Contains(Modified(Shell,    "aas-modify"), changes)
        Assert.Contains(Removed (Submodel, "sm-remove"),  changes)
        Assert.Contains(Added   (Package,  "pkg-new"),    changes)
    finally
        cleanup root

[<Fact>]
let ``List returns idBase64 values without extension per kind`` () =
    let root = newRoot ()
    try
        writeFile root Shell    "aas-a" [| 0uy |] |> ignore
        writeFile root Shell    "aas-b" [| 0uy |] |> ignore
        writeFile root Submodel "sm-x"  [| 0uy |] |> ignore
        writeFile root Package  "pkg-1" [| 0uy |] |> ignore

        let scanner = AasFileScanner(root)

        let shells = scanner.List Shell |> List.sort
        Assert.Equal<string list>([ "aas-a"; "aas-b" ], shells)

        let submodels = scanner.List Submodel
        Assert.Equal<string list>([ "sm-x" ], submodels)

        let cds = scanner.List ConceptDescription
        Assert.Empty cds

        let packages = scanner.List Package
        Assert.Equal<string list>([ "pkg-1" ], packages)
    finally
        cleanup root

[<Fact>]
let ``TryLoad returns exact bytes for existing file - roundtrip`` () =
    let root = newRoot ()
    try
        let original = System.Text.Encoding.UTF8.GetBytes """{"idShort":"cnc01"}"""
        writeFile root Shell "aas-cnc01" original |> ignore

        let scanner = AasFileScanner(root)

        match scanner.TryLoad(Shell, "aas-cnc01") with
        | Some bytes -> Assert.Equal<byte[]>(original, bytes)
        | None       -> Assert.Fail "TryLoad returned None for existing shell"
    finally
        cleanup root

[<Fact>]
let ``TryLoad returns None for missing file`` () =
    let root = newRoot ()
    try
        let scanner = AasFileScanner(root)
        let result = scanner.TryLoad(Shell, "does-not-exist")
        Assert.True(Option.isNone result)
    finally
        cleanup root

[<Fact>]
let ``Reload isolates by resource kind - same idBase64 in Shell and Submodel produce distinct entries`` () =
    let root = newRoot ()
    try
        writeFile root Shell    "shared-id" [| 0uy |] |> ignore
        writeFile root Submodel "shared-id" [| 0uy |] |> ignore

        let scanner = AasFileScanner(root)
        let changes = scanner.Reload()

        Assert.Equal(2, List.length changes)
        Assert.Contains(Added(Shell,    "shared-id"), changes)
        Assert.Contains(Added(Submodel, "shared-id"), changes)
    finally
        cleanup root
