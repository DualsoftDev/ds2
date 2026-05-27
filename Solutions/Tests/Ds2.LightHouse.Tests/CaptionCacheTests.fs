module Ds2.LightHouse.Tests.CaptionCacheTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Newtonsoft.Json.Linq
open Xunit
open Ds2.LightHouse

/// P-R3a (todo-documents-based-gfm.md §6.1 N16) — `CaptionCache.fs` 회귀 차단.
///
/// scope (spec §(d) 의 11 시나리오):
/// 1. happy path save+load
/// 2. hash hit
/// 3. hash miss
/// 4. 빈 cache file (captions: {})
/// 5. 부재 cache file
/// 6. corrupt JSON
/// 7. version mismatch
/// 8. CaptionText NULL skip
/// 9. CaptionText "" skip
/// 10. 멱등 (2회 호출 동일 결과 + savedAt 만 갱신)
/// 11. integration with captionGen closure (mock VLM API 호출 0회 검증)

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-capcache-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// in-memory style 이지만 file DB 박제 — SqliteStore.openConnection 의 PRAGMA + extension load SSOT 의존.
let private openFresh (dir: string) : SqliteConnection =
    let dbPath = SqliteStore.dbPath dir
    let conn = SqliteStore.openConnection dbPath false
    SqliteStore.ensureSchema conn
    SqliteStore.stampVersion conn
    conn

/// caption 박제된 ImageCache row 박제 helper. hash → caption 결정성 박제.
let private upsertCaption
    (conn: SqliteConnection)
    (hash: string)
    (text: string)
    (model: string)
    : unit =
    ImageStore.upsertImageCache conn hash (sprintf "stored/%s.png" hash) Png None None
    if not (isNull text) && not (String.IsNullOrEmpty text) then
        ImageStore.updateCaption conn hash text model

[<Fact>]
let ``saveFromDb + tryLookup — happy path (3 row 박제 + cache file 박제 + 각 hash hit)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        upsertCaption conn "hash-1" "캡션 첫번째" "claude-sonnet-4-6"
        upsertCaption conn "hash-2" "두번째 caption" "claude-opus-4-7"
        upsertCaption conn "hash-3" "세번째" "claude-test"
        let saved = CaptionCache.saveFromDb dir conn
        Assert.Equal(3, saved)
        // cache file 박제 확인.
        let cachePath = CaptionCache.cacheFilePath dir
        Assert.True(File.Exists cachePath, sprintf "cache file 부재 — %s" cachePath)
        // 각 hash 별 tryLookup hit.
        match CaptionCache.tryLookup dir "hash-1" with
        | Some (text, model) ->
            Assert.Equal("캡션 첫번째", text)
            Assert.Equal("claude-sonnet-4-6", model)
        | None -> Assert.Fail "hash-1 lookup miss"
        match CaptionCache.tryLookup dir "hash-2" with
        | Some (text, model) ->
            Assert.Equal("두번째 caption", text)
            Assert.Equal("claude-opus-4-7", model)
        | None -> Assert.Fail "hash-2 lookup miss"
        match CaptionCache.tryLookup dir "hash-3" with
        | Some (text, _) -> Assert.Equal("세번째", text)
        | None -> Assert.Fail "hash-3 lookup miss")

[<Fact>]
let ``tryLookup — hash miss (cache 에 없는 hash) → None`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        upsertCaption conn "hash-A" "AAA" "model-a"
        let _ = CaptionCache.saveFromDb dir conn
        Assert.Equal(None, CaptionCache.tryLookup dir "hash-Z"))

[<Fact>]
let ``saveFromDb — 빈 ImageCache (CaptionText 박제 row 0) → cache file 박제 + captions:{}`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        // caption 없는 ImageCache row 박제 — saveFromDb 의 WHERE 절이 skip.
        ImageStore.upsertImageCache conn "no-cap" "x" Png None None
        let saved = CaptionCache.saveFromDb dir conn
        Assert.Equal(0, saved)
        let cachePath = CaptionCache.cacheFilePath dir
        Assert.True(File.Exists cachePath)
        // captions: {} 박제 검증.
        let json = File.ReadAllText(cachePath, Text.UTF8Encoding(false))
        let root = JObject.Parse(json)
        let captions = root.["captions"] :?> JObject
        Assert.Equal(0, captions.Count)
        // tryLookup 도 None.
        Assert.Equal(None, CaptionCache.tryLookup dir "no-cap"))

