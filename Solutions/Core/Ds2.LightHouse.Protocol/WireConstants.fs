namespace Ds2.LightHouse.Protocol

/// **K4 Protocol SSOT (A2)** — wire-level 상수 SSOT. 본 namespace 하의 module 들이
/// server / Promaker / cli 양측에서 동일 박제 의무.

/// `meta.json` + zip 안 폴더 구조 (done-lighthouse-kb-server.md §3.3 zip layout SSOT).
///
/// 본 module 박제 이전 = Promaker `CollectionPackager.SourceFolderName / KbFolderName` + cli `Packager`
/// + server `SqliteStore.kbDir` 3곳 양분 (M22 outlier). K4 통합으로 단일 SSOT.
[<RequireQualifiedAccess>]
module ZipLayout =

    /// zip 안 사용자 원본 파일이 들어가는 최상위 폴더.
    [<Literal>]
    let SourceFolderName = "source"

    /// zip 안 KB 산출물 (index.db, meta.json, blobs/...) 폴더 — server / client 가 동일 경로 박제.
    /// 사용자 폴더의 in-place 색인 시에도 동일 sub-dir.
    [<Literal>]
    let KbFolderName = ".lighthouse-kb"


/// HTTP 요청/응답 header 이름 SSOT (done-lighthouse-kb-server.md §3.7 / §3.12 IndexerVersion gate).
[<RequireQualifiedAccess>]
module HeaderNames =

    /// mTLS subject CN 박제 시 강제 cross-check 대상 (s6-r70 C-3 박제).
    /// client = HTTP 요청 시 동봉 / server = `userIdentityOf` 가 추출.
    [<Literal>]
    let UserIdentity = "X-User-Identity"

    /// IndexerVersion gate (todo §3.12). server = config.indexerVersionRange [Min, Max] ∋ client 박제값 검증.
    [<Literal>]
    let IndexerVersion = "X-Indexer-Version"


/// HTTP Content-Type MIME 값 SSOT.
[<RequireQualifiedAccess>]
module MimeTypes =

    [<Literal>]
    let Json = "application/json"

    [<Literal>]
    let OctetStream = "application/octet-stream"

    /// SSE stream (text/event-stream).
    [<Literal>]
    let EventStream = "text/event-stream"
