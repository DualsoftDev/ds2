namespace Ds2.LlmAgent

open System
open System.Diagnostics
open System.Text.RegularExpressions

/// Claude CLI 의 SemVer 검증.
///
/// Phase 1a 시점 minimum: 2.1.0 (실증 시점 2.1.129 기준 보수적 minimum).
/// 새 인자 / 패킷 형식이 추가되어 minimum 을 올려야 할 때 본 모듈만 수정.
[<RequireQualifiedAccess>]
module ClaudeCliVersion =

    [<Literal>]
    let MinimumMajor = 2

    [<Literal>]
    let MinimumMinor = 1

    [<Literal>]
    let MinimumPatch = 0

    let private versionRegex = Regex(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled)

    /// "2.1.129 (Claude Code)" 같은 출력에서 (major, minor, patch) 추출.
    let parse (raw: string) : (int * int * int) option =
        if String.IsNullOrWhiteSpace(raw) then None
        else
            let m = versionRegex.Match(raw)
            if not m.Success then None
            else
                Some(int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value)

    let private compare ((a1, a2, a3): int * int * int) ((b1, b2, b3): int * int * int) : int =
        if a1 <> b1 then compare a1 b1
        elif a2 <> b2 then compare a2 b2
        else compare a3 b3

    /// `claude --version` 를 실행해서 raw 문자열을 가져온다. CLI 가 PATH 에 없으면 None.
    let tryGetInstalledVersion () : string option =
        try
            let psi = ProcessStartInfo("claude", "--version")
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use p = Process.Start(psi)
            p.WaitForExit(5000) |> ignore
            if p.HasExited && p.ExitCode = 0 then
                let output = p.StandardOutput.ReadToEnd()
                Some(output.Trim())
            else
                None
        with ex ->
            Log.provider.Warn($"Failed to invoke `claude --version`: {ex.Message}")
            None

    /// `ensureMinimum` 결과 (C# interop 친화 record).
    type Result = {
        IsValid: bool
        Message: string
        VersionString: string
    }

    /// minimum 보다 낮으면 IsValid=false, 충분하면 IsValid=true.
    let ensureMinimum () : Result =
        match tryGetInstalledVersion () with
        | None ->
            { IsValid = false
              Message = "Claude CLI 가 PATH 에 없거나 `claude --version` 실행 실패. 설치 후 재시도해주세요."
              VersionString = "" }
        | Some raw ->
            match parse raw with
            | None ->
                { IsValid = false
                  Message = $"Claude CLI 버전 문자열 파싱 실패: '{raw}'"
                  VersionString = raw }
            | Some current ->
                let minimum = (MinimumMajor, MinimumMinor, MinimumPatch)
                let (a, b, c) = current
                let versionStr = $"{a}.{b}.{c}"
                if compare current minimum < 0 then
                    { IsValid = false
                      Message = $"Claude CLI {versionStr} 는 minimum {MinimumMajor}.{MinimumMinor}.{MinimumPatch} 미만입니다. 업데이트 후 재시도해주세요."
                      VersionString = versionStr }
                else
                    { IsValid = true
                      Message = $"Claude CLI {versionStr} 검출"
                      VersionString = versionStr }
