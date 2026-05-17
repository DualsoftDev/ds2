namespace Ds2.LightHouseService

open System
open System.IO
open System.IO.Compression
open System.Text.RegularExpressions

/// `POST /collections` 의 zip 검증/전개 (todo-lighthouse-kb-server.md §3.3 / §3.12).
///
/// 책임:
/// - sanitize: entry path `..` / 절대경로 / symlink / Collections\<id>\ 하위 verify / blobs/images regex
/// - zip bomb: 누적 압축 해제 byte / 압축 byte 비율 한도 (zipBombRatioLimit)
/// - IndexerVersion gate: 전개된 `.lighthouse-kb/index.db` 의 `Meta.indexer_version` 호환성 검증
/// - atomic move: Staging\ → Collections\<guid>-<sanitized-title>\

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type IndexerVersionGateResult =
    | Compatible
    | TooLow of clientVersion: string * hostMin: string
    | TooHigh of clientVersion: string * hostMax: string
    | Missing of reason: string

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type SanitizeError =
    | TraversalEscape of entry: string
    | AbsoluteEntry of entry: string
    | BlobRegexViolation of entry: string
    | ZipBombExceeded of decompressedBytes: int64 * compressedBytes: int64 * ratio: int

exception SanitizeException of error: SanitizeError


