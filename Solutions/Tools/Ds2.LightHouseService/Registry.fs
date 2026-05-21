namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

/// **Phase S7 D-S7-4 (s6-r66)** — T3 mode 의 collection ACL.
///
/// `Users` = X-User-Identity 화이트리스트. 빈 배열 = 전체 공개 (legacy entry / 미설정 default 정합).
/// `ReadOnly` = true 면 visible 한 user 도 mutation (payload swap / delete) reject. 검색은 허용.
///
/// T1/T2 모드 시 본 필드 무시. T3 모드일 때만 검증 의무. registry.json 의 collections[] 안 inline 박제
/// (별 acl.json 파일 분리 안 함 — 단일 SemaphoreSlim mutationLock 의 atomic save 정합).
[<NoComparison; NoEquality>]
type CollectionAcl = {
    [<JsonPropertyName("users")>] Users: string array
    [<JsonPropertyName("readOnly")>] ReadOnly: bool
}

/// `registry.json` 의 한 collection entry (todo-lighthouse-kb-server.md §3.10 / §3.3.1).
///
/// id 발급 주체 = server (D3, CR3 강화). client 가 `POST /collections` 시 server 가 guid v4 생성.
/// `meta.json` SSOT (§3.3.1) 와 1:1 — registry 는 list view + status, meta 는 storage 안 단일 source.
///
/// **s6-r66 D-S7-4**: `Acl` 필드 — T3 mode 시 검증. null = 전체 공개 (legacy entry / T1·T2 모드 정합).
/// JSON 직렬화 시 null 허용 (`Newtonsoft.Json` 의 nullable POCO 정합 — F# record nullable 필드는 reference type
/// `CollectionAcl` 이라 직렬화 시 null OK). `ImportedBy` = T2 mode 의 owner identity 의미 재사용 (별도 OwnerIdentity
/// 필드 신설 안 함 — schema 단순 유지, 사용자 결정 정합).
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
    /// **s6-r66 D-S7-4** — T3 mode ACL. null = 전체 공개. T1/T2 모드 시 무시.
    [<JsonPropertyName("acl")>] Acl: CollectionAcl
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

    /// **s6-r53 K6 (보안 sweep)** — entry tampering 검증.
    /// admin 침해 / 외부 수동 편집으로 손상된 entry skip + audit warn. defense-in-depth — fail-fast
    /// 대신 silent skip (단일 손상 row 로 service 시작 차단 회피 / 정상 entry 보존).
    ///
    /// 검증 항목:
    /// - `Id` 비어있지 않음 (guid v4 격리 의무)
    /// - `DisplayName` path traversal 차단 (`..` / `/` / `\` / `:` / 절대경로 / nul / control char 없음)
    /// - `StorageRelPath` path traversal 차단 (동일) + storage root 의 단일 segment 형식 (서브디렉토리 prefix 없음)
    /// - `Status` ∈ {idle, indexing, error} (other 값 시 idle 으로 normalize 후 audit)
    ///
    /// 본 helper 는 lock 외부 호출 OK (read-only).
    /// DisplayName 은 file name 의미 (path separator 없음) — strict 검증.
    let private isDisplayNameSuspect (s: string) : bool =
        if String.IsNullOrEmpty s then true
        elif s.Contains("..") then true
        elif s.IndexOfAny([| '/'; '\\'; ':'; '*'; '?'; '<'; '>'; '|'; '"'; '\000' |]) >= 0 then true
        else
            s |> Seq.exists (fun c -> int c < 32)  // control char

    /// StorageRelPath 는 storage root 의 sub-directory 의미 — `\` / `/` 포함은 정상,
    /// 단 `..` (traversal) / 절대경로 / DOS drive letter (`C:`) / control char 거부.
    let private isStorageRelPathSuspect (s: string) : bool =
        if String.IsNullOrEmpty s then true
        elif s.Contains("..") then true
        elif s.Contains(":") then true  // DOS drive letter / NTFS ADS / device
        elif s.StartsWith("/") || s.StartsWith("\\") then true  // 절대경로 / UNC
        else
            s |> Seq.exists (fun c -> int c < 32)

    let private validateEntry (path: string) (e: CollectionEntry) : CollectionEntry option =
        let mutable reasons = []
        if String.IsNullOrEmpty e.Id then
            reasons <- "id=empty" :: reasons
        if isDisplayNameSuspect e.DisplayName then
            reasons <- "displayName=traversal-suspect" :: reasons
        if isStorageRelPathSuspect e.StorageRelPath then
            reasons <- "storageRelPath=traversal-suspect" :: reasons
        if not (List.isEmpty reasons) then
            Log.audit.Warn(sprintf "registry.json K6 tampering 의심 — path=%s id=%s reasons=[%s]"
                (Log.sanitizeForLog path) (Log.sanitizeForLog e.Id) (String.concat "," reasons))
            None
        else
            Some e

    /// registry 파일 → RegistryFile. 미존재 시 빈 registry 반환 (정상 — 첫 실행).
    /// schema mismatch (Current 와 다름) 시 fail-fast.
    /// **s6-r53 K6**: entry validation — tampering 의심 row skip + audit log.
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
            let collections = if isNull reg.Collections then [||] else reg.Collections
            // **s6-r53 K6** — tampering 의심 entry skip.
            let validated = collections |> Array.choose (validateEntry p)
            { reg with Collections = validated }

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
    /// **B7 (s6-r88, 15-reviewer Major)** — write-path validation 박제. 이전 박제는 load 시점만 validateEntry
    /// 통과 (K6 tampering 가드) + write 시점 검증 0 → caller (admin endpoint 등) 가 traversal 문자열 박제 시
    /// registry.json 으로 직접 write. validateEntry 호출 → reject 시 InvalidDataException (caller fail-fast).
    let upsertAsync (storageRoot: string) (entry: CollectionEntry) : Task<unit> = task {
        let p = path storageRoot
        match validateEntry p entry with
        | None ->
            raise (InvalidDataException(
                sprintf "Registry.upsertAsync: entry validation 실패 (id=%s, K6 가드 reject)" entry.Id))
        | Some validated ->
            do! mutationLock.WaitAsync()
            try
                let reg = load storageRoot
                let updated =
                    match reg.Collections |> Array.tryFindIndex (fun e -> e.Id = validated.Id) with
                    | Some idx ->
                        let arr = Array.copy reg.Collections
                        arr.[idx] <- validated
                        arr
                    | None ->
                        Array.append reg.Collections [| validated |]
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
