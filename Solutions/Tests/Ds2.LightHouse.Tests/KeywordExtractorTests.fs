module Ds2.LightHouse.Tests.KeywordExtractorTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-B (todo-lighthouse-index-summary.md §3.1)** — KeywordExtractor 단위 fact.
/// 알고리즘 = b1 stats (NLTK 영문 stopword + 길이≥2 + 알파/숫자/한글 + self-MATCH precision floor + top-15).

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-kw-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private writeFile (dir: string) (name: string) (body: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, body, Encoding.UTF8)
    path

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new PdfExtractor() :> IExtractor
    new OoxmlExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]

let private noProgress (_: IngestProgress) = ()

/// 폴더에 파일 색인 → KeywordExtractor.extract.
let private indexAndExtract (dir: string) : KeywordExtractionResult =
    let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
    if results.Length = 0 && not (File.Exists (SqliteStore.dbPath dir)) then
        failwith "test fixture — index.db 미생성"
    use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
    KeywordExtractor.extract conn


[<Fact>]
let ``빈 collection — 0 keyword 반환`` () =
    withTempDir (fun dir ->
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
        let r = KeywordExtractor.extract conn
        Assert.Empty(r.Keywords)
        Assert.Equal(None, r.Topic))


[<Fact>]
let ``영문 chunk — top-N 추출 + 빈도 desc 정렬`` () =
    withTempDir (fun dir ->
        // "conveyor" 가 5번 / "sensor" 가 3번 / "motor" 가 1번 등장 — 빈도 정렬 확인.
        // chunk 분할 위해 한 file 에 충분히 긴 본문 박제.
        let body =
            String.concat "\n" [
                "conveyor system specification version 3"
                "conveyor motor specification"
                "sensor calibration for conveyor"
                "sensor noise filter"
                "conveyor safety protocol"
                "sensor maintenance schedule"
                "conveyor inspection checklist"
            ]
        writeFile dir "spec.txt" body |> ignore
        let r = indexAndExtract dir
        Assert.NotEmpty(r.Keywords)
        // "conveyor" 가 가장 많이 등장 (5회) → 첫 위치
        Assert.Equal("conveyor", r.Keywords.[0].ToLowerInvariant())
        // "sensor" (3회) 가 "motor" (1회) 보다 앞
        let sensorIdx = Array.tryFindIndex (fun (k: string) -> k.ToLowerInvariant() = "sensor") r.Keywords
        let motorIdx = Array.tryFindIndex (fun (k: string) -> k.ToLowerInvariant() = "motor") r.Keywords
        match sensorIdx, motorIdx with
        | Some s, Some m -> Assert.True(s < m, sprintf "sensor (idx=%d) 가 motor (idx=%d) 보다 앞이어야 함" s m)
        | Some _, None -> ()  // motor 가 top-15 밖이면 OK
        | _ -> Assert.Fail("sensor 가 결과에 포함돼야 함"))


[<Fact>]
let ``stopword 제거 — NLTK 영문 stopword (the / and / is 등) 미포함`` () =
    withTempDir (fun dir ->
        // stopword 가 본문 token 대부분이지만 필터로 제거돼야 함.
        let body =
            String.concat "\n" [
                "the conveyor and the sensor are connected"
                "the motor is the heart of the conveyor"
                "and the safety is a must"
                "is the conveyor and motor are good"
                "the the the and and is is conveyor"
            ]
        writeFile dir "stopwords.txt" body |> ignore
        let r = indexAndExtract dir
        let lowered = r.Keywords |> Array.map (fun k -> k.ToLowerInvariant())
        Assert.DoesNotContain("the", lowered)
        Assert.DoesNotContain("and", lowered)
        Assert.DoesNotContain("is", lowered)
        Assert.DoesNotContain("are", lowered)
        Assert.DoesNotContain("a", lowered))


[<Fact>]
let ``길이 < MinTokenLength (=2) filter — 1-char token 제거`` () =
    withTempDir (fun dir ->
        let body = "a b c d e f g h conveyor sensor motor"
        writeFile dir "short.txt" body |> ignore
        let r = indexAndExtract dir
        for k in r.Keywords do
            Assert.True(k.Length >= 2, sprintf "1-char token (%s) 가 결과에 포함됨" k))


[<Fact>]
let ``한글 chunk — 한영 혼합 token 추출 (trigram tokenizer 정합)`` () =
    withTempDir (fun dir ->
        // 한국어 token 은 whitespace 단위 분리. trigram tokenizer 가 색인하므로 self-MATCH 통과.
        let body =
            String.concat "\n" [
                "컨베이어 시스템 사양서 version 3"
                "컨베이어 모터 사양 conveyor motor"
                "센서 캘리브레이션 sensor"
                "컨베이어 안전 protocol"
                "센서 점검 schedule"
                "컨베이어 검사 checklist"
            ]
        writeFile dir "korean.txt" body |> ignore
        let r = indexAndExtract dir
        Assert.NotEmpty(r.Keywords)
        // 컨베이어 / 센서 가 추출되어야 함
        let hasKr term = r.Keywords |> Array.exists (fun k -> k = term)
        Assert.True(hasKr "컨베이어", sprintf "컨베이어 누락 — %A" r.Keywords))


[<Fact>]
let ``self-MATCH precision floor — 추출된 모든 keyword 가 ChunksFts MATCH ≥ 1 hit`` () =
    withTempDir (fun dir ->
        let body =
            String.concat "\n" [
                "conveyor system specification version 3"
                "motor controller 컨베이어 동작 확인"
                "sensor 컨베이어 calibration"
                "보호 등급 IP65 conveyor"
            ]
        writeFile dir "match.txt" body |> ignore
        let r = indexAndExtract dir
        Assert.NotEmpty(r.Keywords)
        // 각 keyword 의 self-MATCH 직접 재검증
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
        for kw in r.Keywords do
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM ChunksFts WHERE ChunksFts MATCH @q"
            let p = cmd.CreateParameter()
            p.ParameterName <- "@q"
            p.Value <- sprintf "\"%s\"" kw
            cmd.Parameters.Add p |> ignore
            let r = cmd.ExecuteScalar() :?> int64
            Assert.True(r > 0L, sprintf "keyword '%s' 가 self-MATCH 0 hit (precision floor 위반)" kw))


[<Fact>]
let ``DefaultTopN (=15) cap — 결과 keyword 수 ≤ 15`` () =
    withTempDir (fun dir ->
        // 20+ unique 단어 박제 — top-N cap 동작 확인
        let words = [
            "conveyor"; "motor"; "sensor"; "actuator"; "valve"; "pump"; "controller"; "encoder"
            "relay"; "switch"; "button"; "lamp"; "indicator"; "alarm"; "buzzer"; "siren"
            "cable"; "connector"; "bracket"; "housing"; "screw"; "washer"
        ]
        let body =
            words
            |> List.mapi (fun i w -> String.replicate (i + 2) (w + " "))
            |> String.concat "\n"
        writeFile dir "many.txt" body |> ignore
        let r = indexAndExtract dir
        Assert.True(r.Keywords.Length <= KeywordExtractor.DefaultTopN,
            sprintf "결과 수 (%d) 가 DefaultTopN (%d) 초과" r.Keywords.Length KeywordExtractor.DefaultTopN))
