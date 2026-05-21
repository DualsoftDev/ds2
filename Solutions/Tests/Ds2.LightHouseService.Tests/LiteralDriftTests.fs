module Ds2.LightHouseService.Tests.LiteralDriftTests

open System.Reflection
open Xunit

/// **s6-r25 m6 — F# [<Literal>] cross-assembly drift 가드 Fact**.
///
/// 배경: F# `[<Literal>]` 은 compile-time const inline 으로 caller assembly 에 박제됨 → lib 만 bump
/// 후 caller assembly 가 stale build 인 경우 두 값 drift. paired-release ps1 (s5d-r0) 는 source
/// regex 추출로 build-time drift 차단 — 본 fact 는 runtime test 측 추가 guard.
///
/// SSOT = lib 의 `KnowledgeBase.MaxAttachedDbs` Literal. 본 test 가 service.Tests assembly 안에서
/// run → service.dll inline 값 ↔ lib metadata 의 값 비교. service 가 stale build 상태면 두 값
/// drift → 회귀 검출.
[<Fact>]
let ``KnowledgeBase.MaxAttachedDbs Literal cross-assembly drift 0`` () =
    let inlineValue : int = Ds2.LightHouse.KnowledgeBase.MaxAttachedDbs

    // lib metadata 의 Literal 은 PE 의 static literal field. reflection 으로 raw constant 추출 가능.
    // F# `module KnowledgeBase` ([<RequireQualifiedAccess>]) 가 same-name record type 과 충돌 시
    // module 은 `KnowledgeBaseModule` suffix 로 emit (CompilationRepresentationFlags.ModuleSuffix 자동 적용).
    let asm = typeof<Ds2.LightHouse.FileKind>.Assembly  // lib 의 임의 type 으로 assembly 핸들
    // **fragility 박제** (s6-r25 자가 검열 review Minor-3): 향후 same-name record/type 제거 시 F#
    // compiler 가 `Module` suffix 자동 소실 → PE type 명이 `Ds2.LightHouse.KnowledgeBase` 로 환원
    // (LiteralDriftTests false-negative). type 충돌 해소 시 본 lookup 의 fallback 분기로 갱신 의무.
    let modType = asm.GetType "Ds2.LightHouse.KnowledgeBaseModule"
    if isNull modType then
        Assert.Fail "Ds2.LightHouse.KnowledgeBaseModule PE type 미발견 — module 위치 변경 또는 KnowledgeBase record/type 제거로 인한 emit 명 환원 시 본 fact 갱신 의무"
    let field = modType.GetField("MaxAttachedDbs", BindingFlags.Public ||| BindingFlags.Static)
    if isNull field then
        Assert.Fail "MaxAttachedDbs Literal field 미발견 — Literal 명 변경 시 본 fact 갱신 의무"
    let metadataValue = field.GetRawConstantValue() :?> int

    Assert.Equal(inlineValue, metadataValue)
