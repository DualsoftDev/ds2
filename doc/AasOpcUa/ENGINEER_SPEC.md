# Ds2 · AAS × OPC UA 통합 · 엔지니어 스펙 (v3 — 2026-07-27, 방법 A)

> 이 문서는 **개발자 발표자료**로 사용된다.
> 대상: 신규 합류 F#/백엔드 엔지니어 · 아키텍처 리뷰어 · IT/OT 통합 담당자.
> **현재 구현된 상태**와 **왜 그렇게 되었는지**를 정리한다. 상위 요약은 [00_OVERVIEW.md](00_OVERVIEW.md).

---

## 0. TL;DR (30초)

- **목표**: 공정 자산을 IDTA AAS + OPC UA 로 표준화하여, 편집 → 실시간 노출 → 수집 → 소비 파이프라인을 한 저장소로 관리.
- **v3 결정 · 방법 A**: **AAS 파일 자체가 SSOT**. Ds2.AasHost REST 서버 삭제. 편집자·CI 는 `aid-store` 에 파일 직접 write. UA Server 는 사용자가 `Reload()` 를 명시적으로 호출해야 변경을 반영 (수동 로딩 — 오탐/락 이슈 회피).
- **원칙**:
  1. **파일 = SSOT**. REST 계층 없음 → 인증/네트워크/장애면 축소.
  2. **NodeId 결정론** — 자산이 재배포되어도 클라이언트 구독은 살아남는다 (ADR-002).
  3. **SQLite-first** — Collector 는 SQLite. Kafka/Influx 는 확장 (ADR-011).
  4. **인터페이스는 실 구현 ≥ 2 개일 때만**.

**프로세스 수**: v1 = 4개 (AasHost + UA + Collector + DataService) → v3 = 2개 (**UA + Collector**).

---

## 1. 프로젝트 지형

```
Solutions/
├─ Core/
│  └─ Ds2.Core/                 ← 도메인 모델 · KPI 파이프라인 · StandardSubmodels · Base64Url
├─ Convert/
│  └─ Ds2.Aasx/                 ← .aasx 파일 import/export (IDTA Part 5) · Phase 0 SM
├─ Runtime/
│  ├─ Ds2.Collector/            ← Envelope · EdgeBuffer · Sink writer · Downsample · Data REST
│  └─ Ds2.OpcUa.Server/         ← 임베디드 UA 서버 (aggregator) + AasFileScanner
└─ Tests/
   ├─ Ds2.AasOpcUa.AllTests/    ← 통합 진입점 aggregator
   ├─ Ds2.Aasx.Tests/
   ├─ Ds2.Collector.Tests/
   ├─ Ds2.Core.Tests/
   └─ Ds2.OpcUa.Server.Tests/

Apps/
├─ Ds2.AasOpcUa.Tutorial.Web/   ← Blazor Server · UA 라이브 뷰어 · pilot seeder
└─ Promaker/                    ← WPF · OpcUaServerHost 내장 · SimEngineUaBridge
```

**존재하지 않는 프로젝트** (문서 곳곳에 언급됨): `Ds2.AasHost`, `Ds2.DataService`, `Ds2.Backend.Plc.UaBridge`, `Ds2.Adapter.{Common,OpcUaClient,Modbus,Mqtt,Http,AutoId}`.
- AasHost/DataService: 방법 A(2026-07-27) 에서 삭제됨.
- Adapter 시리즈: 현재는 Promaker `SimEngineUaBridge` 가 유일한 in-process publisher.
- Envelope/EdgeBuffer 는 `Ds2.Collector` 어셈블리에 있음 (별도 Adapter.Common 없음).

빌드: `dotnet build Solutions/Ds2.sln`.
테스트: `dotnet test Solutions/Tests/Ds2.AasOpcUa.AllTests/Ds2.AasOpcUa.AllTests.fsproj`.

---

## 2. 데이터 흐름 (End-to-End)

