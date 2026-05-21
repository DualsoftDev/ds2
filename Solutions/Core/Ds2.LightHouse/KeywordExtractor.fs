namespace Ds2.LightHouse

open System
open System.Text
open Microsoft.Data.Sqlite

/// **PR-B (todo-lighthouse-index-summary.md §3.1)** — KeywordExtractor 출력.
/// Topic = Phase 1 None (b1 stats 만으로 합성 불가) / Phase 2 b2 LLM-driven 도입 시 채움.
/// Keywords = top-N 빈도 keyword (self-MATCH precision floor 통과만).
type KeywordExtractionResult = {
    Topic: string option
    Keywords: string array
}

/// **PR-B (todo-lighthouse-index-summary.md §3.1)** — collection-level keyword profile 자동 추출.
///
/// 입력 = `.lighthouse-kb/index.db` 의 `Chunks.Text` 전체. 출력 = top-N 빈도 keyword (self-MATCH 검증 통과).
///
/// Phase 1 (b1 단독, 잠정 default):
/// - Topic = None (b1 stats 만으로 합성 불가, Phase 2 b2 LLM-driven 도입 시 채움)
/// - Keywords = unigram 빈도 top-15 (영문 NLTK stopword 제거 + 길이≥2 + 알파/숫자/한글 필터)
/// - **self-MATCH precision floor (필수)** — 추출된 각 keyword 가 자기 collection 의 `ChunksFts MATCH` ≥ 1 hit
///   되는지 assert. 0 hit keyword 는 결과에서 drop (FTS5 trigram tokenizer 정합 보장 — SqliteStore.fs §3.7).
///
/// 호출처 = CLI `runUpload` 의 `Packager.writeMeta` 직전 hook. server-side 자동 추출은 본 phase scope 외.
[<RequireQualifiedAccess>]
module KeywordExtractor =

    /// **PR-B 잠정 default (todo §4 미결정 1)** — top-N keyword/collection.
    /// Collection 수 N 개일 때 system prompt 토큰 = N × 15 × ~3 ≈ N × 45. 활성 10 → ~450 token (무난).
    /// Phase 2 진입 시 적응형 (`max(5, 30/N)`) 검토.
    [<Literal>]
    let DefaultTopN = 15

    /// **PR-B 잠정 default** — token 최소 길이 (char). 1-char token 은 noise 비율 高.
    [<Literal>]
    let MinTokenLength = 2

    /// 빈도 dict cap — 대형 KB (5000+ chunks) 시 메모리 안전 boundary.
    /// 50K entry × ~32 byte/string ≈ 1.6MB working set.
    [<Literal>]
    let private MaxDictSize = 50000

    /// **NLTK 영문 stopword** (https://github.com/nltk/nltk/blob/develop/nltk/corpus/reader/wordlist.py).
    /// 한국어는 trigram tokenizer 환경에서 단어 경계 추출 어려움 → 길이 / 문자 필터만 (noise 수용).
    /// 본 list = NLTK 3.8 english (~179 단어, lowercase).
    let private englishStopWords : Set<string> =
        Set.ofArray [|
            "i"; "me"; "my"; "myself"; "we"; "our"; "ours"; "ourselves"; "you"; "your"; "yours"
            "yourself"; "yourselves"; "he"; "him"; "his"; "himself"; "she"; "her"; "hers"
            "herself"; "it"; "its"; "itself"; "they"; "them"; "their"; "theirs"; "themselves"
            "what"; "which"; "who"; "whom"; "this"; "that"; "these"; "those"; "am"; "is"; "are"
            "was"; "were"; "be"; "been"; "being"; "have"; "has"; "had"; "having"; "do"; "does"
            "did"; "doing"; "a"; "an"; "the"; "and"; "but"; "if"; "or"; "because"; "as"; "until"
            "while"; "of"; "at"; "by"; "for"; "with"; "about"; "against"; "between"; "into"
            "through"; "during"; "before"; "after"; "above"; "below"; "to"; "from"; "up"; "down"
            "in"; "out"; "on"; "off"; "over"; "under"; "again"; "further"; "then"; "once"
            "here"; "there"; "when"; "where"; "why"; "how"; "all"; "any"; "both"; "each"; "few"
            "more"; "most"; "other"; "some"; "such"; "no"; "nor"; "not"; "only"; "own"; "same"
            "so"; "than"; "too"; "very"; "s"; "t"; "can"; "will"; "just"; "don"; "should"; "now"
            "d"; "ll"; "m"; "o"; "re"; "ve"; "y"; "ain"; "aren"; "couldn"; "didn"; "doesn"
            "hadn"; "hasn"; "haven"; "isn"; "ma"; "mightn"; "mustn"; "needn"; "shan"; "shouldn"
            "wasn"; "weren"; "won"; "wouldn"
        |]

    /// token 분리 — whitespace + 구두점 (UnicodeCategory 의 알파/숫자/한글 제외 모든 char 가 separator).
    /// Char.IsLetterOrDigit 가 한글 (Hangul Syllables) 도 letter 로 인식 — 한영 혼합 정합.
    let private tokenize (text: string) : string seq = seq {
        if not (String.IsNullOrEmpty text) then
            let sb = StringBuilder()
            for ch in text do
                if Char.IsLetterOrDigit ch then
                    sb.Append ch |> ignore
                elif sb.Length > 0 then
                    yield sb.ToString()
                    sb.Clear() |> ignore
            if sb.Length > 0 then yield sb.ToString()
    }

    /// 1 token 필터 — 길이 + stop-word + 빈 값.
    let private isValidToken (tok: string) : bool =
        if String.IsNullOrEmpty tok then false
        elif tok.Length < MinTokenLength then false
        elif englishStopWords.Contains(tok.ToLowerInvariant()) then false
        else true

    /// chunk text 전체 streaming → 빈도 dict 누적. cap (MaxDictSize) 도달 시 추가 token 무시
    /// (이후 token 이 top-N 진입 가능성 낮음 — 빈도 누적 cost 가 메모리 cost 보다 큼).
    let private accumulateFrequency (conn: SqliteConnection) : System.Collections.Generic.Dictionary<string, int> =
        let dict = System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Text FROM Chunks"
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let text = reader.GetString(0)
            for tok in tokenize text do
                if isValidToken tok then
                    match dict.TryGetValue tok with
                    | true, n -> dict.[tok] <- n + 1
                    | false, _ ->
                        if dict.Count < MaxDictSize then
                            dict.[tok] <- 1
        dict

    /// **self-MATCH precision floor (todo §3.1 SSOT)** — 추출된 keyword 가 자기 collection 의 `ChunksFts MATCH`
    /// ≥ 1 hit 되는지 검증. FTS5 trigram tokenizer 정합 — 한글의 경우 trigram split 결과로 phrase match 됨.
    /// phrase quoting (`"<kw>"`) 으로 wildcard / FTS 특수 문자 회피.
    let private selfMatchValidate (conn: SqliteConnection) (candidates: string array) : string array =
        if candidates.Length = 0 then [||]
        else
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM ChunksFts WHERE ChunksFts MATCH @q LIMIT 1"
            let p = cmd.CreateParameter()
            p.ParameterName <- "@q"
            cmd.Parameters.Add p |> ignore
            candidates
            |> Array.filter (fun kw ->
                p.Value <- sprintf "\"%s\"" kw  // phrase quote — FTS 특수 문자 safe
                let r = cmd.ExecuteScalar()
                match r with
                | :? int64 as n -> n > 0L
                | _ -> false)

    /// **PR-B 잠정 default (todo §4 미결정 1)** — Phase 1 단독 b1 stats.
    /// (b2 LLM-driven topic 합성은 Phase 2 진입 시점에 본 함수 확장 또는 별 함수 신설.)
    let extract (conn: SqliteConnection) : KeywordExtractionResult =
        let dict = accumulateFrequency conn
        let topCandidates =
            // 빈도 desc + 동률은 길이 desc (긴 토큰이 통상 의미 高)
            dict
            |> Seq.sortByDescending (fun kvp -> (kvp.Value, kvp.Key.Length))
            |> Seq.map (fun kvp -> kvp.Key)
            |> Seq.truncate (DefaultTopN * 2)  // self-MATCH drop 대비 over-fetch
            |> Seq.toArray
        let validated = selfMatchValidate conn topCandidates
        {
            Topic = None  // Phase 1 = None (b2 도입 시 채움)
            Keywords = validated |> Array.truncate DefaultTopN
        }
