module Ds2.LightHouseService.Tests.StagingSweepTests

open System
open System.IO
open Xunit
open Ds2.LightHouseService

let private withStorage (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-sweep-%s" (Guid.NewGuid().ToString("N")))
    Storage.initialize dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``isValidStagingId — guid 형식만 OK`` () =
    Assert.True(StagingSweep.isValidStagingId (Guid.NewGuid().ToString("D")))
    Assert.True(StagingSweep.isValidStagingId (Guid.NewGuid().ToString("N")))
    Assert.False(StagingSweep.isValidStagingId "")
    Assert.False(StagingSweep.isValidStagingId "../escape")
    Assert.False(StagingSweep.isValidStagingId "C:\\evil")
    Assert.False(StagingSweep.isValidStagingId "not-a-guid")

[<Fact>]
let ``removeStaging — 디렉토리 + .tmp 두 형식 모두 제거`` () = withStorage (fun root ->
    let id = Guid.NewGuid().ToString("D")
    let stagingDir = Storage.stagingDir root
    let asDir = Path.Combine(stagingDir, id)
    let asTmp = Path.Combine(stagingDir, id + ".tmp")
    Directory.CreateDirectory asDir |> ignore
    File.WriteAllText(asTmp, "")
    Assert.True(StagingSweep.removeStaging root id)
    Assert.False(Directory.Exists asDir)
    Assert.False(File.Exists asTmp)
    // 다시 호출 → 둘 다 미존재 → false
    Assert.False(StagingSweep.removeStaging root id))

[<Fact>]
let ``removeStaging — invalid id 거부`` () = withStorage (fun root ->
    Assert.False(StagingSweep.removeStaging root "../escape")
    Assert.False(StagingSweep.removeStaging root "not-a-guid"))

[<Fact>]
let ``sweepStale — maxAge 이상 오래된 entry 제거`` () = withStorage (fun root ->
    let stagingDir = Storage.stagingDir root
    // 오래된 entry — LastWriteTime 1시간 전
    let oldDir = Path.Combine(stagingDir, "old-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory oldDir |> ignore
    let oldTime = DateTime.UtcNow.AddHours(-2.0)
    Directory.SetLastWriteTimeUtc(oldDir, oldTime)
    // 신규 entry — 지금 막 생성
    let newDir = Path.Combine(stagingDir, "new-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory newDir |> ignore
    // maxAge = 1시간 — old 만 제거
    let removed = StagingSweep.sweepStale root (TimeSpan.FromHours(1.0))
    Assert.Equal(1, removed)
    Assert.False(Directory.Exists oldDir)
    Assert.True(Directory.Exists newDir))

[<Fact>]
let ``sweepStale — staging 폴더 미존재 시 0 (no throw)`` () =
    let bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Assert.Equal(0, StagingSweep.sweepStale bogus (TimeSpan.FromMinutes(1.0)))