```
        ┌───────────────────────────────────────────────────┐
        │   AAS 파일 SSOT   /var/ds/aid-store               │
        │   ├─ shells/{aasIdBase64Url}.json                 │
        │   ├─ submodels/{smIdBase64Url}.json               │
        │   ├─ concept-descriptions/{cdIdBase64Url}.json    │
        │   └─ packages/{packageIdBase64Url}.aasx           │
        └────┬─────────────────────────────────┬────────────┘
             │ write (SMB/scp/git/직접)         │ 수동 Reload() 호출
             │                                  ▼
   [Editor · Vendor CI · 사람]         ┌─────────────────────┐
                                        │ Ds2.OpcUa.Server    │
                                        │  · AasFileScanner   │
                                        │  · DeterministicNs  │
                                        │  · Aggregator only  │
                                        └──────────┬──────────┘
                                                   ▲ UA Write
                                                   │
                                            [OT Adapters/PLC]
                                                   │ Envelope · outbox
                                                   ▼
                                        ┌─────────────────────┐
                                        │ Ds2.Collector       │
                                        │  · SqliteSinkWriter │
                                        │  · Downsample       │
                                        │  · Data REST API    │
                                        └─────────────────────┘
                                                   ▲
                                                   │ GET /v1/series
                                                   │ GET /v1/events
                                                [IT/Cloud]
```

핵심 계약:
- **AAS 파일**: 파일 자체가 SSOT. Base64Url 파일명 (ADR-009). 편집은 원자적 rename.
- **Envelope**: `EnvelopeId` (Guid) 로 dedup, `SourceTimestamp` 는 어댑터 소유.
- **NodeId**: `ns={idx};s={identifier}` — identifier ∈ {"Asset", `{signalId}`, "Events", "Events/RaiseAssetEvent"}. namespace `urn:ds:asset:{Base64Url(gaid)}` 자산별 할당.
- **엔드포인트**: `opc.tcp://localhost:62541/Ds2/OpcUa/Server` (기본, `OpcUaServerSettings` 로 조정).

---

## 3. 핵심 모듈 요약

### 3.1 Ds2.Core (모델 · KPI 파이프라인)

- **식별자 값 타입**: `SignalId` (kebab-case, [a-z0-9-_.], 128자 이내), `GlobalAssetId` (URI/IRDI, 2048자), `SemanticId` (URI/IRDI, 2048자).
- **AAS 도메인**: `AssetInterfacesDescription`, `OperationalData`, `AssetInterfacesMappingConfiguration` — F# 클래스 (member val), Provenance HashSet 포함.
- **SequenceSubmodels/04_Logging**: `SignalPolicy` · `AcquisitionMode` · `Iso8601Duration.isValid` — CollectionPolicy 는 별도 SM 이 아니라 SequenceLogging SM 흡수.
- **KPI 파이프라인** (`Kpi/`):
  - `KpiKits.all` — Convention-driven 5 엔티티 (System/Work/Call/ArrowWork/UserTag) × 고정 메트릭.
  - SemanticId 규칙: `urn:ds:kpi/{Entity}/{Metric}/1/0`.
  - `KpiIdentifiers.idShort` — `Kpi_{typeShort}_{hash8}_{sanitizedMetric}`.
  - `KpiIdentifiers.signalId` — `{prefix}.{typeShort}.{hash8}.{metric-kebab}-{metricHash}`.
  - `KpiWalker` — Active System · ArrowWork · UserTag만 walk (Work/Call 자체 KPI 는 미생성).
  - `KpiAppenders` — AID/OpData/AIMC 3-tuple guard 로 idempotent append.
  - `SequenceKpiGenerator.appendForProject` — 상위 API, `KpiGenerationStats` 반환.
- **Base64Url** (`Encoding/Base64Url.fs`): ADR-009 인코딩 · .NET framework 호환.

### 3.2 Ds2.OpcUa.Server (aggregator + 파일 스캐너)

- OPC Foundation `OPCUA-.NETStandard` 1.5.375.457 스택 임베드.
- **DeterministicNodeId** (`NodeIds/DeterministicNodeId.fs`): `NodeIdKind` DU — AssetFolder, Variable(signalId), EventsFolder, RaiseAssetEventMethod.
- **NamespaceAllocator**: `urn:ds:asset:{Base64Url(gaid)}` per-asset namespace. `nodeset-state.json` 에 atomic write.
- **AasFileScanner** (`AasClient/AasFileScanner.fs`):
  - `<aid-store>/{shells|submodels|concept-descriptions|packages}` 4개 디렉토리 감시.
  - `Reload()` 명시 호출 시 델타 (Added/Modified/Removed) 반환. FileSystemWatcher 없음.
- **DsNodeManager**: 계층 브라우저 — Assets/{idShort}/{System KPI, Transitions, Works, Calls, IO}.
- **EmbeddedUaServer**: `BrowseFolders` 폴더 상수, `WriteWorkState`/`WriteCallState`/`WriteRuntimeIo` 3개 write API.
- **정책**: 서버는 값을 스스로 만들지 않는다. 모든 `Write` 는 외부 (Promaker in-process bridge, 또는 계획된 UA Client 어댑터) 담당.

