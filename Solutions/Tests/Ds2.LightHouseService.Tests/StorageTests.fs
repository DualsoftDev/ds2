module Ds2.LightHouseService.Tests.StorageTests

open System
open System.IO
open Xunit
open Ds2.LightHouseService

let private withTempRoot (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-storage-%s" (Guid.NewGuid().ToString("N")))
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``initialize — 4 subdir 자동 생성`` () =
    withTempRoot (fun root ->
        let resolved = Storage.initialize root
        Assert.Equal(root, resolved)
        Assert.True(Directory.Exists (Storage.collectionsDir root))
        Assert.True(Directory.Exists (Storage.stagingDir root))
        Assert.True(Directory.Exists (Storage.logsDir root))
        Assert.True(Directory.Exists (Storage.auditDir root)))

[<Fact>]
let ``initialize — envvar 전개`` () =
    let envvar = "%TEMP%\\lhs-env-" + Guid.NewGuid().ToString("N")
    let resolved = Storage.initialize envvar
    try
        Assert.DoesNotContain("%TEMP%", resolved)
        Assert.True(Directory.Exists (Storage.collectionsDir resolved))
    finally
        try Directory.Delete(resolved, true) with _ -> ()

[<Fact>]
let ``initialize — 빈 storageRoot 는 ArgumentException`` () =
    Assert.Throws<ArgumentException>(fun () -> Storage.initialize "" |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> Storage.initialize "   " |> ignore) |> ignore

[<Fact>]
let ``initialize idempotent — 2회 호출 OK (probe 파일 잔재 0)`` () =
    withTempRoot (fun root ->
        Storage.initialize root |> ignore
        Storage.initialize root |> ignore
        // .probe-* 잔재 없는지 확인
        let probeRemains = Directory.GetFiles(Storage.logsDir root, ".probe-*") |> Array.length
        Assert.Equal(0, probeRemains))
