namespace Ds2.LightHouseService

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Net.Http.Headers

/// Phase S4 file serving — citation 원문 byte stream (todo-lighthouse-kb-server.md §3.9 / §4.2 Phase S4 / D6).
///
/// 흐름:
/// 1. PSK auth + X-User-Identity (AuthMiddleware 가 이미 검증)
/// 2. `GET /collections/{id}/files/{fileId}` 진입 — `{id}` = collection guid, `{fileId}` = documents.Id (Int64 문자열)
/// 3. Registry.tryFindById → CollectionEntry (404 if None)
/// 4. AttachmentResolver.collectionPath → collectionRoot
/// 5. KnowledgeBase.lookupDocument collectionRoot docId → (OriginalPath, FileHash, SizeBytes)
/// 6. source/ 디렉토리 안에서 basename + size match 파일 lookup (traversal 가드)
/// 7. `Results.File(physicalPath, contentType, fileName, enableRangeProcessing=true, entityTag=ETag(FileHash))`
///    → ASP.NET Core 가 Range / ETag / If-None-Match 자동 처리
///
/// 외부 fileId 형식 `<collection-guid>:<docId>` (AttachmentTools 의 exportFileId) 는 client (Promaker) 가
/// `:` 로 split 한 후 본 endpoint 의 path 두 segment 로 변환. 본 endpoint 는 path segment 받음.
[<RequireQualifiedAccess>]
module FileServing =

    // **C-15 (s6-r79)** — 3 helper 폐기 → `EndpointHelpers` SSOT 통과 alias.
    // FileServing 의 userIdentityOf 가 `Log.audit.Warn` invariant 박제 — EndpointHelpers SSOT 가 본 박제 일치.
    let private userIdentityOf = EndpointHelpers.userIdentityOf
    let private writeError = EndpointHelpers.writeError

    /// 파일 확장자 → MIME content-type (§4.2 Phase S4 DoD: PDF/DOCX/XLSX/PPTX/TXT/MD).
    /// 미인식 확장자 → `application/octet-stream` (browser 가 download 처리).
    ///
    /// review S4-m8 (P6 검토 결과): `String.IsNullOrEmpty` 가드는 *잉여 아님* — `Path.GetExtension(null)` 은
    /// .NET 8 에서 `null` 반환 (실제 throw 안 함). 이후 `null.ToLowerInvariant()` 가 NRE → 본 가드가 NRE 차단.
    /// 박제 유지 (잉여 정리 거부).
    let contentTypeOf (fileName: string) : string =
        let ext =
            if String.IsNullOrEmpty fileName then ""
            else (Path.GetExtension fileName).ToLowerInvariant()
        match ext with
        | ".pdf"  -> "application/pdf"
        | ".docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        | ".doc"  -> "application/msword"
        | ".xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        | ".xls"  -> "application/vnd.ms-excel"
        | ".pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        | ".ppt"  -> "application/vnd.ms-powerpoint"
        | ".txt"  -> "text/plain; charset=utf-8"
        | ".md"   -> "text/markdown; charset=utf-8"
        | ".csv"  -> "text/csv; charset=utf-8"
        | ".json" -> "application/json; charset=utf-8"
        | ".xml"  -> "application/xml; charset=utf-8"
        // review S4-m7: html/htm 은 *citation source* 라도 inline render 시 `<script>` XSS 위험 →
        // 강제 download (octet-stream). 본격 보기는 client (Promaker) 측 sanitize render 책임.
        | ".htm" | ".html" -> "application/octet-stream"
        | _ -> "application/octet-stream"

    /// source 디렉토리 안에서 basename + size match 파일 검색.
    ///
    /// documents.OriginalPath 는 client 색인 시점의 절대 경로 — server 측 source/ 안 파일과는 basename 만 일치.
    /// CollectionPackager (Phase S5) 가 source/ 안에 어떻게 배치할지 확정 전이므로 basename + size 매칭으로 lookup.
    /// 동명 파일이 여러 sub-directory 에 있어도 size 가 다르면 안전 — match 1건만 반환 (Array.tryFind).
    ///
    /// 보안: `Path.GetFullPath` 후 source root prefix 검증 — symlink / `..` 가드.
    /// (sanitize 시점에 이미 source/ 경계 안에 들어왔지만 paranoid double check.)
    /// test 단위 시험 위해 public (production caller 는 `getFile` 만 사용 의도).
    let findSourceFile (sourceRoot: string) (basename: string) (sizeBytes: int64) : string option =
        if not (Directory.Exists sourceRoot) then None
        else
            // review S4-M1: prefix-only 비교는 `source` ↔ `source-evil` 같은 형제 디렉토리 false-positive.
            // separator 명시 부착으로 경계 강제 — paranoid double check 정합 강화.
            let sep = string Path.DirectorySeparatorChar
            let sourceFullPath =
                let p = Path.GetFullPath sourceRoot
                if p.EndsWith sep then p else p + sep
            // recursive walk — basename match 후 size check.
            let candidates =
                Directory.EnumerateFiles(sourceRoot, basename, SearchOption.AllDirectories)
                |> Seq.toArray
            candidates
            |> Array.tryFind (fun p ->
                let full = Path.GetFullPath p
                // traversal 가드 — full path 가 sourceRoot 경계 안 (separator 포함) 에 있어야 함.
                if not (full.StartsWith(sourceFullPath, StringComparison.OrdinalIgnoreCase)) then false
                else
                    let info = FileInfo p
                    info.Length = sizeBytes)

    /// `GET /collections/{id}/files/{fileId}` handler.
    /// test 단위 시험 위해 public (production caller 는 `map` 만 사용 의도).
    ///
    /// **s6-r70 review C-1**: cfg 인자 추가 + `MultiTenantPolicy.evaluate` filter. T2/T3 모드에서 Hidden 분기는 404
    /// (acl reject ↔ 미존재 정보 leak 차단). ReadOnly 도 visible 셋 — file read 는 mutation 아니므로 통과.
    let getFile
        (cfg: ServiceConfig)
        (storageRoot: string)
        (id: string)
        (fileIdRaw: string)
        (ctx: HttpContext)
        : Task =
        task {
            let user = userIdentityOf ctx
            match Registry.tryFindById storageRoot id with
            | None ->
                Log.audit.Info(sprintf "file get: collection 미존재 — id=%s by=%s"
                    (Log.sanitizeForLog id) (Log.sanitizeForLog user))
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some entry when
                (match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user entry with
                 | MultiTenantPolicy.AccessDecision.Hidden -> true
                 | _ -> false) ->
                // **s6-r70 review C-1** — T2/T3 acl reject. cross-tenant download 차단 (Hidden = 404 정보 leak 0).
                Log.audit.Warn(sprintf "file get: acl reject (hidden) — id=%s by=%s mode=%s"
                    (Log.sanitizeForLog id) (Log.sanitizeForLog user) cfg.MultiTenant.Mode)
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some entry ->
                // fileId parse — Int64 (documents.Id).
                // review S4-m4 (P6): fileId SSOT = todo-lighthouse-kb-server.md §3.10 (server-side storage layout)
                // + §4.2 Phase S4 DoD + MA23 (AttachmentTools.exportFileId `<collection-guid>:<docId>` 합성).
                // client (Promaker) 는 외부 fileId 의 `:` 로 split 한 후 본 endpoint 의 path 두 segment 로 전달.
                match Int64.TryParse fileIdRaw with
                | false, _ ->
                    Log.audit.Info(sprintf "file get: fileId parse 실패 — id=%s fileId=%s by=%s"
                        (Log.sanitizeForLog id) (Log.sanitizeForLog fileIdRaw) (Log.sanitizeForLog user))
                    do! writeError ctx 400 (sprintf "fileId 형식 오류 — Int64 documents.Id 필요 (received='%s')" fileIdRaw)
                | true, docId ->
                    let collectionRoot = AttachmentResolver.collectionPath storageRoot entry.Id entry.DisplayName
                    match Ds2.LightHouse.KnowledgeBase.lookupDocument collectionRoot docId with
                    | None ->
                        Log.audit.Info(sprintf "file get: document 미존재 — id=%s docId=%d by=%s"
                            (Log.sanitizeForLog id) docId (Log.sanitizeForLog user))
                        do! writeError ctx 404 (sprintf "document 미존재 — id=%s docId=%d" id docId)
                    | Some (originalPath, fileHash, sizeBytes) ->
                        let basename = Path.GetFileName originalPath
                        let sourceRoot = Path.Combine(collectionRoot, "source")
                        match findSourceFile sourceRoot basename sizeBytes with
                        | None ->
                            Log.audit.Info(sprintf "file get: source/ 안 매칭 파일 없음 — id=%s docId=%d basename=%s size=%d by=%s"
                                (Log.sanitizeForLog id) docId (Log.sanitizeForLog basename) sizeBytes (Log.sanitizeForLog user))
                            do! writeError ctx 404
                                    (sprintf "source 파일 미존재 — basename=%s size=%d" basename sizeBytes)
                        | Some physicalPath ->
                            let contentType = contentTypeOf basename
                            let etag = EntityTagHeaderValue(Microsoft.Extensions.Primitives.StringSegment(sprintf "\"%s\"" fileHash))

                            // If-None-Match 검사 — match 시 304 + body 없음.
                            // ASP.NET Core 의 Results.File 이 자동 처리하지만 본 endpoint 는 IResult 미사용 (raw HttpContext).
                            // 명시 처리 — Range 호환성 차원에서 If-None-Match 만 단순 비교.
                            let ifNoneMatch =
                                match ctx.Request.Headers.TryGetValue HeaderNames.IfNoneMatch with
                                | true, v when v.Count >= 1 -> string v.[0]
                                | _ -> ""
                            // review S4-M2 (RFC 7232 §3.2): `If-None-Match: *` = "any current representation match" → 304.
                            // 일반 분기는 weak/strong tag 모두 hash substring match.
                            let etagMatch =
                                not (String.IsNullOrEmpty ifNoneMatch)
                                && (ifNoneMatch.Trim() = "*" || ifNoneMatch.Contains(fileHash))
                            if etagMatch then
                                ctx.Response.Headers.[HeaderNames.ETag] <- Microsoft.Extensions.Primitives.StringValues(etag.ToString())
                                ctx.Response.StatusCode <- 304
                                Log.audit.Info(sprintf "file get 304 (ETag match) — id=%s docId=%d by=%s"
                                    (Log.sanitizeForLog id) docId (Log.sanitizeForLog user))
                            else
                                Log.audit.Info(sprintf "file get — id=%s docId=%d basename=%s size=%d by=%s"
                                    (Log.sanitizeForLog id) docId (Log.sanitizeForLog basename) sizeBytes (Log.sanitizeForLog user))
                                // Results.File 사용 — Range / Content-Length / Last-Modified 자동 처리.
                                // physicalPath 는 sourceRoot 안인지 findSourceFile 이 이미 검증.
                                let lastModified = File.GetLastWriteTimeUtc physicalPath
                                let result =
                                    Results.File(
                                        path = physicalPath,
                                        contentType = contentType,
                                        fileDownloadName = basename,
                                        lastModified = Nullable(DateTimeOffset lastModified),
                                        entityTag = etag,
                                        enableRangeProcessing = true)
                                do! result.ExecuteAsync(ctx)
        } :> Task

    /// endpoint 등록. AuthMiddleware 뒤에 매핑.
    /// **s6-r70 review C-1**: cfg 인자 추가 (multi-tenant filter SSOT).
    let map (app: IEndpointRouteBuilder) (cfg: ServiceConfig) (storageRoot: string) =
        app.MapGet("/collections/{id}/files/{fileId}", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            let fileId = ctx.Request.RouteValues.["fileId"] |> string
            getFile cfg storageRoot id fileId ctx)) |> ignore
