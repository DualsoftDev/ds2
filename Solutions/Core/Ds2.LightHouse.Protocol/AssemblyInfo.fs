module Ds2.LightHouse.Protocol.AssemblyInfo

/// **K4 Protocol SSOT 통합 (A2, 2026-05-20)** — server F# (`Ds2.LightHouseService`) ↔ client C# (`Promaker.Knowledge`)
/// ↔ client F# (`Ds2.LightHouse.Cli`) 의 wire-level 계약 양분 박제 → 단일 SSOT.
///
/// 책임:
/// - wire 상수 SSOT — endpoint name / SSE event name / header / mime / zip layout (`WireConstants.fs`).
/// - SSE event name 5종 (`ServerEventNames.fs`) — server F# `EventBus.fs` 양분 박제 + client C# `ServerEventNames.cs`
///   3중 박제 모두 본 module 1회 박제로 정합 (K4 결함 해소).
/// - `meta.json` schema SSOT (`MetaJson.fs`) — server F# `MetaJson.fs` + client C# `CollectionPackager.MetaPayload`
///   + cli F# `Packager.MetaDto` 3중 박제 모두 본 record + module 1회 박제로 정합 (K4 결함 해소).
///
/// 비-책임:
/// - HTTP 호출 본체 (HttpClient / endpoint method 시그니처) — server F# / client C# / client F# 각자 유지.
/// - server-side endpoint helper (`writeJson` / `writeError` / `userIdentityOf` / `jsonOpts`) — F# server 전용,
///   본 phase 범위 밖. server 내부 `EndpointHelpers.fs` 별 module 추출은 C-15 잔여 backlog.
/// - domain logic (Indexer / Searcher / Storage) — `Ds2.LightHouse` lib 영역.
///
/// 의존:
/// - 본 project 는 **dependency 0** (FSharp.Core 만). 모든 wire schema 의 base — server / Promaker / cli 가 본 project 참조.
