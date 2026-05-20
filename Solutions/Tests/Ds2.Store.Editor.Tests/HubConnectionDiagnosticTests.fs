module Ds2.Store.Editor.Tests.HubConnectionDiagnosticTests

open System
open Ds2.Backend.Common.HubConnectionDiagnostic
open Xunit

[<Fact>]
let ``classifyException — TimeoutException → Timeout`` () =
    let ex = TimeoutException("operation timed out")
    let d = classifyException ex
    match d with
    | Timeout _ -> ()
    | other -> Assert.Fail(sprintf "expected Timeout, got %A" other)

[<Fact>]
let ``classifyException — connection refused message → ConnectionRefused`` () =
    let ex = Exception("connection refused by remote host")
    Assert.Equal(ConnectionRefused, classifyException ex)

[<Fact>]
let ``classifyException — 401 message → AuthenticationFailed`` () =
    let ex = Exception("HTTP 401 Unauthorized")
    Assert.Equal(AuthenticationFailed, classifyException ex)

[<Fact>]
let ``classifyException — unknown → InternalError preserves message`` () =
    let ex = Exception("something unexpected")
    match classifyException ex with
    | InternalError m -> Assert.Equal("something unexpected", m)
    | other -> Assert.Fail(sprintf "expected InternalError, got %A" other)

[<Fact>]
let ``diagnosticLabel — NetworkUnreachable 한국어 표시`` () =
    let label = diagnosticLabel NetworkUnreachable
    Assert.Contains("네트워크", label)

[<Fact>]
let ``diagnosticLabel — Reconnecting 은 attempt + ETA 표시`` () =
    let label = diagnosticLabel (Reconnecting (3, 2500))
    Assert.Contains("#3", label)
    Assert.Contains("2.5", label)

[<Fact>]
let ``diagnosticLabel — Timeout with ms 표시`` () =
    let label = diagnosticLabel (Timeout 5000)
    Assert.Contains("5000", label)

[<Fact>]
let ``diagnosticLabel — ConnectionRefused 한국어 표시`` () =
    let label = diagnosticLabel ConnectionRefused
    Assert.Contains("연결 거부", label)

[<Fact>]
let ``diagnosticLabel — AuthenticationFailed 한국어 표시`` () =
    let label = diagnosticLabel AuthenticationFailed
    Assert.Contains("인증 실패", label)
