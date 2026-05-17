namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// `meta.json` schema SSOT (todo-lighthouse-kb-server.md §3.3.1).
///
/// client (CollectionPackager) ↔ service (sanitize + import) 단일 schema. camelCase.
/// 필드는 client 가 채우는 것 / server 가 import 시 채우는 것 두 그룹 — runtime 에 *모두 동일 record* 로 표현.
/// 양쪽이 채우지 않는 시점에는 null / "" / 0 으로 잔류.
[<NoComparison; NoEquality>]
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
    [<Literal>]
    let Current = 1


[<RequireQualifiedAccess>]
module MetaJson =

    [<Literal>]
    let FileName = "meta.json"

    let private jsonOptions () =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)

    /// collection 디렉토리 → `<dir>/meta.json` 절대경로.
    let path (collectionDir: string) : string =
        Path.Combine(collectionDir, FileName)

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
    let save (collectionDir: string) (meta: MetaJson) =
        let p = path collectionDir
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
        let nowUtc = DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture)
        { clientMeta with
            Id = id
            ImportedAt = nowUtc
            ImportedBy = importedBy
            StorageRelPath = storageRelPath }

    /// CollectionEntry 변환 — Registry 에 upsert 할 때 사용.
    /// meta 의 file/byte count 가 0 인 경우 (0-doc collection, MA18) 그대로 보존.
    let toRegistryEntry (meta: MetaJson) : CollectionEntry =
        { Id = meta.Id
          DisplayName = meta.Title
          IndexerVersion = meta.IndexerVersion
          FileCount = meta.FileCount
          TotalSourceBytes = meta.TotalSourceBytes
          CreatedAt = meta.CreatedAt
          ImportedAt = meta.ImportedAt
          ImportedBy = meta.ImportedBy
          StorageRelPath = meta.StorageRelPath
          Status = "idle"
          ErrorReason = null
          LastImportedAt = meta.ImportedAt }