[<Fact>]
let ``tryLookup — 부재 cache file → None (예외 0)`` () =
    withTempDir (fun dir ->
        // cache file 박제 안 함 — tryLookup None 박제 의무.
        Assert.Equal(None, CaptionCache.tryLookup dir "any-hash"))

[<Fact>]
let ``tryLookup — corrupt JSON → None (예외 0, logWarn 만)`` () =
    withTempDir (fun dir ->
        let cachePath = CaptionCache.cacheFilePath dir
        File.WriteAllText(cachePath, "{ this is not valid json", Text.UTF8Encoding(false))
        Assert.Equal(None, CaptionCache.tryLookup dir "any-hash"))

[<Fact>]
let ``tryLookup — version mismatch (version: 99) → None (예외 0)`` () =
    withTempDir (fun dir ->
        let cachePath = CaptionCache.cacheFilePath dir
        let mismatchJson = """{"version":99,"savedAt":"2026-01-01T00:00:00Z","captions":{"h":{"captionText":"x","captionModel":"m","capturedAt":""}}}"""
        File.WriteAllText(cachePath, mismatchJson, Text.UTF8Encoding(false))
        Assert.Equal(None, CaptionCache.tryLookup dir "h"))

[<Fact>]
let ``saveFromDb — CaptionText NULL row 는 skip (sweep 진입 안 함)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        // caption 없는 row + caption 있는 row 혼합.
        ImageStore.upsertImageCache conn "hash-NULL" "x" Png None None  // CaptionText NULL
        upsertCaption conn "hash-OK" "valid caption" "model-x"
        let saved = CaptionCache.saveFromDb dir conn
        Assert.Equal(1, saved)
        // NULL row 는 cache 에 없음.
        Assert.Equal(None, CaptionCache.tryLookup dir "hash-NULL")
        // OK row 는 hit.
        Assert.True(Option.isSome (CaptionCache.tryLookup dir "hash-OK")))

