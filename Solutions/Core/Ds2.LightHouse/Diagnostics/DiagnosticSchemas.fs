namespace Ds2.LightHouse.Diagnostics

open System
open Newtonsoft.Json
open Newtonsoft.Json.Serialization

/// **PR-I1 (todo-documents-based-gfm.md §6.1 N6 + §3.1)** — strategy 진단 JSON schema SSOT.
///
/// `.lighthouse-kb/{rejected, near-miss, stale}.json` 파일의 직렬화 단위.
/// `documents-based-gfm.md` §8.5.5 (footer cross-ref-hash) + §8.5.7 (near-miss 점수)
/// + §8.5.8 (stale 감지) 와 1:1.
///
/// PR-I1 시점 활성: `RejectedEntry` + `NearMissEntry` (N6 의 default 결정).
/// `StaleEntry` 는 schema 만 박제 (M11 backlog 정합), 활성은 후속 PR 진입 시.

/// strategy signature 매치는 되었으나 변환 실패 / 매크로 xlsx 등 비처리 케이스.
/// (`documents-based-gfm.md` §8.5.6 — 매크로 xlsm / 비밀번호 보호 / 외부 링크 끊김 등).
///
/// **Sheet field 박제 결정** (round 2 Major-3 fix):
/// F# `string option` 은 Newtonsoft.Json 기본 직렬화 시
/// `{"Case":"Some","Fields":["..."]}` envelope 으로 출력되어 외부 reader (.json
/// schema 소비자) 가 string 으로 파싱 시 fail. schema 정합 (single string 또는
/// key 누락) 을 위해 `string` (nullable, F# 비관용 trade-off 수용). `.NET string` 은
/// reference type 이라 F# `null` literal 대입이 컴파일러 통과 — record 차원 `[<AllowNullLiteral>]`
/// 부착 0 (F# record 에 사용 불가). `[<CLIMutable>]` 는 Newtonsoft deserialize 의 빈
/// 생성자 활용. 호출 site 는 `Sheet = null` 또는 `Sheet = "Sheet1"` 패턴.
[<CLIMutable>]
type RejectedEntry = {
    /// 원본 파일 절대경로 또는 collection root 기준 상대경로.
    [<JsonProperty("file")>]
    File: string
    /// 시트명 / 페이지번호 / 슬라이드번호. 전체 파일 reject 면 `null` (직렬화에서 key 누락).
    [<JsonProperty("sheet", NullValueHandling = NullValueHandling.Ignore)>]
    Sheet: string
    /// reject 사유 한국어 (사용자 진단용).
    [<JsonProperty("reason")>]
    Reason: string
    /// strategy 이름 (예: `IoListStrategy`). signature 단계에서 reject 면 `XlsxSignatureClassifier`.
    [<JsonProperty("strategy")>]
    Strategy: string
    /// 진단 박제 시각 ISO-8601.
    [<JsonProperty("rejectedAt")>]
    RejectedAt: DateTime
}

/// signature 점수가 매치 threshold 에 미달 (거의 매치) — 사용자 진단용.
/// (`documents-based-gfm.md` §8.5.7 threshold weighting 의 "거의 매치" 정책).
type NearMissEntry = {
    [<JsonProperty("file")>]
    File: string
    /// 후보 strategy 이름.
    [<JsonProperty("candidateStrategy")>]
    CandidateStrategy: string
    /// 합산 점수.
    [<JsonProperty("score")>]
    Score: int
    /// 매치에 필요한 최소 점수 (threshold).
    [<JsonProperty("threshold")>]
    Threshold: int
    /// 점수 미달 사유 (어느 조건이 매치 / 미매치 한지).
    [<JsonProperty("detail")>]
    Detail: string
    [<JsonProperty("detectedAt")>]
    DetectedAt: DateTime
}

/// 색인 후 strategy version 갱신 또는 source 파일 mtime/hash 갱신 감지.
/// (`documents-based-gfm.md` §8.5.8 stale 감지). PR-I1 시점 schema 만 박제 (M11 backlog).
type StaleEntry = {
    [<JsonProperty("file")>]
    File: string
    [<JsonProperty("strategy")>]
    Strategy: string
    /// 색인 당시 strategy semver (예: `1.0.0`).
    [<JsonProperty("indexedVersion")>]
    IndexedVersion: string
    /// 현재 lib 의 strategy semver.
    [<JsonProperty("currentVersion")>]
    CurrentVersion: string
    /// 색인 당시 source bytes sha256.
    [<JsonProperty("indexedHash")>]
    IndexedHash: string
    /// 현재 source bytes sha256.
    [<JsonProperty("currentHash")>]
    CurrentHash: string
    /// `major-version-mismatch` / `source-mtime-changed` / `source-hash-changed`.
    [<JsonProperty("reason")>]
    Reason: string
    [<JsonProperty("detectedAt")>]
    DetectedAt: DateTime
}

/// Newtonsoft.Json 직렬화/역직렬화 helper. 모든 schema 공통 indented + camelCase 박제.
[<RequireQualifiedAccess>]
module DiagnosticJson =

    let private settings =
        let s = JsonSerializerSettings()
        s.Formatting <- Formatting.Indented
        s.NullValueHandling <- NullValueHandling.Ignore
        // round 2 Major-3 fix — docstring "camelCase 박제" 정합. 종전은 `[<JsonProperty(...)>]`
        // 의 명시적 이름에만 의존했으나 (모든 field 에 attribute 가 있어 우연히 동작), 누락
        // field 가 추가될 경우 PascalCase 누출 위험. ContractResolver 로 default 보장.
        s.ContractResolver <- DefaultContractResolver(NamingStrategy = CamelCaseNamingStrategy())
        // F·m10 (Outlier/Minor 묶음 1) — DateTime UTC 강제. RejectedAt / DetectedAt 가 caller 에
        // 따라 Local / Unspecified 로 박제되어도 직렬화 시 UTC 정합 ISO 8601 Z suffix 박제.
        // DateTimeZoneHandling = Utc → write 시 Local → UTC 변환 + 'Z' suffix.
        // DateParseHandling = DateTimeOffset → read 시 zone 정보 보존, ToUniversalTime() 일치.
        s.DateTimeZoneHandling <- DateTimeZoneHandling.Utc
        s.DateFormatHandling <- DateFormatHandling.IsoDateFormat
        s.DateFormatString <- @"yyyy-MM-ddTHH:mm:ss.fffZ"
        s

    let serialize<'T> (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

    let deserialize<'T> (json: string) : 'T =
        JsonConvert.DeserializeObject<'T>(json, settings)
