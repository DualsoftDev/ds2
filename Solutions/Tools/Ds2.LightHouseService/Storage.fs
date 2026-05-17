namespace Ds2.LightHouseService

open System
open System.IO

/// storage layout 초기화 (todo-lighthouse-kb-server.md §3.10).
///
/// Phase S1 책임: `Collections/` / `Staging/` / `Logs/` / `Audit/` 폴더 자동 생성 + permission probe.
/// `registry.json` 신설 (빈 registry) 은 Phase S2 책임 — 본 phase 에서는 폴더만.
[<RequireQualifiedAccess>]
module Storage =

    [<Literal>]
    let CollectionsSubdir = "Collections"

    [<Literal>]
    let StagingSubdir = "Staging"

    [<Literal>]
    let LogsSubdir = "Logs"

    [<Literal>]
    let AuditSubdir = "Audit"

    /// storage root 안에 4 subdir 자동 생성 + 쓰기 가능 probe.
    /// storage root 미존재 시 자동 생성. permission 부족 시 UnauthorizedAccessException reraise (fail-fast).
    let initialize (storageRoot: string) =
        if String.IsNullOrWhiteSpace storageRoot then
            raise (ArgumentException("storageRoot 가 비어있음"))

        let expanded = Environment.ExpandEnvironmentVariables storageRoot
        Directory.CreateDirectory expanded |> ignore

        for sub in [ CollectionsSubdir; StagingSubdir; LogsSubdir; AuditSubdir ] do
            let dir = Path.Combine(expanded, sub)
            Directory.CreateDirectory dir |> ignore

        // 쓰기 가능 probe — Logs/.probe-<guid> 파일 생성/삭제. permission 미달 시 즉시 fail-fast.
        let logsDir = Path.Combine(expanded, LogsSubdir)
        let probe = Path.Combine(logsDir, ".probe-" + Guid.NewGuid().ToString("N"))
        File.WriteAllText(probe, "")
        File.Delete probe
        expanded

    /// 각 subdir 의 절대 경로 helper.
    let collectionsDir (storageRoot: string) = Path.Combine(storageRoot, CollectionsSubdir)
    let stagingDir (storageRoot: string) = Path.Combine(storageRoot, StagingSubdir)
    let logsDir (storageRoot: string) = Path.Combine(storageRoot, LogsSubdir)
    let auditDir (storageRoot: string) = Path.Combine(storageRoot, AuditSubdir)
