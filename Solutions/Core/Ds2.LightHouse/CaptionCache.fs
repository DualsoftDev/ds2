namespace Ds2.LightHouse

open System
open System.IO
open Microsoft.Data.Sqlite
open Newtonsoft.Json
open Newtonsoft.Json.Linq

/// **P-R3a (todo-documents-based-gfm.md §6.1 N16)** — caption cache JSON 박제 helper.
///
/// 목적: `.lighthouse-kb/` wipe 직전 ImageCache.CaptionText (Anthropic VLM API call 결과) 를
/// `<sourceFolder>/.lighthouse-caption-cache.json` 으로 박제 → 재색인 시 caption 생성 closure 가
/// 동일 image hash 의 cache 우선 lookup → Anthropic call skip path 도입. 사용자 결정 (1a 즉시) —
/// "image hash 와 caption 만 추출해서 어딘가 임시 저장해 놓고, 색인시, caption 생성할 때
/// 저장된 정보 있으면 그걸 사용".
///
/// 책임 경계:
/// - `saveFromDb` — index.db 의 ImageCache table 에서 caption 박제된 row sweep → JSON file
/// - `tryLookup` — sourceFolder 기준 cache file 조회 (없거나 corrupt 시 None, fail-safe)
/// - `cacheFilePath` — SSOT path helper (`<sourceFolder>/.lighthouse-caption-cache.json`)
///
/// JSON schema = `{ "version":1, "savedAt":"<ISO>", "captions":{ "<hash>": { "captionText","captionModel","capturedAt" } } }`
/// version mismatch / corrupt JSON / file 부재 모두 `None` 반환 (예외 0, logWarn 만 박제).
[<RequireQualifiedAccess>]
module CaptionCache =

    /// cache 파일명 — `<sourceFolder>/.lighthouse-caption-cache.json`. SSOT.
    [<Literal>]
    let CacheFileName = ".lighthouse-caption-cache.json"

    /// JSON schema 버전 — version mismatch 시 tryLookup 이 None 반환 + saveFromDb 는 본 값 박제.
    [<Literal>]
    let SchemaVersion = 1

    /// sourceFolder → cache file 절대 경로. SSOT helper.
    let cacheFilePath (sourceFolder: string) : string =
        Path.Combine(sourceFolder, CacheFileName)

    /// `index.db` 의 `ImageCache` table 에서 CaptionText 박제된 row sweep → cache file 박제.
    ///
    /// SQL: `SELECT ImageHash, CaptionText, CaptionModel, CaptionAt FROM ImageCache
    ///       WHERE CaptionText IS NOT NULL AND CaptionText != ''`.
    /// 반환 = 박제된 row 수. 0 row 면 빈 cache file 박제 (멱등 — 다음 saveFromDb 호출이 동일 결과 + savedAt 만 갱신).
    ///
    /// caller (`Packager.resetKbDir`) 가 wipe 직전 호출. 실패 시 caller 가 try/with 로 흡수 (fail-safe — 색인 자체는 진행).
    let saveFromDb (sourceFolder: string) (conn: SqliteConnection) : int =
        let captions = JObject()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT ImageHash, CaptionText, CaptionModel, CaptionAt
            FROM ImageCache
            WHERE CaptionText IS NOT NULL AND CaptionText != ''
        """
        use reader = cmd.ExecuteReader()
        let mutable n = 0
        while reader.Read() do
            let hash = reader.GetString 0
            let text = reader.GetString 1
            let model = if reader.IsDBNull 2 then "" else reader.GetString 2
            let at = if reader.IsDBNull 3 then "" else reader.GetString 3
            let row = JObject()
            row.["captionText"] <- JValue(text)
            row.["captionModel"] <- JValue(model)
            row.["capturedAt"] <- JValue(at)
            captions.[hash] <- row
            n <- n + 1
        let root = JObject()
        root.["version"] <- JValue(SchemaVersion)
        root.["savedAt"] <- JValue(DateTime.UtcNow.ToString("o"))
        root.["captions"] <- captions
        let json = root.ToString(Formatting.Indented)
        let path = cacheFilePath sourceFolder
        // 부모 폴더는 sourceFolder 자체라 항상 존재 (caller 보장). file 만 UTF-8 박제.
        File.WriteAllText(path, json, Text.UTF8Encoding(false))
        n

    /// cache file 에서 hash 매칭 row 조회. 미존재/corrupt/version mismatch → `None` (fail-safe).
    ///
    /// caller (`Vlm.buildCaptionGen` 의 반환 closure) 가 Anthropic API call 진입 *전* 본 함수 호출 →
    /// Some 시 (text, model) 박제로 `Captioned` 반환, None 시 기존 흐름 (callAnthropic) 진입.
    ///
    /// 실패 mode (모두 None + logWarn):
    /// - file 부재 → 정상 (cache 없음, 신규 색인)
    /// - JSON parse 실패 → 진단 신호 (외부 도구가 손상시킴 / 부분 write)
    /// - version mismatch → 진단 신호 (lib 갱신 후 구 cache 호환 안 됨)
    /// - hash 미존재 → 정상 (cache 에 해당 image 없음)
    let tryLookup (sourceFolder: string) (hash: string) : (string * string) option =
        let path = cacheFilePath sourceFolder
        if not (File.Exists path) then None
        else
            try
                let json = File.ReadAllText(path, Text.UTF8Encoding(false))
                let root = JObject.Parse(json)
                let version =
                    match root.TryGetValue("version") with
                    | true, v -> v.Value<int>()
                    | _ -> -1
                if version <> SchemaVersion then
                    Log.lighthouse.Warn(
                        sprintf "CaptionCache: version mismatch — expected=%d actual=%d (%s)"
                            SchemaVersion version path)
                    None
                else
                    let captions =
                        match root.TryGetValue("captions") with
                        | true, v when v.Type = JTokenType.Object -> v :?> JObject
                        | _ -> JObject()
                    match captions.TryGetValue(hash) with
                    | true, rowTok when rowTok.Type = JTokenType.Object ->
                        let row = rowTok :?> JObject
                        let text =
                            match row.TryGetValue("captionText") with
                            | true, v -> v.Value<string>()
                            | _ -> null
                        let model =
                            match row.TryGetValue("captionModel") with
                            | true, v -> v.Value<string>()
                            | _ -> null
                        if isNull text || System.String.IsNullOrWhiteSpace text then None
                        else
                            let modelSafe = if isNull model then "" else model
                            Some (text, modelSafe)
                    | _ -> None
            with ex ->
                Log.lighthouse.Warn(
                    sprintf "CaptionCache: 읽기 실패 (재색인 진행, cache 무시) — %s: %s"
                        (ex.GetType().Name) ex.Message)
                None
