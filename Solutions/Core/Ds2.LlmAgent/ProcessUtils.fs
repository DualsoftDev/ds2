namespace Ds2.LlmAgent

open System
open System.IO

/// Provider 공통 process 실행 유틸. CLI 가 PATH 에 누락되거나 .NET `Process.Start` 의 PATHEXT
/// 자동 검색 경로에서 빠지는 경우 명시 검색 + well-known fallback 으로 robustness.
///
/// **배경**: 사용자 환경에 따라 `Process.Start("codex")` 가 `codex.cmd` 를 못 찾는 경우 발견 (npm 글로벌
/// 디렉토리가 일부 process 의 PATH 에 누락). Claude 는 별도 인스톨러 위치 (e.g. `F:\HOME\.local\bin`) 가
/// PATH 에 정상 등록되어 영향 없었음. 본 helper 는 PATH + PATHEXT + `%APPDATA%\npm` 같은 well-known
/// 디렉토리까지 명시 검색하여 fully-qualified path 반환. ClaudeCli / CodexCli 두 provider 가 spawn 직전
/// 정규화에 사용한다.
[<RequireQualifiedAccess>]
module ProcessUtils =

    /// PATHEXT 환경변수 → 확장자 list. Windows 기본값으로 fallback.
    /// 비-Windows 에서도 호출 가능 (PATHEXT 미정의 시 기본값) — 단 보통 의미 없음.
    let pathExtensions () : string array =
        let raw = Environment.GetEnvironmentVariable("PATHEXT")
        if String.IsNullOrEmpty raw then
            [| ".COM"; ".EXE"; ".BAT"; ".CMD" |]
        else
            raw.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun e -> e.Trim())
            |> Array.filter (fun e -> e.Length > 0)

    /// PATH 환경변수 → 디렉토리 list. 빈 항목 / 공백 제거.
    let pathDirectories () : string array =
        let raw = Environment.GetEnvironmentVariable("PATH")
        if String.IsNullOrEmpty raw then Array.empty
        else
            raw.Split([| Path.PathSeparator |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun d -> d.Trim())
            |> Array.filter (fun d -> d.Length > 0)

    /// Windows 의 일반적 npm 글로벌 디렉토리 fallback. PATH 누락 환경 보완.
    /// 비-Windows 또는 환경변수 없으면 빈 array. nvm/Volta/pnpm 등 확장은 별도 spike 후 추가.
    let wellKnownFallbackDirs () : string array =
        if not (OperatingSystem.IsWindows()) then Array.empty
        else
            let appdata = Environment.GetEnvironmentVariable("APPDATA")
            if String.IsNullOrEmpty appdata then Array.empty
            else [| Path.Combine(appdata, "npm") |]

    /// `name` 이 어느 디렉토리에 있는지 PATH 우선 / well-known fallback 순으로 검색.
    /// `name` 에 path separator 가 들어있으면 (이미 fully-qualified 또는 상대경로) 존재 여부만 확인.
    /// 못 찾으면 None.
    ///
    /// **Windows extension 우선순위 주의** — npm 글로벌 디렉토리에는 `codex` (bash shim, 확장자 없음) +
    /// `codex.cmd` (Windows shim) + `codex.ps1` 가 공존. `Process.Start` 는 .cmd / .exe / .bat 만 실행 가능
    /// (bash shim 은 "not a valid application for this OS platform"). 따라서 Windows 에서는 PATHEXT 확장자만
    /// 시도하고 ext="" 는 시도하지 않는다 — Windows 단독 실행 가능 binary 가 확장자 없는 경우는 매우 드물고
    /// 있어도 .NET 으로 spawn 불가. 비-Windows 에서는 ext="" 우선 (Unix executable bit 가진 단일 파일 일반적).
    let findOnPath (name: string) : string option =
        if String.IsNullOrWhiteSpace name then None
        elif name.IndexOfAny([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]) >= 0 then
            // 의도적 단순화 — path separator 가 들어있으면 사용자 명시 경로로 간주, PATHEXT 자동 부착 X.
            // ExecutablePath 옵션으로 `C:\foo\bar\codex` (확장자 없이) 를 주는 케이스는 미지원.
            // 일반적 사용 패턴은 fully-qualified `.cmd` / `.exe` 까지 명시이므로 본 단순화로 충분.
            if File.Exists name then Some name else None
        else
            let isWindows = OperatingSystem.IsWindows()
            let exts =
                if Path.HasExtension name then [| "" |]
                elif isWindows then pathExtensions ()
                else Array.append [| "" |] (pathExtensions ())
            let trySearch (dirs: string array) =
                dirs
                |> Array.tryPick (fun dir ->
                    exts
                    |> Array.tryPick (fun ext ->
                        let candidate = Path.Combine(dir, name + ext)
                        if File.Exists candidate then Some candidate else None))
            match trySearch (pathDirectories ()) with
            | Some p -> Some p
            | None -> trySearch (wellKnownFallbackDirs ())

    /// 진단용 — `name` 의 fully-qualified path 를 찾으면 Ok, 못 찾으면 검색 디렉토리 요약 hint.
    /// EnsureCli 의 fail 메시지에 흘려 사용자가 PATH 누락을 즉시 진단 가능하도록.
    let resolveOrDiagnostic (name: string) : Result<string, string> =
        match findOnPath name with
        | Some p -> Ok p
        | None ->
            let pathDirs = pathDirectories ()
            let fallback = wellKnownFallbackDirs ()
            let truncate (xs: string array) =
                if xs.Length <= 12 then xs
                else Array.append (Array.truncate 12 xs) [| sprintf "... (+%d more)" (xs.Length - 12) |]
            let summary =
                Array.append (truncate pathDirs) fallback
                |> String.concat "; "
            Error (sprintf "PATH + well-known fallback (%d dirs) 의 어느 위치에서도 '%s' 를 찾지 못함. 검색한 디렉토리: %s" (pathDirs.Length + fallback.Length) name summary)
