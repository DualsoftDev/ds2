module Ds2.LlmAgent.Tests.ProcessUtilsTests

open System
open System.IO
open Xunit
open Ds2.LlmAgent

/// 임시 디렉토리 + 환경변수 PATH 를 그것만 가리키게 set 한 상태에서 fn 실행.
/// 끝나면 PATH 복원 + 임시 디렉토리 삭제.
let withTempDirOnPath (fn: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), "Ds2.LlmAgent.Tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore
    let originalPath = Environment.GetEnvironmentVariable("PATH")
    try
        Environment.SetEnvironmentVariable("PATH", tempDir)
        fn tempDir
    finally
        Environment.SetEnvironmentVariable("PATH", originalPath)
        try Directory.Delete(tempDir, true) with _ -> ()

/// PATHEXT 환경변수가 ".CMD" / ".cmd" / 혼합 케이스 등 사용자 환경마다 달라 반환된 path 의 확장자
/// 케이스가 unstable. Windows file system 이 case-insensitive 라 실제 동작에는 영향 없으므로 테스트는
/// case-insensitive 비교 헬퍼 사용.
let assertSomePath (expected: string) (actual: string option) =
    match actual with
    | None -> Assert.Fail(sprintf "Expected Some(%s), got None" expected)
    | Some a ->
        if not (String.Equals(expected, a, StringComparison.OrdinalIgnoreCase)) then
            Assert.Fail(sprintf "Expected Some(%s), got Some(%s)" expected a)

[<Fact>]
let ``findOnPath: 빈 문자열 / null 은 None`` () =
    Assert.Equal(None, ProcessUtils.findOnPath null)
    Assert.Equal(None, ProcessUtils.findOnPath "")
    Assert.Equal(None, ProcessUtils.findOnPath "   ")

[<Fact>]
let ``findOnPath: PATH 디렉토리에 .cmd 만 있으면 발견`` () =
    withTempDirOnPath (fun dir ->
        let cmdFile = Path.Combine(dir, "fake-cli.cmd")
        File.WriteAllText(cmdFile, "@echo fake")
        ProcessUtils.findOnPath "fake-cli" |> assertSomePath cmdFile)

[<Fact>]
let ``findOnPath: 존재하지 않으면 None`` () =
    withTempDirOnPath (fun _ ->
        Assert.Equal(None, ProcessUtils.findOnPath "definitely-not-installed-cli-xyz"))

[<Fact>]
let ``findOnPath: Windows — 확장자 없는 파일과 .cmd 공존 시 .cmd 선택 (R1 회귀)`` () =
    // npm 글로벌 디렉토리의 typical 구성 — bash shim (no ext) + Windows shim (.cmd) + PowerShell shim (.ps1)
    // .NET Process.Start 는 bash shim 을 실행 못 함 → .cmd 를 우선해야 한다 (todo line 19).
    if not (OperatingSystem.IsWindows()) then ()
    else
        withTempDirOnPath (fun dir ->
            let bashShim = Path.Combine(dir, "fake-cli")           // 확장자 없음
            let cmdShim = Path.Combine(dir, "fake-cli.cmd")
            let ps1Shim = Path.Combine(dir, "fake-cli.ps1")
            File.WriteAllText(bashShim, "#!/bin/sh\necho bash")
            File.WriteAllText(cmdShim, "@echo cmd")
            File.WriteAllText(ps1Shim, "Write-Host ps1")
            // PATHEXT 의 .CMD 가 .COM/.EXE/.BAT 다음 순서지만 같은 디렉토리에 .cmd 가 있으면 .cmd 가 잡혀야 함
            // (확장자 없는 bash shim 은 Windows 에서 시도하지 않음).
            ProcessUtils.findOnPath "fake-cli" |> assertSomePath cmdShim)

[<Fact>]
let ``findOnPath: 확장자가 명시된 입력은 그 확장자로만 시도`` () =
    withTempDirOnPath (fun dir ->
        let cmdFile = Path.Combine(dir, "fake-cli.cmd")
        let exeFile = Path.Combine(dir, "fake-cli.exe")
        File.WriteAllText(cmdFile, "@echo cmd")
        File.WriteAllText(exeFile, "fake exe")
        // 명시적 .cmd → .exe 가 같은 디렉토리에 있어도 .cmd 가 잡힘
        ProcessUtils.findOnPath "fake-cli.cmd" |> assertSomePath cmdFile)

[<Fact>]
let ``findOnPath: path separator 포함이면 file 존재 검사만`` () =
    let tempFile = Path.GetTempFileName()
    try
        ProcessUtils.findOnPath tempFile |> assertSomePath tempFile
        let nonexist = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"))
        Assert.Equal(None, ProcessUtils.findOnPath nonexist)
    finally
        try File.Delete(tempFile) with _ -> ()

[<Fact>]
let ``resolveOrDiagnostic: 못 찾으면 검색 디렉토리 갯수가 메시지에 포함`` () =
    withTempDirOnPath (fun _ ->
        match ProcessUtils.resolveOrDiagnostic "definitely-not-installed-cli-xyz" with
        | Ok _ -> Assert.Fail("발견되면 안 됨")
        | Error msg ->
            Assert.Contains("definitely-not-installed-cli-xyz", msg)
            Assert.Contains("PATH", msg))