### 3.3 Promaker in-process host (Phase 3.5)

Phase 4 어댑터가 없는 대신 **Promaker 가 UA 서버를 직접 구동**한다:
- `Promaker.Shared.OpcUaServerSettings` — JSON 설정 (endpoint, 포트, 인증 옵션).
- `OpcUaServerHost` — `EmbeddedUaServer` 를 singleton 으로 구동 (`Instance`).
- `SimEngineUaBridge`:
  - `engine.WorkStateChanged` 이벤트 → `WriteWorkState`.
  - `engine.CallStateChanged` 이벤트 → `WriteCallState`.
  - `engine.State.IOValues` 를 200ms 주기 폴링 → `WriteRuntimeIo` (diff 캐시로 변경분만 push).
- 초기 스냅샷 push 로 `BadWaitingForInitialData` 해제.

### 3.4 Ds2.Collector (Envelope + Sink + Data API)

- **Envelope** (`Envelope.fs`): `EnvelopeId` (Guid), `Kind` (Sample|Event), `GlobalAssetId`, `SignalId`, `SourceTimestamp`, `Value` (Double|Long|String|Bool|None), `StatusCode`, `Unit`, `Origin`, event 시 `EventPayloadJson` · `EventTypeSemanticId`.
- **EdgeBuffer** (`EdgeBuffer.fs`):
  - SQLite outbox. 파일명 `pending.db`.
  - Schema: `pending(envelope_id BLOB PK, payload BLOB, kind TEXT, priority INT, attempts INT, next_retry_us INT, created_us INT)`.
  - Priority: `Event(0) < Sample(1)` — 이벤트가 항상 먼저 pull.
  - Idempotent enqueue, `Requeue` 는 backoff 스케줄.
- **UaWriterContract** — `IUaWriter` + `InMemoryUaWriter` (테스트 전용).

- **SqliteSinkWriter**: envelope batch 를 `telemetry.db` (samples) / `events.db` (events) 로 라우팅. `EnvelopeId` dedup.
- **DownsampleScheduler**: `signals` → `signals_1h` → `signals_1d` 롤업.
- **DataApi** (Phase 7 REST 통합):
  - `GET /v1/series?seriesId=…&rangeSeconds=…` — range 크기에 따라 자동 테이블 선택.
  - `GET /v1/events?asset=…&eventType=…` — events.db 쿼리.
  - `GET /healthz`, `GET /v1/info`.
- **SeriesIdRegistry**: in-memory 매핑. 후속에서 aid-store 스캔으로 채움.

---

## 4. Simplification (v1 → v3) — 이번 리팩토링에서 제거된 것

| # | 제거된 것 | 이유 |
|---|---|---|
| 1 | AasxEditor 의 Ds2 skeleton 5개 페이지 | 모두 disabled 버튼 · 빈 테이블 |
| 2 | `Ds2.AasOpcUa.Tutorial.Web` Docs.razor + 하드코딩 상태 표 | 이 문서로 이관 |
| 3 | `Ds2.Backend.Plc.UaBridge` 프로젝트 (전체) | Program.fs 없음, 자체 테스트에서만 참조 — 죽은 코드 |
| 4 | `Ds2.AasHost.Api.Base64UrlHelper` | `Ds2.Core.Encoding.Base64Url` 와 중복 |
| 5 | `ISinkWriter` / `IEdgeBuffer` 인터페이스 | 각 1개 구현 · 인터페이스 불필요 |
| 6 | `DedupGuard` 모듈 (KpiAppenders) | `ensureMany [t] \|> List.head` 로 위임 가능 |
| 7 | AasHost 3개 컨트롤러 save/delete 중복 | (이후 8번에서 프로젝트 자체 삭제됨) |
| 8 | `Ds2.DataService` 별도 프로젝트 | Collector 프로세스와 통합 (Web SDK 전환) |
| **9** | **`Ds2.AasHost` 프로젝트 전체 + `Ds2.AasHost.Tests`** | **방법 A: 파일 SSOT 로 전환. REST · Journal · SignalR 전부 삭제** |
| 10 | Tutorial.Web `AasHostStatusService` + Assets.razor 원격 조회 | AasHost 삭제로 무의미 |
| 11 | `Dockerfile.aashost`, `Dockerfile.dataservice` + docker-compose 항목 | 프로세스 통합에 따른 정리 |

