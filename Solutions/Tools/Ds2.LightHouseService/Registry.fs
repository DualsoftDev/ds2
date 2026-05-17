namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

/// `registry.json` 의 한 collection entry (todo-lighthouse-kb-server.md §3.10 / §3.3.1).
///
/// id 발급 주체 = server (D3, CR3 강화). client 가 `POST /collections` 시 server 가 guid v4 생성.
/// `meta.json` SSOT (§3.3.1) 와 1:1 — registry 는 list view + status, meta 는 storage 안 단일 source.
[<NoComparison; NoEquality>]
type CollectionEntry = {
    [<JsonPropertyName("id")>] Id: string
    [<JsonPropertyName("displayName")>] DisplayName: string
    [<JsonPropertyName("indexerVersion")>] IndexerVersion: string
    [<JsonPropertyName("fileCount")>] FileCount: int
    [<JsonPropertyName("totalSourceBytes")>] TotalSourceBytes: int64
    [<JsonPropertyName("createdAt")>] CreatedAt: string
    [<JsonPropertyName("importedAt")>] ImportedAt: string
    [<JsonPropertyName("importedBy")>] ImportedBy: string
    [<JsonPropertyName("storageRelPath")>] StorageRelPath: string
    /// idle | indexing | error. Phase S2 = idle / error 둘 (indexing 은 client 측 진행률 의미).
    [<JsonPropertyName("status")>] Status: string
    [<JsonPropertyName("errorReason")>] ErrorReason: string
    [<JsonPropertyName("lastImportedAt")>] LastImportedAt: string
}

/// `registry.json` root 형식.
type RegistryFile = {
    [<JsonPropertyName("schemaVersion")>] SchemaVersion: int
    [<JsonPropertyName("collections")>] Collections: CollectionEntry array
}


/// registry.json 의 schema 버전 SSOT.
/// schema 변경 (CollectionEntry 필드 추가/제거) 시 bump + migration 함수 추가 (Phase S2 = v=1 만).
[<RequireQualifiedAccess>]
module RegistrySchema =
    [<Literal>]
    let Current = 1


/// `%PROGRAMDATA%\Dualsoft\LightHouseService\registry.json` CRUD.
///
/// mutation 은 `SemaphoreSlim(1, 1)` 직렬화 (CR5 / §3.9.1). read 는 lock-free OK (read-time snapshot).
/// atomic save 패턴 — write to `.tmp` → flush → File.Replace 또는 rename.
[<RequireQualifiedAccess>]
module Registry =

    [<Literal>]
    let FileName = "registry.json"

    /// process-wide 직렬화 lock (CR5). 같은 process 안 모든 mutation API 가 본 lock 통과.
    let private mutationLock = new SemaphoreSlim(1, 1)

    let private jsonOptions () =
        let opts = JsonSerializerOptions(
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never)
        opts

    /// storage root → `<root>/registry.json` 절대경로.
    let path (storageRoot: string) : string =
        Path.Combine(storageRoot, FileName)

    /// 빈 registry — 미존재 시 default.
    let empty () : RegistryFile =
        { SchemaVersion = RegistrySchema.Current; Collections = [||] }

    /// registry 파일 → RegistryFile. 미존재 시 빈 registry 반환 (정상 — 첫 실행).
    /// schema mismatch (Current 와 다름) 시 fail-fast.
    let load (storageRoot: string) : RegistryFile =
        let p = path storageRoot
        if not (File.Exists p) then empty ()
        else
            let json = File.ReadAllText(p, Encoding.UTF8)
            let reg = JsonSerializer.Deserialize<RegistryFile>(json, jsonOptions())
            if obj.ReferenceEquals(reg, null) then
                raise (InvalidDataException(sprintf "registry.json 역직렬화 실패 — %s" p))
            if reg.SchemaVersion <> RegistrySchema.Current then
                raise (InvalidDataException(
                    sprintf "registry.json schemaVersion=%d 가 supported=%d 와 불일치 — migration 필요"
                        reg.SchemaVersion RegistrySchema.Current))
            // null Collections 가드 — JSON 의 `"collections": null` 대응
            if isNull reg.Collections then { reg with Collections = [||] } else reg

    /// atomic save — `.tmp` 에 write 후 File.Replace (기존 파일 있으면) 또는 File.Move.
    /// SemaphoreSlim 안에서만 호출 — 본 함수 자체는 lock 안 잡음.
    let private saveUnlocked (storageRoot: string) (reg: RegistryFile) =
        let p = path storageRoot
        let tmp = p + ".tmp"
        let json = JsonSerializer.Serialize(reg, jsonOptions())
        File.WriteAllText(tmp, json, Encoding.UTF8)
        if File.Exists p then
            File.Replace(tmp, p, null, ignoreMetadataErrors = true)
        else
            File.Move(tmp, p)

    /// upsert — entry.Id 가 일치하는 항목 갱신, 없으면 추가. SemaphoreSlim 직렬화 (CR5).
    let upsertAsync (storageRoot: string) (entry: CollectionEntry) : Task<unit> = task {
        do! mutationLock.WaitAsync()
        try
            let reg = load storageRoot
            let updated =
                match reg.Collections |> Array.tryFindIndex (fun e -> e.Id = entry.Id) with
                | Some idx ->
                    let arr = Array.copy reg.Collections
                    arr.[idx] <- entry
                    arr
                | None ->
                    Array.append reg.Collections [| entry |]
            saveUnlocked storageRoot { reg with Collections = updated }
        finally
            mutationLock.Release() |> ignore
    }

    /// remove — id 일치 항목 제거. 반환 = 실제 제거됐는지 (없으면 false). SemaphoreSlim 직렬화.
    let removeAsync (storageRoot: string) (id: string) : Task<bool> = task {
        do! mutationLock.WaitAsync()
        try
            let reg = load storageRoot
            let remaining = reg.Collections |> Array.filter (fun e -> e.Id <> id)
            if remaining.Length = reg.Collections.Length then return false
            else
                saveUnlocked storageRoot { reg with Collections = remaining }
                return true
        finally
            mutationLock.Release() |> ignore
    }

    /// read-only list. lock 미사용 (read-time snapshot) — concurrent mutation 도중 호출되어도
    /// `load` 가 file system 의 atomic rename 결과 (이전 또는 이후 snapshot) 만 봄.
    let listSnapshot (storageRoot: string) : CollectionEntry array =
        (load storageRoot).Collections

    /// 특정 id 의 entry. 없으면 None.
    let tryFindById (storageRoot: string) (id: string) : CollectionEntry option =
        listSnapshot storageRoot
        |> Array.tryFind (fun e -> e.Id = id)

    /// 본 lock 의 in-flight queue depth (테스트/진단용). 운영 코드에서는 사용 안 함.
    let internal currentLockCount () : int = mutationLock.CurrentCount
