namespace Ds2.LightHouse.Diagnostics

open System
open Newtonsoft.Json

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
type RejectedEntry = {
    /// 원본 파일 절대경로 또는 collection root 기준 상대경로.
    [<JsonProperty("file")>]
    File: string
    /// 시트명 / 페이지번호 / 슬라이드번호. 전체 파일 reject 면 None.
    [<JsonProperty("sheet", NullValueHandling = NullValueHandling.Ignore)>]
    Sheet: string option
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
        s

    let serialize<'T> (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

    let deserialize<'T> (json: string) : 'T =
        JsonConvert.DeserializeObject<'T>(json, settings)
