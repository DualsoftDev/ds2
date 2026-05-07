namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Diagnostics
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
        /// non-zero exit code 메시지 빌더. exitCode + 마지막 stderr 라인 (Codex 전용 — Claude 는 "" 무시).
        OnExitNonZero: int -> string -> string
        /// log / error message prefix ("Claude" / "Codex" / 후속 provider 명).
        Label: string
        /// BoundedChannel 용량 (provider option 의 ChannelCapacity 그대로 전달).
        ChannelCapacity: int
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
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.StandardOutputEncoding <- System.Text.Encoding.UTF8
                psi.StandardErrorEncoding <- System.Text.Encoding.UTF8
                for (k, v) in spec.EnvOverrides do
                    psi.Environment.[k] <- v

                let redacted = spec.Redact spec.Args
                Log.provider.Debug($"Spawning: {spec.Executable} {ClaudeCliArgs.formatArgs redacted}")

                let mutable proc : Process = null
                try
                    proc <- Process.Start(psi)
                with ex ->
                    do! writeAsync(ProviderError $"{spec.Label} CLI 실행 실패: {ex.Message}")
                    writer.TryComplete() |> ignore
                    return ()

                if isNull proc then
                    do! writeAsync(ProviderError $"{spec.Label} CLI process 시작 실패 (null process).")
                    writer.TryComplete() |> ignore
                    return ()

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

                let killOnCancel =
                    ct.Register(fun () ->
                        try
                            if not p.HasExited then p.Kill(true)
                        with _ -> ())

                try
                    try
                        let! _ = Task.WhenAll(stdoutTask :> Task, stderrTask :> Task)
                        p.WaitForExit()
                        if p.ExitCode <> 0 then
                            do! writeAsync(ProviderError (spec.OnExitNonZero p.ExitCode lastStderr))
                    with
                    | :? OperationCanceledException ->
                        do! writeAsync(ProviderError "사용자 취소.")
                    | ex ->
                        do! writeAsync(ProviderError $"Stream 파싱 실패: {ex.Message}")
                finally
                    killOnCancel.Dispose()
                    writer.TryComplete() |> ignore
            }

        let bg = runProcess()
        bg.ContinueWith(fun (t: Task) ->
            if t.IsFaulted && not (isNull t.Exception) then
                Log.provider.Error($"{spec.Label}CliProvider background task faulted", t.Exception)
            writer.TryComplete() |> ignore
        ) |> ignore

        reader.ReadAllAsync(ct)