[<RequireQualifiedAccess>]
module ZipImport =

    /// blobs/images/ 하위 entry name pattern (§3.3 mn11).
    /// sha256 64-hex + 확장자 {png|jpg|jpeg|webp|tif|jp2}. **lowercase 만 허용** (review m1, §3.3 SSOT 정합).
    /// case-insensitive filesystem 의 deduplication 깨짐 회피.
    let private blobRegex =
        Regex(@"^[0-9a-f]{64}\.(png|jpg|jpeg|webp|tif|jp2)$", RegexOptions.Compiled)

    /// Windows 파일명 invalid char (`<>:"/\|?*` + control + format(Cf) + trailing dot/space).
    /// title sanitize 시 underscore 로 치환. 길이 한도 80 자 (Collections\<guid>-<sanitized> 의 디스크 path 한도 고려).
    ///
    /// review IC-2 (1/7 reviewer): unicode bidi override (U+202E 등) 는 `Char.IsControl` 가 false (Cf 분류).
    /// audit log spoofing / OS file picker 우회 차단을 위해 Format category 도 함께 거부 →
    /// `System.Globalization.CharUnicodeInfo.GetUnicodeCategory` 로 Cf 검출.
    let sanitizeTitle (raw: string) : string =
        if String.IsNullOrWhiteSpace raw then "untitled"
        else
            let invalidChars = Set.ofArray (Path.GetInvalidFileNameChars())
            let isFormat (ch: char) =
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory ch
                    = System.Globalization.UnicodeCategory.Format
            let sb = System.Text.StringBuilder()
            for ch in raw do
                if invalidChars.Contains ch || Char.IsControl ch || isFormat ch then
                    sb.Append '_' |> ignore
                else
                    sb.Append ch |> ignore
            let s = sb.ToString().Trim().TrimEnd('.').TrimEnd()
            let limited = if s.Length > 80 then s.Substring(0, 80) else s
            if String.IsNullOrEmpty limited then "untitled" else limited

    /// collection 디렉토리 이름 — `<guid>-<sanitized-title>` (D2).
    let collectionDirName (id: string) (rawTitle: string) : string =
        sprintf "%s-%s" id (sanitizeTitle rawTitle)

    /// entry name 의 path traversal / 절대경로 가드.
    /// zip entry 의 path separator 는 `/` 표준이지만 Windows 도구가 `\` 도 만들 수 있어 양쪽 정규화.
    let private validateEntryPath (rawEntry: string) =
        if String.IsNullOrEmpty rawEntry then ()
        else
            // DOS device path prefix (`\\?\C:\foo`, `\\.\C:\foo`) 명시 거부 (review C1).
            // resolveDestination 의 `Path.GetFullPath` + `StartsWith destRoot` 2차 가드가 잡지만
            // 1차 명시 거부가 진단 가독성 향상.
            if rawEntry.StartsWith(@"\\?\") || rawEntry.StartsWith(@"\\.\")
               || rawEntry.StartsWith("//?/") || rawEntry.StartsWith("//./") then
                raise (SanitizeException(SanitizeError.AbsoluteEntry rawEntry))
            // 절대경로 — Linux 의 `/foo` 또는 Windows 의 `C:\foo` 모두 거부.
            if rawEntry.StartsWith("/") || rawEntry.StartsWith("\\") then
                raise (SanitizeException(SanitizeError.AbsoluteEntry rawEntry))
            if rawEntry.Length >= 2 && Char.IsLetter rawEntry.[0] && rawEntry.[1] = ':' then
                raise (SanitizeException(SanitizeError.AbsoluteEntry rawEntry))

            // `..` traversal — segment 단위 비교. 정규화된 path 가 빈 segment 또는 `..` 포함 시 거부.
            let normalized = rawEntry.Replace('\\', '/')
            let segments = normalized.Split('/', StringSplitOptions.None)
            for seg in segments do
                if seg = ".." then
                    raise (SanitizeException(SanitizeError.TraversalEscape rawEntry))

    /// blobs/images/ 하위 entry name regex 강제.
    let private validateBlobEntry (relativePath: string) =
        let normalized = relativePath.Replace('\\', '/')
        let prefix = ".lighthouse-kb/blobs/images/"
        if normalized.StartsWith prefix then
            let fileName = normalized.Substring(prefix.Length)
            // 하위 디렉토리 없이 직접 sha256.ext 만 — segment 분리 시 1 segment
            if fileName.Contains '/' || not (blobRegex.IsMatch fileName) then
                raise (SanitizeException(SanitizeError.BlobRegexViolation relativePath))

    /// 단일 zip entry → 안전한 destination 절대 경로 변환.
    /// caller (`extractAll`) 가 `destRoot` 안에 있는지 verify.
    /// reject 케이스 = SanitizeException.
    let private resolveDestination (destRoot: string) (entryFullName: string) : string =
        validateEntryPath entryFullName
        validateBlobEntry entryFullName
        let normalized = entryFullName.Replace('\\', '/')
        let combined = Path.Combine(destRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
        let resolved = Path.GetFullPath combined
        let rootResolved =
            let r = Path.GetFullPath destRoot
            if r.EndsWith(string Path.DirectorySeparatorChar) then r else r + string Path.DirectorySeparatorChar
        if not (resolved.StartsWith(rootResolved, StringComparison.OrdinalIgnoreCase)) then
            raise (SanitizeException(SanitizeError.TraversalEscape entryFullName))
        resolved

    /// zip stream 을 destRoot 에 안전하게 extract.
    ///
    /// - sanitize 4종 (traversal / abs / symlink / blob regex)
    /// - zip bomb: 누적 decompressed byte > compressed total × ratio 면 abort
    /// - symlink: ZipArchiveEntry 의 ExternalAttributes (POSIX 의 S_IFLNK) 검사. .NET 의 ZipFile 이 자동 풀지는 않지만 명시 거부.
    ///
    /// 반환: decompressed total bytes (audit / 진단용)
    let extractAll
        (zipStream: Stream)
        (destRoot: string)
        (compressedTotalBytes: int64)
        (zipBombRatioLimit: int)
        : int64 =

        if not (Directory.Exists destRoot) then
            Directory.CreateDirectory destRoot |> ignore

        use archive = new ZipArchive(zipStream, ZipArchiveMode.Read)

        let bombLimit =
            if compressedTotalBytes <= 0L then Int64.MaxValue
            else
                let l = compressedTotalBytes * int64 zipBombRatioLimit
                if l < compressedTotalBytes then Int64.MaxValue else l

        let mutable totalDecompressed = 0L

        // POSIX symlink (S_IFLNK = 0xA000) 검사 — Unix permission 의 상위 nibble (review C2).
        // 디렉토리 / 파일 entry 둘 다 동일 검사 적용 — Linux `zip -y` 가 만든 symlink-as-dir 회피.
        let isSymlink (entry: ZipArchiveEntry) : bool =
            let attrUpper = (entry.ExternalAttributes >>> 16) &&& 0xF000
            attrUpper = 0xA000

        for entry in archive.Entries do
            if isSymlink entry then
                raise (SanitizeException(SanitizeError.TraversalEscape (entry.FullName + " (symlink)")))

            // 디렉토리 entry — 이름만 검증 후 skip (Directory.CreateDirectory 는 파일 entry 의 parent 에서 호출).
            if entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") then
                validateEntryPath entry.FullName
            else

                let dest = resolveDestination destRoot entry.FullName
                let parent = Path.GetDirectoryName dest
                if not (String.IsNullOrEmpty parent) && not (Directory.Exists parent) then
                    Directory.CreateDirectory parent |> ignore

                // open + counted copy — zip bomb 가드.
                use src = entry.Open()
                use dst = File.Create dest
                let buf = Array.zeroCreate<byte> 81920
                let mutable read = src.Read(buf, 0, buf.Length)
                while read > 0 do
                    totalDecompressed <- totalDecompressed + int64 read
                    if totalDecompressed > bombLimit then
                        raise (SanitizeException(
                            SanitizeError.ZipBombExceeded(totalDecompressed, compressedTotalBytes, zipBombRatioLimit)))
                    dst.Write(buf, 0, read)
                    read <- src.Read(buf, 0, buf.Length)

        totalDecompressed

    /// 전개된 collection 디렉토리의 `.lighthouse-kb/index.db` 의 `Meta.indexer_version` 호환성 검증 (§3.12).
    ///
    /// host 의 `indexerVersionRange.min`/`max` 와 SemVer 비교. 단순 문자열 비교 (semver lexical OK for x.y.z digits up to single-digit).
    /// `0-doc collection` (Documents 0건) 도 index.db 자체는 있어야 함 (Phase 1 의 SqliteStore.ensureSchema + stampVersion 결과).
    ///
    /// review IM-6 (2/7 reviewer): SqliteStore 직접 호출 우회 제거 → Ds2.LightHouse.KnowledgeBase.probeIndexerVersion delegation.
    /// service 의 Microsoft.Data.Sqlite PackageReference 제거 가능 (lib 의 PrivateAssets=all 정합).
    let probeIndexerVersion (collectionDir: string) : string option =
        Ds2.LightHouse.KnowledgeBase.probeIndexerVersion collectionDir

    /// SemVer 단순 비교 (digit-only 가정 — parent IndexerVersion.Current = "1.0.0").
    /// version 문자열 lexical compare 가 1.0.0 < 1.99.99 < 2.0.0 인데, 1.10.0 vs 1.9.0 같은 multi-digit
    /// 분기에서 잘못된 결과. host range 가 단일 자리 (1.x.y) 기준이라 Phase S2 는 보수적으로 단순 compare 채택.
    /// 향후 multi-digit 진입 시 SemVer parser 도입.
    let private compareVersion (a: string) (b: string) : int =
        let normalize (v: string) =
            v.Split('.')
            |> Array.map (fun s ->
                match Int32.TryParse s with
                | true, n -> n
                | _ -> 0)
        let aa = normalize a
        let bb = normalize b
        let len = max aa.Length bb.Length
        let mutable result = 0
        let mutable i = 0
        while result = 0 && i < len do
            let av = if i < aa.Length then aa.[i] else 0
            let bv = if i < bb.Length then bb.[i] else 0
            if av < bv then result <- -1
            elif av > bv then result <- 1
            i <- i + 1
        result

    /// IndexerVersion gate — host 의 `indexerVersionRange` 와 비교. §3.12 SSOT.
    let evaluateIndexerVersionGate
        (clientVersion: string option)
        (hostMin: string)
        (hostMax: string)
        : IndexerVersionGateResult =
        match clientVersion with
        | None -> IndexerVersionGateResult.Missing "index.db 의 Meta.indexer_version 키 미존재"
        | Some v when String.IsNullOrWhiteSpace v -> IndexerVersionGateResult.Missing "indexer_version 빈 값"
        | Some v ->
            if compareVersion v hostMin < 0 then IndexerVersionGateResult.TooLow(v, hostMin)
            elif compareVersion v hostMax > 0 then IndexerVersionGateResult.TooHigh(v, hostMax)
            else IndexerVersionGateResult.Compatible

    /// Staging 경로 → Collections\<guid>-<sanitized>\ atomic move (§3.10).
    /// 호출 전제: stagingPath 가 sanitize + IndexerVersion gate 통과한 상태.
    /// Collections root 안 이미 같은 이름 디렉토리 존재 시 InvalidOperationException (caller 가 swap path 사용).
    let moveStagingToCollection
        (storageRoot: string)
        (stagingPath: string)
        (collectionId: string)
        (rawTitle: string)
        : string =
        let collectionsDir = Storage.collectionsDir storageRoot
        if not (Directory.Exists collectionsDir) then
            Directory.CreateDirectory collectionsDir |> ignore
        let targetName = collectionDirName collectionId rawTitle
        let target = Path.Combine(collectionsDir, targetName)
        if Directory.Exists target then
            raise (InvalidOperationException(
                sprintf "collection 디렉토리 이미 존재 — %s (swap path 사용 권장)" target))
        Directory.Move(stagingPath, target)
        target

    /// payload swap — 기존 collection 디렉토리의 source/ + .lighthouse-kb/ 만 새 staging 내용으로 교체.
    /// meta.json 의 server 필드 (id / importedAt / importedBy / storageRelPath) 는 caller 가 stampServerFields 로
    /// 미리 stagingPath/meta.json 에 박아둠. 본 함수는 디렉토리 단위 swap 만.
    /// rollback: target 의 기존 source\ + .lighthouse-kb\ 를 `*.bak` 으로 rename → staging 내용 move → 성공 시 .bak 삭제.
    /// 실패 시 .bak 복귀.
    ///
    /// **review IC-1 강화 (3/7 reviewer 합의)**: 부분 실패 안전성 보강.
    /// - target 부분 잔재 cleanup 실패 시 silent swallow 금지 → `<target>.broken-<timestamp>` rename 으로 별도 격리
    /// - backup 복귀 시 target 이미 존재 분기 처리 — rollback path 의 모든 실패는 운영자 sentinel + fatal log
    /// - 원래 ex 보존 위해 `ExceptionDispatchInfo` 사용 (reraise 가 새 ex 로 덮이는 회귀 차단)
    let swapCollectionPayload
        (storageRoot: string)
        (stagingPath: string)
        (collectionId: string)
        (rawTitle: string)
        : string =
        let collectionsDir = Storage.collectionsDir storageRoot
        let targetName = collectionDirName collectionId rawTitle
        let target = Path.Combine(collectionsDir, targetName)
        if not (Directory.Exists target) then
            raise (InvalidOperationException(sprintf "swap 대상 collection 디렉토리 미존재 — %s" target))

        let backup = target + ".bak"
        if Directory.Exists backup then
            // 이전 swap 실패 잔재 — cleanup.
            Directory.Delete(backup, true)

        try
            Directory.Move(target, backup)
            Directory.Move(stagingPath, target)
            // 성공 — 백업 제거
            Directory.Delete(backup, true)
            target
        with ex ->
            // 원래 ex 보존 — reraise() 가 rollback 도중 새 ex 로 덮이는 회귀 차단 (review IC-1).
            let edi = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex
            Log.service.Error(sprintf "swapCollectionPayload 실패 — rollback 시작. ex=%s" ex.Message)
            Log.audit.Warn(sprintf "swap rollback: id=%s target=%s ex=%s" collectionId target ex.Message)

            // 1. target 부분 잔재 정리 — 실패 시 별도 격리 ('.broken-<ts>' rename) 로 backup 복귀 path 보호.
            if Directory.Exists target then
                try
                    Directory.Delete(target, true)
                with cleanupEx ->
                    let ts = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                    let broken = sprintf "%s.broken-%s" target ts
                    Log.service.Error(
                        sprintf "swap rollback: target cleanup 실패 — '%s'.broken-%s 로 격리 시도. cleanupEx=%s"
                            target ts cleanupEx.Message)
                    try
                        Directory.Move(target, broken)
                        Log.audit.Warn(
                            sprintf "swap rollback SENTINEL — id=%s broken=%s 운영자 수동 확인 필요" collectionId broken)
                    with renameEx ->
                        Log.service.Error(
                            sprintf "swap rollback: broken rename 도 실패 — target 잔재 + backup 보존. renameEx=%s"
                                renameEx.Message)
                        Log.audit.Warn(
                            sprintf "swap rollback FATAL — id=%s target=%s backup=%s 둘 다 잔재. 운영자 즉시 개입 필요"
                                collectionId target backup)
                        edi.Throw()   // 원래 ex 보존하고 즉시 throw — backup 복귀 시도 회피 (target 이 비지 않음)

            // 2. backup 복귀 — target 이 비어있어야 정상. 비어있지 않으면 위 fatal 분기에서 이미 throw 됨.
            if Directory.Exists backup then
                try
                    Directory.Move(backup, target)
                    Log.service.Info(sprintf "swap rollback: backup 복귀 완료 — %s" target)
                with restoreEx ->
                    Log.service.Error(
                        sprintf "swap rollback: backup 복귀 실패 — backup 잔재 유지. restoreEx=%s" restoreEx.Message)
                    Log.audit.Warn(
                        sprintf "swap rollback SENTINEL — id=%s backup=%s 별도 존재. registry storageRelPath dead link 위험. 운영자 수동 복구"
                            collectionId backup)
            // 원래 ex 그대로 throw (caller 의 catch 가 본 ex 만 봄, rollback 의 noise 없이)
            edi.Throw()
            target    // unreachable — edi.Throw() 이후 도달 안 함. F# 타입 정합용.

    /// collection 전체 purge (§3.9 D7). DELETE /collections/{id}.
    let purgeCollection (storageRoot: string) (collectionId: string) (rawTitle: string) =
        let collectionsDir = Storage.collectionsDir storageRoot
        let targetName = collectionDirName collectionId rawTitle
        let target = Path.Combine(collectionsDir, targetName)
        if Directory.Exists target then
            Directory.Delete(target, true)
