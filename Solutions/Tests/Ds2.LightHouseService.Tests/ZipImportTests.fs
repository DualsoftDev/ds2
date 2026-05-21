module Ds2.LightHouseService.Tests.ZipImportTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Ds2.LightHouseService

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-zip-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// 메모리 zip 빌더 — entry 이름 + content bytes 쌍.
let private mkZip (entries: (string * byte[]) list) : MemoryStream =
    let ms = new MemoryStream()
    (
        use archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen = true)
        for (name, bytes) in entries do
            let entry = archive.CreateEntry(name, CompressionLevel.NoCompression)
            use s = entry.Open()
            s.Write(bytes, 0, bytes.Length)
    )
    ms.Position <- 0L
    ms

[<Fact>]
let ``sanitizeTitle — invalid Windows char 치환`` () =
    Assert.Equal("a_b_c_d", ZipImport.sanitizeTitle "a<b>c|d")
    Assert.Equal("foo_bar", ZipImport.sanitizeTitle "foo:bar")
    Assert.Equal("normal", ZipImport.sanitizeTitle "normal")
    Assert.Equal("untitled", ZipImport.sanitizeTitle "")
    Assert.Equal("untitled", ZipImport.sanitizeTitle "   ")

[<Fact>]
let ``sanitizeTitle — 80 char 길이 한도`` () =
    let raw = String.replicate 100 "x"
    Assert.Equal(80, (ZipImport.sanitizeTitle raw).Length)

[<Fact>]
let ``sanitizeTitle — idempotency 검증 (review IC-2 SSOT 보호)`` () =
    // 다양한 입력에 대해 sanitize(sanitize(x)) = sanitize(x) — directory path drift 방지의 핵심 invariant.
    let inputs = [
        "normal-title"
        "a<b>c|d"
        "foo:bar/baz\\qux"
        "tailing space  "
        "tailing dot."
        "tailing dot space . "
        "  leading space"
        String.replicate 100 "x"
        String.replicate 200 "한국어"   // 80 char 한도 + 멀티바이트
        "‮hidden-bidi"
        "control\t\n\r"
        ""
        "   "
    ]
    for input in inputs do
        let once = ZipImport.sanitizeTitle input
        let twice = ZipImport.sanitizeTitle once
        Assert.Equal(once, twice)

[<Fact>]
let ``sanitizeTitle — unicode bidi / control char → underscore (audit log spoofing 차단, IC-2)`` () =
    // RTL override (U+202E) 는 Char.IsControl 에서 true (Cc category) → '_' 치환 보장
    let result = ZipImport.sanitizeTitle "alice‮.exe"
    Assert.DoesNotContain('‮', result)
    // CR/LF 도 IsControl → '_'
    let crlf = ZipImport.sanitizeTitle "line1\r\nline2"
    Assert.DoesNotContain('\r', crlf)
    Assert.DoesNotContain('\n', crlf)

[<Fact>]
let ``collectionDirName — guid + sanitized title`` () =
    let id = "550e8400-e29b-41d4-a716-446655440000"
    Assert.Equal(id + "-line-A", ZipImport.collectionDirName id "line-A")
    Assert.Equal(id + "-untitled", ZipImport.collectionDirName id "")

[<Fact>]
let ``extractAll — 정상 zip 추출`` () = withTempDir (fun root ->
    use zip = mkZip [
        "a.txt", Encoding.UTF8.GetBytes "content-A"
        "sub/b.txt", Encoding.UTF8.GetBytes "content-B"
    ]
    let decompressed = ZipImport.extractAll zip root zip.Length 50
    Assert.True(decompressed > 0L)
    Assert.True(File.Exists (Path.Combine(root, "a.txt")))
    Assert.True(File.Exists (Path.Combine(root, "sub", "b.txt")))
    Assert.Equal("content-A", File.ReadAllText (Path.Combine(root, "a.txt"))))

