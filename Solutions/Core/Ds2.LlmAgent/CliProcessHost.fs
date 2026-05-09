namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// CLI provider 의 process spawn / stdout-stderr loop / cancel hook / channel backpressure 추상화.
///
/// **추출 배경** (Phase 2 후속): `ClaudeCliProvider` 와 `CodexCliProvider` 의 `Send` 본문이 95% 동일
/// (process spawn → stdout/stderr ReadLineAsync loop → stream-json parse → BoundedChannel write →
/// WhenAll → exit-code 분기 → ContinueWith 의 unobserved exception 안전망). 차이는 단 5 축:
/// `executable`, `args`, `parser`, `env override` (CODEX_HOME), redact 인덱스 규칙, exit-code 메시지 포맷.
/// 본 module 의 `runStream` 는 이 차이만 spec 으로 받아 동일 골격을 1회 구현 — 후속 provider 추가 시
/// 200+ 라인 복제 → 30 라인 옵션 변환으로 축소.
[<RequireQualifiedAccess>]
module CliProcessHost =

    /// M11 — stderr last line 의 secret 토큰 redact + 길이 cap. exit-error 메시지로 사용자에게
    /// 노출되는 영문 raw 가 인증 토큰 / API key 같은 secret 을 포함하면 화면 / IM 캡처에 leak.
    /// 패턴 = `(키워드)(separator)(value)` 3 group — 원본 separator (`:` / `=` / 공백 / `Bearer ` 의 단일 공백 등) 보존.
    let private secretRedactPattern =
        Regex(@"(?i)\b(api[_-]?key|token|bearer|secret|password)([\s=:]+)[^\s'""]+",
              RegexOptions.Compiled)

    let internal redactStderr (line: string) : string =
        if String.IsNullOrEmpty line then ""
        else
            let redacted =
                secretRedactPattern.Replace(line, fun m ->
                    m.Groups.[1].Value + m.Groups.[2].Value + "<redacted>")
            if redacted.Length > 200 then redacted.Substring(0, 200) + "…"
            else redacted

    /// CLI process 를 stream LlmEvent 로 어댑트하기 위한 사양.
    type Spec = {
        /// 실행 파일 경로 (이미 PATH 검색 끝낸 fully-qualified 또는 raw name).
        Executable: string
        /// process 인자 (이미 빌드된 list).
        Args: string list
        /// process 환경변수 추가 / 덮어쓰기. 빈 list 면 default 환경 그대로.
        EnvOverrides: (string * string) list
        /// 사용자 prompt 평문 등을 log 에서 가리기 위한 인자 list 변환. log 출력 직전 1회 호출.
        /// 입력 / 출력 길이는 동일해야 함 (formatArgs 가 인덱스 매핑 가정).
        Redact: string list -> string list
        /// stdout 라인 → 0~N LlmEvent 시퀀스 매핑.
        Parser: string -> LlmEvent seq
        /// SessionStarted 캡처 콜백. provider 의 mutable session id 를 갱신.
        OnSessionStarted: string -> unit
        /// process spawn 직후 lifecycle hook (Job Object attach 등). 예외 발생 시 그대로 throw —
        /// 상위에서 fail-fast (CLAUDE.md 정책: 무관한 catch-and-warn 자제).
        OnProcessStarted: (Process -> unit) option
        /// non-zero exit code 메시지 빌더. exitCode + 마지막 stderr 라인.
        /// Codex / Claude 양쪽 모두 stderr suffix 노출 (commit-6b 후속: stream-json input 회귀 진단 위해 Claude 도 활성).
        OnExitNonZero: int -> string -> string
        /// log / error message prefix ("Claude" / "Codex" / 후속 provider 명).
        Label: string
        /// BoundedChannel 용량 (provider option 의 ChannelCapacity 그대로 전달).
        ChannelCapacity: int
        /// stdin 파이프 입력 본문. Some 이면 RedirectStandardInput=true + spawn 직후 1회 write + close.
        /// Windows CreateProcess 의 32K command-line 한도 회피용 — prompt 본문은 인자가 아닌 stdin 으로
        /// 전달 (Ev2.Oracle ClaudeCliProvider 의 stdin 우회 패턴 채택, HelloDSKit 사고 전례).
        Stdin: string option
        /// process 종료 / 실패 후 1회 호출되는 cleanup hook (임시 파일 삭제 등). 예외는 swallow.
        OnFinally: unit -> unit
    }

    /// spec 대로 process 를 spawn 후 stream LlmEvent 를 IAsyncEnumerable 로 노출.
    /// 호출자는 `await foreach` 로 소비. `ct` cancel 시 process kill + cancel ProviderError emit.
    let runStream (spec: Spec) (ct: CancellationToken) : IAsyncEnumerable<LlmEvent> =
        let opts =
            BoundedChannelOptions(
                spec.ChannelCapacity,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false)
        let channel = Channel.CreateBounded<LlmEvent>(opts)
        let writer = channel.Writer
        let reader = channel.Reader

        let writeAsync (evt: LlmEvent) : Task =
            task {
                try
                    do! writer.WriteAsync(evt, ct)
                with
                | :? OperationCanceledException -> ()
                // M4 — outer try 가 writer 를 complete 한 후 sibling task 가 emit 하면 ChannelClosed.
                // emit 무시하고 silent skip — outer ProviderError 와 이중 emit 회피.
                | :? ChannelClosedException -> ()
            } :> Task

        let runProcess () =
            task {
                let psi = ProcessStartInfo(spec.Executable)
                for a in spec.Args do psi.ArgumentList.Add(a)
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.RedirectStandardInput <- spec.Stdin.IsSome
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.StandardOutputEncoding <- System.Text.Encoding.UTF8
                psi.StandardErrorEncoding <- System.Text.Encoding.UTF8
                if spec.Stdin.IsSome then
                    // 한글 prompt 본문이 CP949 등으로 인코딩되어 깨지는 경로 차단.
                    // **BOM 없는 UTF-8** — `System.Text.Encoding.UTF8` 의 default 는 emitUTF8Identifier=true 라
                    // stdin 첫 write 시 BOM (EF BB BF) 이 자동 송출됨. text 모드는 흡수되지만 Claude CLI 의
                    // `--input-format stream-json` line-strict parser 는 BOM 포함 line 을 즉시 reject
                    // ("Error parsing streaming input line" + exit 1).
                    psi.StandardInputEncoding <- System.Text.UTF8Encoding(false)
                for (k, v) in spec.EnvOverrides do
                    psi.Environment.[k] <- v

                let redacted = spec.Redact spec.Args
                Log.provider.Debug($"Spawning: {spec.Executable} {ClaudeCliArgs.formatArgs redacted}")

                // F# `task { }` CE 의 `try with + return ()` 가 일부 시나리오에서 흐름을 끊지 못해
                // 후속 코드가 실행되어 NRE 가 나는 경로를 차단 — Process.Start 결과를 Result 로 캡처 후
                // match 분기로 명시적 종료. (이전 사고: lpCommandLine 32K 초과로 Win32Exception 발생 시
                // proc=null 인 채 use p = proc 통과 → stderrTask 의 p.StandardError.ReadLineAsync 에서 NRE)
                let procResult =
                    try Ok (Process.Start(psi))
                    with ex -> Error ex.Message

                try
                    match procResult with
                    | Error msg ->
                        do! writeAsync(ProviderError $"{spec.Label} CLI 실행 실패: {msg}")
                    | Ok null ->
                        do! writeAsync(ProviderError $"{spec.Label} CLI process 시작 실패 (null process).")
                    | Ok proc ->
                        use p = proc

                        // 1d-5 lifecycle hook. 예외는 그대로 escalate — fail-fast (CLAUDE.md 정책).
                        match spec.OnProcessStarted with
                        | Some cb -> cb p
                        | None -> ()

                        // stderr last non-empty line 캡처. Claude provider 는 본 값을 무시 (OnExitNonZero 가 ""만 받음).
                        // Codex provider 는 stderr 의 인증 / mcp / sandbox 메시지를 exit error 에 append.
                        let mutable lastStderr : string = ""

                        // M4 — inner task 의 unhandled exception 이 sibling task 의 WriteAsync 를 closed channel
                        // 으로 보내거나 outer try 까지 escalation 되어 ProviderError 가 두 번 emit 되는 경로 차단.
                        // 한 task 가 throw 해도 다른 task 는 ct cancel 또는 EOF 까지 자연 종료.
                        let stderrTask =
                            task {
                                try
                                    let mutable line = ""
                                    while not (isNull line) do
                                        let! l = p.StandardError.ReadLineAsync(ct).AsTask()
                                        line <- l
                                        if not (isNull line) && line.Length > 0 then
                                            lastStderr <- line
                                            Log.provider.Warn($"[{spec.Label.ToLowerInvariant()} stderr] {line}")
                                with
                                | :? OperationCanceledException -> ()
                                | ex -> Log.provider.Warn("stderr task 실패", ex)
                            }

                        let stdoutTask =
                            task {
                                try
                                    let mutable line = ""
                                    while not (isNull line) do
                                        let! l = p.StandardOutput.ReadLineAsync(ct).AsTask()
                                        line <- l
                                        if not (isNull line) then
                                            if Log.rawStream.IsDebugEnabled then
                                                Log.rawStream.Debug(line)
                                            for evt in spec.Parser line do
                                                match evt with
                                                | SessionStarted(sid, _, _, _) -> spec.OnSessionStarted sid
                                                | _ -> ()
                                                do! writeAsync evt
                                with
                                | :? OperationCanceledException -> ()
                                | ex ->
                                    Log.provider.Warn("stdout task 실패", ex)
                                    do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
                            }

                        // stdin 본문 1회 write + close. 32K 초과 prompt 를 인자 대신 파이프로 전달.
                        // BrokenPipeException 등은 stdout/stderr 측 에러로 이미 진단되므로 swallow.
                        let stdinTask =
                            task {
                                match spec.Stdin with
                                | Some text ->
                                    try
                                        do! p.StandardInput.WriteAsync(text.AsMemory(), ct)
                                        p.StandardInput.Close()
                                    with
                                    | :? OperationCanceledException -> ()
                                    | ex -> Log.provider.Warn("stdin task 실패", ex)
                                | None -> ()
                            }

                        let killOnCancel =
                            ct.Register(fun () ->
                                try
                                    if not p.HasExited then p.Kill(true)
                                with _ -> ())

                        try
                            try
                                let! _ = Task.WhenAll(stdoutTask :> Task, stderrTask :> Task, stdinTask :> Task)
                                p.WaitForExit()
                                if p.ExitCode <> 0 then
                                    // M11 — stderr suffix 사용자 노출 시 secret 토큰 redact + 200자 cap.
                                    // CLI 가 향후 인증 토큰 / API key 를 stderr 로 leak 하는 회귀 대비.
                                    let sanitizedStderr = redactStderr lastStderr
                                    do! writeAsync(ProviderError (spec.OnExitNonZero p.ExitCode sanitizedStderr))
                            with
                            | :? OperationCanceledException ->
                                do! writeAsync(ProviderError "사용자 취소.")
                            | ex ->
                                do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
                        finally
                            killOnCancel.Dispose()
                finally
                    try spec.OnFinally()
                    with ex -> Log.provider.Warn($"{spec.Label} OnFinally cleanup 실패: {ex.Message}", ex)
                    writer.TryComplete() |> ignore
            }

        let bg = runProcess()
        bg.ContinueWith(fun (t: Task) ->
            if t.IsFaulted && not (isNull t.Exception) then
                Log.provider.Error($"{spec.Label}CliProvider background task faulted", t.Exception)
            writer.TryComplete() |> ignore
        ) |> ignore

        reader.ReadAllAsync(ct)
