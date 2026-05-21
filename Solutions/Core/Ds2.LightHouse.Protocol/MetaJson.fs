namespace Ds2.LightHouse.Protocol

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// **K4 Protocol SSOT (A2)** — `meta.json` schema 단일 박제 (todo-lighthouse-kb-server.md §3.3.1).
///
/// **본 record 박제 이전** = server F# `Ds2.LightHouseService.MetaJson` + client C# `CollectionPackager.MetaPayload`
/// (Promaker) + client F# `Packager.MetaDto` (cli) 3중 박제. K4 통합으로 단일 SSOT — schema 변경 시 본 record
/// 1회만 박제로 양측 컴파일러 강제.
///
/// client (CollectionPackager / cli Packager) ↔ service (sanitize + import) 단일 schema. camelCase.
/// 필드는 client 가 채우는 것 / server 가 import 시 채우는 것 두 그룹 — runtime 에 *모두 동일 record* 로 표현.
/// 양쪽이 채우지 않는 시점에는 null / "" / 0 으로 잔류.
///
/// `[<CLIMutable>]` — System.Text.Json 의 reflection 이 mutable property 로 deserialize 가능
/// (cli F# 의 기존 `MetaDto` 박제 정합 — private record 시 schemaVersion=0 silently 직렬화 회귀).
/// C# 측 (`Promaker.CollectionPackager`) 도 default ctor + property setter 패턴 사용 가능.
[<CLIMutable; NoComparison; NoEquality>]
type MetaJson = {
    // ── client 가 채움 ──────────────────────────────────────────────────
    [<JsonPropertyName("schemaVersion")>] SchemaVersion: int
    [<JsonPropertyName("indexerVersion")>] IndexerVersion: string
    [<JsonPropertyName("title")>] Title: string
    [<JsonPropertyName("sourcePathHint")>] SourcePathHint: string
    [<JsonPropertyName("fileCount")>] FileCount: int
    [<JsonPropertyName("totalSourceBytes")>] TotalSourceBytes: int64
    [<JsonPropertyName("createdAt")>] CreatedAt: string
    [<JsonPropertyName("clientHost")>] ClientHost: string
    [<JsonPropertyName("clientUser")>] ClientUser: string

    // ── server 가 import 시 채움 (client 가 보낸 값은 무시) ────────────
    [<JsonPropertyName("id")>] Id: string
    [<JsonPropertyName("importedAt")>] ImportedAt: string
    [<JsonPropertyName("importedBy")>] ImportedBy: string
    [<JsonPropertyName("storageRelPath")>] StorageRelPath: string
}


[<RequireQualifiedAccess>]
module MetaJsonSchema =
    /// 현재 schema version. client 가 본 값을 박제, server 가 load 시 mismatch reject.
    /// 본 field 변경은 wire breaking — paired-release 검사 의무.
    [<Literal>]
    let Current = 1


/// **A2 module 이름 분리 (2026-05-20)** — record `MetaJson` (type) 과 동명 module 충돌 시 F# 컴파일러가 module 의 IL
/// 이름을 `MetaJsonModule` 로 자동 suffix → C# caller 의 `MetaJson.FileName` 호출 fail (CS0117). 본 module 명을
/// `MetaJsonIO` 로 변경하여 F# / C# 양측 호환. F# 측 호출 = `MetaJsonIO.load / save / stampServerFields / path /
/// FileName / SubDir / jsonOptions() / emptyClientFill`. C# 측 호출 = 동일 (`Ds2.LightHouse.Protocol.MetaJsonIO.X`).
[<RequireQualifiedAccess>]
module MetaJsonIO =

    /// `meta.json` 파일 이름 SSOT. 양분 박제 (Promaker / cli / server) → 본 module 1회 박제로 통합.
    [<Literal>]
    let FileName = "meta.json"

    /// `meta.json` 이 들어가는 sub-directory — `ZipLayout.KbFolderName` 정합 (`.lighthouse-kb`).
    /// 본 [<Literal>] 은 caller 코드에서 patterns matching 시 string literal 박제 호환 (FS0815 회피).
    /// `ZipLayout.KbFolderName` 의 literal 값과 동일 — drift 시 컴파일 오류는 안 나지만 본 module 내 invariant.
    [<Literal>]
    let SubDir = ".lighthouse-kb"

    /// System.Text.Json options SSOT — `[<JsonPropertyName>]` attribute 정합으로 camelCase 자동.
    /// `JsonIgnoreCondition.Never` — server stamp 필드의 빈 문자열도 wire 에 박제 (client 측이 명시적 stamp path).
    let jsonOptions () =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)

    /// collection 디렉토리 → `<dir>/.lighthouse-kb/meta.json` 절대경로.
    let path (collectionDir: string) : string =
        Path.Combine(collectionDir, SubDir, FileName)

    /// 파일 → MetaJson. 미존재 시 FileNotFoundException — caller fail-fast.
    /// schemaVersion mismatch 시 InvalidDataException (forward-compat — 미정의 필드는 reject 안 함).
    let load (collectionDir: string) : MetaJson =
        let p = path collectionDir
        if not (File.Exists p) then
            raise (FileNotFoundException(sprintf "meta.json 미존재 — %s" p, p))
        let json = File.ReadAllText(p, Encoding.UTF8)
        let meta = JsonSerializer.Deserialize<MetaJson>(json, jsonOptions())
        if obj.ReferenceEquals(meta, null) then
            raise (InvalidDataException(sprintf "meta.json 역직렬화 실패 — %s" p))
        if meta.SchemaVersion <> MetaJsonSchema.Current then
            raise (InvalidDataException(
                sprintf "meta.json schemaVersion=%d 가 supported=%d 와 불일치"
                    meta.SchemaVersion MetaJsonSchema.Current))
        meta

    /// atomic save — `.tmp` write 후 File.Replace / File.Move (Registry 와 동일 패턴).
    /// `.lighthouse-kb/` 부재 시 자동 생성 (첫 save 시점).
    let save (collectionDir: string) (meta: MetaJson) =
        let p = path collectionDir
        let dir = Path.GetDirectoryName p
        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore
        let tmp = p + ".tmp"
        let json = JsonSerializer.Serialize(meta, jsonOptions())
        File.WriteAllText(tmp, json, Encoding.UTF8)
        if File.Exists p then
            File.Replace(tmp, p, null, ignoreMetadataErrors = true)
        else
            File.Move(tmp, p)

    /// client 가 보낸 meta 의 server 필드 (id / importedAt / importedBy / storageRelPath) 를 채워서 반환.
    /// client 가 미리 채운 server 필드 값은 무시 (§3.3.1 SSOT — "server 가 client 가 보낸 값 무시").
    let stampServerFields
        (id: string)
        (importedBy: string)
        (storageRelPath: string)
        (clientMeta: MetaJson)
        : MetaJson =
        let nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        { clientMeta with
            Id = id
            ImportedAt = nowUtc
            ImportedBy = importedBy
            StorageRelPath = storageRelPath }