**의도**:
- YAGNI · KISS · DRY 위주.
- 인터페이스는 **≥ 2 개 구현이 실존할 때** 만 만든다.
- 프로세스 = 실제 배포 단위. 지금 필요한 최소 개수 유지.

---

## 5. 아키텍처 결정 요약

원본 ADR 문서는 삭제(2026-07-28). 코드에 반영된 핵심 결정만 남긴다:

| # | 결정 | 코드 상의 반영 |
|---|---|---|
| 001 | UA 서버는 순수 aggregator | `DsNodeManager` 는 `Write` accept, 자체 값 생성 없음 |
| 002 | Deterministic NodeId | `NodeIds/DeterministicNodeId.fs`, `NodeIds/NamespaceAllocator.fs` |
| 006 | At-least-once + dedup | `Envelope.EnvelopeId` + `SqliteSinkWriter` upsert dedup |
| 009 | Base64Url ID | `Ds2.Core.Encoding.Base64Url` 로 통합 |
| 011 | SQLite-first | Collector · Adapter outbox 모두 SQLite (WAL, synchronous NORMAL) |

**폐기된 결정** (방법 A 로 무효화):
- **AAS API 프로파일** — REST API 미제공, 파일 SSOT 로 대체.
- **AasHost 인증·인가** — AasHost 삭제로 무효화. 파일 시스템 권한으로 대체.

---

## 6. 빌드 · 테스트 · 실행

### 빌드
```powershell
dotnet build Solutions/Ds2.sln
```

### 테스트
```powershell
dotnet test Solutions/Tests/Ds2.AasOpcUa.AllTests/Ds2.AasOpcUa.AllTests.fsproj
```
개별 프로젝트: Ds2.Core.Tests · Ds2.Aasx.Tests · Ds2.OpcUa.Server.Tests · Ds2.Collector.Tests · Promaker.Tests.

### 실행 (개발)
```powershell
# OPC UA Server (aggregator + 파일 SSOT 스캐너)
dotnet run --project Solutions/Runtime/Ds2.OpcUa.Server

# Collector (Sink + Data API)
dotnet run --project Solutions/Runtime/Ds2.Collector
```

환경변수:
- `DS2_AIDSTORE_ROOT` — AAS 파일 SSOT 루트 (기본: `/var/ds/aid-store`).
- `DS2_UASERVER_ROOT` — UA 서버 상태/인증서 (기본: `./data/opcua-server`).
- `DS2_COLLECTOR_ROOT` — telemetry.db/events.db 루트 (기본: `./data/collector`).

### 배포 (Docker)
```powershell
docker compose -f deploy/docker-compose.yml up -d
```
컨테이너: `ds2-opcua-server` + `ds2-collector` (+ Vault, Prometheus, Grafana).
`aid-store` 는 Editor / OT 도구가 공유하는 볼륨.

---

## 7. 발표 슬라이드 흐름 (제안)

1. **왜 파일 SSOT 인가** — REST 인프라를 세우기 전에 파일이면 충분한 시점.
2. **아키텍처 한 장** — §2 다이어그램.
3. **핵심 원칙** — 파일 = SSOT / Aggregator / SQLite-first / 인터페이스 최소화.
4. **모듈 투어** — §3 요약, 실물 파일 열어서 보여주기.
5. **What we simplified** — §4 표. v1 → v3 로의 정리 여정.
6. **다음 phase** — [00_OVERVIEW.md](00_OVERVIEW.md) §1 표에서 🟡/📋 항목.
7. **Q & A** — 코드가 사실. 문서/코드 충돌 시 코드 우선.

---

## 8. 앞으로의 작업 (roadmap 요약)

| Phase | 남은 작업 |
|---|---|
| 2 (AAS SSOT) | ADR-004/005 개정. `aid-store` 접근 권한 · git 감사 · 파일 락 정책. |
| 3 (OPC UA) | AutoID NodeSet 완전 로딩, `RaiseAssetEvent` method, RolePermissions (ADR-010). |
| 4 (Adapters) | Fuji ZWT / Optical Scanner 어댑터 실제 구현. |
| 5 (Editor) | AasxEditor 가 `aid-store` 직접 write (원자적 rename). |
| 7 (Data / Gov) | SeriesIdRegistry 를 aid-store 스캐너로 채움. Data API mTLS · rate-limit. |
| 8 (Pilot) | 실제 라인 배포, git 기반 감사 로그 검증. |

---

*작성: 2026-07-27 · 브랜치: `bAASopcUA` · 담당: ahn@dualsoft.com*