[<Fact>]
let ``saveFromDb — CaptionText 빈 string row 는 skip`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        // 빈 string caption 직접 UPDATE (helper 의 fail-fast 우회 — 회귀 fixture 박제 용도).
        ImageStore.upsertImageCache conn "hash-EMPTY" "x" Png None None
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "UPDATE ImageCache SET CaptionText = '' WHERE ImageHash = $h"
        cmd.Parameters.AddWithValue("$h", "hash-EMPTY") |> ignore
        cmd.ExecuteNonQuery() |> ignore
        upsertCaption conn "hash-OK" "non-empty" "m"
        let saved = CaptionCache.saveFromDb dir conn
        Assert.Equal(1, saved)
        Assert.Equal(None, CaptionCache.tryLookup dir "hash-EMPTY"))

[<Fact>]
let ``saveFromDb — 멱등 (2회 호출 동일 결과 + savedAt 만 갱신)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        upsertCaption conn "hash-1" "first" "m1"
        let saved1 = CaptionCache.saveFromDb dir conn
        Assert.Equal(1, saved1)
        let cachePath = CaptionCache.cacheFilePath dir
        let json1 = File.ReadAllText(cachePath, Text.UTF8Encoding(false))
        let root1 = JObject.Parse(json1)
        let savedAt1 = root1.["savedAt"].Value<string>()
        // sleep — savedAt timestamp 분해능 확보. Newtonsoft.Json 의 default DateParseHandling 이 string
        // 으로 박제된 ISO timestamp 를 DateTime 으로 round-trip parse 하면서 second resolution 까지만 보장
        // (sub-second tick 손실). millisecond 단위 차이가 비교에 묻히지 않도록 1.1s sleep.
        System.Threading.Thread.Sleep 1100
        let saved2 = CaptionCache.saveFromDb dir conn
        Assert.Equal(1, saved2)
        let json2 = File.ReadAllText(cachePath, Text.UTF8Encoding(false))
        let root2 = JObject.Parse(json2)
        let savedAt2 = root2.["savedAt"].Value<string>()
        Assert.NotEqual<string>(savedAt1, savedAt2)
        // captions 내용은 동일 — hash-1 row 유지.
        let captions2 = root2.["captions"] :?> JObject
        Assert.Equal(1, captions2.Count)
        Assert.True(captions2.ContainsKey("hash-1")))

[<Fact>]
let ``cacheFilePath — SSOT path (<sourceFolder>/.lighthouse-caption-cache.json)`` () =
    withTempDir (fun dir ->
        let path = CaptionCache.cacheFilePath dir
        Assert.EndsWith(".lighthouse-caption-cache.json", path)
        // dir 안에 박제 (sourceFolder 직접 자식 — .lighthouse-kb 안 아님).
        Assert.Equal(dir, Path.GetDirectoryName path))

[<Fact>]
let ``Integration — captionGen closure cache hit 시 mock VLM call 0회 (sweep → restore → API skip)`` () =
    // P-R3a 의 본질 path 회귀 — saveFromDb 박제분 → tryLookup hit → API call skip 흐름의 byte-level 검증.
    // 본 fact 는 `Vlm.buildCaptionGen` 의 closure 패턴을 mimic — 환경 변수 의존 없이 lib 만으로 검증.
    withTempDir (fun dir ->
        // 1차: ImageCache 박제 + cache sweep.
        use conn = openFresh dir
        let bytes = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |]  // PNG signature prefix
        let hash = ImageStore.computeSha256 bytes
        upsertCaption conn "ignored" "ignored" "ignored"  // 다른 row noise
        upsertCaption conn hash "기존 색인 시점 박제된 caption" "claude-sonnet-4-6"
        let _ = CaptionCache.saveFromDb dir conn
        // 2차: 신규 색인 시점 closure mimic — tryLookup hit → API call 진입 안 함.
        let mutable apiCallCount = 0
        let captionClosure (b: byte[]) (_: ImageFormat) : CaptionResult =
            let h = ImageStore.computeSha256 b
            match CaptionCache.tryLookup dir h with
            | Some (text, model) -> Captioned (text, model)
            | None ->
                apiCallCount <- apiCallCount + 1
                Captioned ("freshly generated", "fallback-model")
        let result = captionClosure bytes Png
        match result with
        | Captioned (text, model) ->
            Assert.Equal("기존 색인 시점 박제된 caption", text)
            Assert.Equal("claude-sonnet-4-6", model)
        | other -> Assert.Fail (sprintf "Captioned 기대, 실제 %A" other)
        Assert.Equal(0, apiCallCount))

[<Fact>]
let ``Integration — captionGen closure cache miss 시 API call 진입 (1회)`` () =
    // cache 에 없는 hash → tryLookup None → mock API 진입. (위 fact 의 대조군)
    withTempDir (fun dir ->
        use conn = openFresh dir
        // 다른 hash 만 cache 에 박제.
        upsertCaption conn "other-hash" "other text" "other-model"
        let _ = CaptionCache.saveFromDb dir conn
        let bytes = [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy |]  // JPEG signature prefix — 위 fact 와 다른 hash
        let mutable apiCallCount = 0
        let captionClosure (b: byte[]) (_: ImageFormat) : CaptionResult =
            let h = ImageStore.computeSha256 b
            match CaptionCache.tryLookup dir h with
            | Some (text, model) -> Captioned (text, model)
            | None ->
                apiCallCount <- apiCallCount + 1
                Captioned ("freshly generated", "fallback-model")
        let result = captionClosure bytes Jpeg
        match result with
        | Captioned (text, _) -> Assert.Equal("freshly generated", text)
        | other -> Assert.Fail (sprintf "Captioned 기대, 실제 %A" other)
        Assert.Equal(1, apiCallCount))