[<Fact>]
let ``sanitize — traversal '..' 거부`` () = withTempDir (fun root ->
    use zip = mkZip [ "../escape.txt", [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.TraversalEscape _ -> ()
    | other -> Assert.Fail(sprintf "TraversalEscape 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — 절대 경로 거부 (Linux 형)`` () = withTempDir (fun root ->
    use zip = mkZip [ "/etc/passwd", [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.AbsoluteEntry _ -> ()
    | other -> Assert.Fail(sprintf "AbsoluteEntry 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — 절대 경로 거부 (Windows 형 C:\\)`` () = withTempDir (fun root ->
    use zip = mkZip [ "C:\\Windows\\System32\\evil.dll", [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.AbsoluteEntry _ -> ()
    | other -> Assert.Fail(sprintf "AbsoluteEntry 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — DOS device path `\\?\C:\foo` 거부 (review C1)`` () = withTempDir (fun root ->
    use zip = mkZip [ @"\\?\C:\Windows\evil.dll", [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.AbsoluteEntry _ -> ()
    | other -> Assert.Fail(sprintf "AbsoluteEntry 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — POSIX symlink entry (S_IFLNK) 거부 (review C2)`` () = withTempDir (fun root ->
    // ZipArchive 가 ExternalAttributes 를 set 할 수 있는 가장 단순한 방법 = 직접 stream 생성.
    let ms = new MemoryStream()
    (
        use archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen = true)
        let entry = archive.CreateEntry("evil-link", CompressionLevel.NoCompression)
        // S_IFLNK = 0xA000, shift 16 → 0xA0000000.
        entry.ExternalAttributes <- 0xA0000000  // POSIX symlink
        use s = entry.Open()
        let target = System.Text.Encoding.UTF8.GetBytes "/etc/passwd"
        s.Write(target, 0, target.Length)
    )
    ms.Position <- 0L
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll ms root ms.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.TraversalEscape msg -> Assert.Contains("symlink", msg)
    | other -> Assert.Fail(sprintf "TraversalEscape(symlink) 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — blobs/images/ 대문자 sha256 거부 (review m1, §3.3 lowercase SSOT)`` () = withTempDir (fun root ->
    let uppercase = String.replicate 64 "A"
    use zip = mkZip [ sprintf ".lighthouse-kb/blobs/images/%s.png" uppercase, [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.BlobRegexViolation _ -> ()
    | other -> Assert.Fail(sprintf "BlobRegexViolation 기대, %A" other))

[<Fact>]
let ``sanitize — blobs/images/ regex 위반 거부`` () = withTempDir (fun root ->
    // 잘못된 형식 (확장자만 잘못, sha256 형식 위반)
    use zip = mkZip [ ".lighthouse-kb/blobs/images/not-sha256.png", [| 1uy |] ]
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll zip root zip.Length 50 |> ignore)
    match ex.error with
    | SanitizeError.BlobRegexViolation _ -> ()
    | other -> Assert.Fail(sprintf "BlobRegexViolation 기대, 실제 = %A" other))

[<Fact>]
let ``sanitize — blobs/images/ 정상 sha256.png 통과`` () = withTempDir (fun root ->
    let valid = String.replicate 64 "a"  // 64 hex
    use zip = mkZip [ sprintf ".lighthouse-kb/blobs/images/%s.png" valid, [| 1uy; 2uy; 3uy |] ]
    let _ = ZipImport.extractAll zip root zip.Length 50
    Assert.True(File.Exists (Path.Combine(root, ".lighthouse-kb", "blobs", "images", sprintf "%s.png" valid))))

[<Fact>]
let ``zip bomb — ratio 한도 초과 거부`` () = withTempDir (fun root ->
    // 매우 압축 잘 되는 1MB zero byte → CompressionLevel.Optimal 로 작게.
    let oneMB = Array.zeroCreate<byte> (1024 * 1024)
    let ms = new MemoryStream()
    (
        use archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen = true)
        let e = archive.CreateEntry("big.bin", CompressionLevel.Optimal)
        use s = e.Open()
        s.Write(oneMB, 0, oneMB.Length)
    )
    ms.Position <- 0L
    let compressedLen = ms.Length
    // ratio = 50 이면 compressed * 50 = 한도. 1MB zero byte 의 deflate ≈ 1KB → 한도 50KB 초과.
    let ex = Assert.Throws<SanitizeException>(fun () ->
        ZipImport.extractAll ms root compressedLen 50 |> ignore)
    match ex.error with
    | SanitizeError.ZipBombExceeded _ -> ()
    | other -> Assert.Fail(sprintf "ZipBombExceeded 기대, 실제 = %A" other))

[<Fact>]
let ``evaluateIndexerVersionGate — Compatible / TooLow / TooHigh / Missing`` () =
    let gate = ZipImport.evaluateIndexerVersionGate
    // None 입력 → Missing
    match gate None "1.0.0" "1.99.99" with
    | IndexerVersionGateResult.Missing _ -> ()
    | other -> Assert.Fail(sprintf "Missing 기대, %A" other)
    // 빈 값 → Missing
    match gate (Some "  ") "1.0.0" "1.99.99" with
    | IndexerVersionGateResult.Missing _ -> ()
    | other -> Assert.Fail(sprintf "Missing 기대 (빈 값), %A" other)
    // Compatible
    Assert.Equal(IndexerVersionGateResult.Compatible, gate (Some "1.0.0") "1.0.0" "1.99.99")
    Assert.Equal(IndexerVersionGateResult.Compatible, gate (Some "1.5.0") "1.0.0" "1.99.99")
    Assert.Equal(IndexerVersionGateResult.Compatible, gate (Some "1.99.99") "1.0.0" "1.99.99")
    // TooLow
    match gate (Some "0.9.99") "1.0.0" "1.99.99" with
    | IndexerVersionGateResult.TooLow ("0.9.99", "1.0.0") -> ()
    | other -> Assert.Fail(sprintf "TooLow 기대, %A" other)
    // TooHigh
    match gate (Some "2.0.0") "1.0.0" "1.99.99" with
    | IndexerVersionGateResult.TooHigh ("2.0.0", "1.99.99") -> ()
    | other -> Assert.Fail(sprintf "TooHigh 기대, %A" other)

[<Fact>]
let ``moveStagingToCollection — Staging → Collections atomic move`` () = withTempDir (fun storageRoot ->
    Storage.initialize storageRoot |> ignore
    let stagingDir = Storage.stagingDir storageRoot
    let staging = Path.Combine(stagingDir, "tmp-source")
    Directory.CreateDirectory staging |> ignore
    File.WriteAllText(Path.Combine(staging, "a.txt"), "content")
    let id = "550e8400-e29b-41d4-a716-446655440000"
    let target = ZipImport.moveStagingToCollection storageRoot staging id "Test Title"
    Assert.True(Directory.Exists target)
    Assert.False(Directory.Exists staging)
    Assert.True(File.Exists (Path.Combine(target, "a.txt"))))

[<Fact>]
let ``swapCollectionPayload — 정상 swap`` () = withTempDir (fun storageRoot ->
    Storage.initialize storageRoot |> ignore
    let collectionsDir = Storage.collectionsDir storageRoot
    let id = "550e8400-e29b-41d4-a716-446655440000"
    let oldTarget = Path.Combine(collectionsDir, ZipImport.collectionDirName id "title")
    Directory.CreateDirectory oldTarget |> ignore
    File.WriteAllText(Path.Combine(oldTarget, "old.txt"), "old-content")
    // staging 에 신규 내용
    let staging = Path.Combine(Storage.stagingDir storageRoot, "new-staging")
    Directory.CreateDirectory staging |> ignore
    File.WriteAllText(Path.Combine(staging, "new.txt"), "new-content")

    let target = ZipImport.swapCollectionPayload storageRoot staging id "title"
    Assert.True(File.Exists (Path.Combine(target, "new.txt")))
    Assert.False(File.Exists (Path.Combine(target, "old.txt")))
    // K1 fix: backup suffix 가 per-호출 unique guid 라 정상 swap 후 `.bak*` 잔재 0.
    let leftoverBak =
        Directory.EnumerateDirectories(collectionsDir, "*.bak*", SearchOption.TopDirectoryOnly)
        |> Seq.toArray
    Assert.Empty leftoverBak
    Assert.False(Directory.Exists staging))

[<Fact>]
let ``makeBackupPath — per-호출 unique suffix `.bak-<hex12>` (K1 회귀 차단)`` () =
    // **K1 회귀 차단 Fact (s6-r10)** — fixed `.bak` 회귀 시 동일 target 의 두 swap 이 같은 backup path 산출 →
    // 첫 swap 의 rollback 도중 두번째 swap 이 backup 을 삭제 → target 영구 손실 risk.
    // 본 Fact = `makeBackupPath` 가 동일 target 입력에 대해 모든 호출이 서로 다른 unique suffix 산출 + suffix 형식 강제 검증.
    //
    // race 시뮬레이션 (Task.WhenAll) 은 swap 자체의 catch 분기 robustness (별 부담) 와 얽혀 flaky.
    // unit-level invariant 검증 만으로 K1 본질 (suffix 충돌 0) 박제 충분 — race timing 무관 deterministic.
    let target = @"C:\some\Collections\550e8400-x-title"
    let pattern = System.Text.RegularExpressions.Regex(@"^.+\.bak-[0-9a-f]{12}$")
    let n = 200
    let paths = [| for _ in 1..n -> ZipImport.makeBackupPath target |]

    // 모든 결과가 unique
    let uniqueCount = paths |> Set.ofArray |> Set.count
    Assert.Equal(n, uniqueCount)

    // suffix 형식 = .bak-<12 hex 소문자>
    for p in paths do
        Assert.True(
            pattern.IsMatch p,
            sprintf "backup path suffix 형식 위반 (.bak-<hex12> 미준수) — %s" p)
        // target 자체 path 가 prefix
        Assert.StartsWith(target + ".bak-", p)

[<Fact>]
let ``purgeCollection — Collections\<id-title> 전체 삭제`` () = withTempDir (fun storageRoot ->
    Storage.initialize storageRoot |> ignore
    let collectionsDir = Storage.collectionsDir storageRoot
    let id = "g1"
    let dir = Path.Combine(collectionsDir, ZipImport.collectionDirName id "x")
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.txt"), "x")
    ZipImport.purgeCollection storageRoot id "x"
    Assert.False(Directory.Exists dir))

[<Fact>]
let ``probeIndexerVersion — index.db 미존재 시 None`` () = withTempDir (fun root ->
    Assert.Equal(None, ZipImport.probeIndexerVersion root))
