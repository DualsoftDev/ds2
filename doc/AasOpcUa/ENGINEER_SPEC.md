# Ds2 · AAS × OPC UA 통합 · 엔지니어 스펙 (v4 — 2026-08-03, Agent 소유)

> 이 문서는 **개발자 발표자료**로 사용된다.
> 대상: 신규 합류 F#/백엔드 엔지니어 · 아키텍처 리뷰어 · IT/OT 통합 담당자.
> **현재 구현된 상태**와 **왜 그렇게 되었는지**를 정리한다. 상위 요약은 [00_OVERVIEW.md](00_OVERVIEW.md).

---

## 0. TL;DR (30초)

- **목표**: 공정 자산을 IDTA AAS + OPC UA 로 표준화하여, 편집 → 실시간 노출 → 수집 → 소비 파이프라인을 한 저장소로 관리.
- **v4 결정**: 배포용 `project.aasx`가 모델/AID SSOT이고 Agent가 UA 서버를 소유한다. Agent watcher가 AASX 변경을 감지하면 runtime과 UA 주소공간을 함께 재구성한다.
- **원칙**:
  1. **파일 = SSOT**. REST 계층 없음 → 인증/네트워크/장애면 축소.
  2. **NodeId 결정론** — 자산이 재배포되어도 클라이언트 구독은 살아남는다 (ADR-002).
  3. **SQLite-first** — Collector 는 SQLite. Kafka/Influx 는 확장 (ADR-011).
  4. **인터페이스는 실 구현 ≥ 2 개일 때만**.

**프로세스 수**: 배포 기준 2개 (**Agent[UA 내장] + Collector**).

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
└─ Promaker/                    ← Agent(UA 정식 소유) + WPF 데모 + Shared bridges
```

**존재하지 않는 프로젝트** (문서 곳곳에 언급됨): `Ds2.AasHost`, `Ds2.DataService`, `Ds2.Backend.Plc.UaBridge`, `Ds2.Adapter.{Common,OpcUaClient,Modbus,Mqtt,Http,AutoId}`.
- AasHost/DataService: 방법 A(2026-07-27) 에서 삭제됨.
- Adapter 시리즈: `InterfaceXGT`는 기존 `Ds2.Backend.Plc` gateway를 재사용한다. 표준 AID 4종 southbound client는 후속이다.
- Envelope/EdgeBuffer 는 `Ds2.Collector` 어셈블리에 있음 (별도 Adapter.Common 없음).

빌드: `dotnet build Solutions/Ds2.sln`.
테스트: `dotnet test Solutions/Tests/Ds2.AasOpcUa.AllTests/Ds2.AasOpcUa.AllTests.fsproj`.

---

## 2. 데이터 흐름 (End-to-End)

```
 AASX Editor/Promaker ─▶ project.aasx ─▶ Agent watcher/import
                                           │
                    ┌──────────────────────▼──────────────────────┐
 PLC ◀▶ Pi5/Agent ─▶│ runtime batch → AID signalId UA Variables  │
                    │ EventDrivenEngine + EmbeddedUaServer :62541 │
                    └──────────────────────┬──────────────────────┘
                                           │ OPC UA subscription
                                 ┌─────────▼──────────┐
                                 │ Collector          │
                                 │ SQLite + Data REST │
                                 └────────────────────┘
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
  - AASX: `SystemProperties/System_<guid>/SignalPoliciesCollection`으로 시스템별 왕복.
  - UA: 각 AID Variable의 HasProperty 자식으로 mode/sampling/publishing/deadband/queue/retention 노출.
  - Collector: policy별 subscription/MonitoredItem 설정 및 SQLite raw retention 적용.
  - `DeadbandPercent`는 OPC UA EURange가 없는 현재 XGT schema에서는 메타데이터만 보존하고 안전하게 미적용하며 경고한다. `DeadbandAbsolute`는 서버 필터로 적용된다.
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
- **EmbeddedUaServer**: AID interaction 자동 노드 투영과 `WriteAidSignal`/`WriteWorkState`/`WriteCallState`/`WriteRuntimeIo` API.
- **정책**: 서버는 값을 스스로 만들지 않는다. 모든 `Write` 는 외부 (Promaker in-process bridge, 또는 계획된 UA Client 어댑터) 담당.

### 3.3 Agent in-process host (Phase 3.5)

**Agent가 UA 서버를 정식 구동**하며 Promaker WPF 경로는 기본 OFF 데모 호환용이다:
- `Promaker.Shared.OpcUaServerSettings` — JSON 설정 (endpoint, 포트, 인증 옵션).
- `OpcUaServerHost` — Agent는 독립 인스턴스/데이터 루트, WPF는 호환 `Instance` 사용.
- `SimEngineUaBridge`:
  - `engine.WorkStateChanged` 이벤트 → `WriteWorkState`.
  - `engine.CallStateChanged` 이벤트 → `WriteCallState`.
  - `engine.State.IOValues` 를 200ms 주기 폴링 → `WriteRuntimeIo` (diff 캐시로 변경분만 push).
- 초기 스냅샷 push 로 `BadWaitingForInitialData` 해제.
- `AidUaValueBridge`: 직접 scan/Pi5 delegated scan의 공통 `TagWrite[]`를 XGT address→AID signalId로 변환해 UA에 기록.

### 3.4 Ds2.Collector (Envelope + Sink + Data API)

- **Envelope** (`Envelope.fs`): `EnvelopeId` (Guid), `Kind` (Sample|Event), `GlobalAssetId`, `SignalId`, `SourceTimestamp`, `Value` (Double|Long|String|Bool|None), `StatusCode`, `Unit`, `Origin`, event 시 `EventPayloadJson` · `EventTypeSemanticId`.
- **EdgeBuffer** (`EdgeBuffer.fs`):
  - SQLite outbox. 파일명 `pending.db`.
  - Schema: `pending(envelope_id BLOB PK, payload BLOB, kind TEXT, priority INT, attempts INT, next_retry_us INT, created_us INT)`.
  - Priority: `Event(0) < Sample(1)` — 이벤트가 항상 먼저 pull.
  - Idempotent enqueue, `Requeue` 는 backoff 스케줄.
- **UaWriterContract** — `IUaWriter` + `InMemoryUaWriter` (테스트 전용).
- **UaSubscriptionService** — UA asset namespace browse, Variable의 CollectionPolicy Property 읽기, publishing별 subscription 그룹, 신호별 sampling/deadband/queue, reconnect, bounded channel, SQLite batch persist.

- **SqliteSinkWriter**: envelope batch 를 `telemetry.db` (samples) / `events.db` (events) 로 라우팅. `EnvelopeId` dedup.
- **DownsampleScheduler**: `signals` → `signals_1h` → `signals_1d` 롤업.
- **DataApi** (Phase 7 REST 통합):
  - `GET /v1/series?seriesId=…&rangeSeconds=…` — range 크기에 따라 자동 테이블 선택.
  - `GET /v1/events?asset=…&eventType=…` — events.db 쿼리.
  - `GET /healthz`, `GET /v1/info`.
- **SeriesIdRegistry**: UA browse 결과로 in-memory series/retention 매핑을 채움.
- **RetentionService**: 정책의 ISO-8601 retention에 따라 신호별 `signals` raw row를 주기적으로 정리.

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
dotnet run --project Solutions/Runtime/Ds2.OpcUa.Server -p:Ds2OpcUaStandalone=true

# Collector (Sink + Data API)
dotnet run --project Solutions/Runtime/Ds2.Collector
```

환경변수:
- `DS2_AIDSTORE_ROOT` — AAS 파일 SSOT 루트 (기본: `/var/ds/aid-store`).
- `DS2_UASERVER_ROOT` — UA 서버 상태/인증서 (기본: `./data/opcua-server`).
- `DS2_COLLECTOR_ROOT` — telemetry.db/events.db 루트 (기본: `./data/collector`).
- `DS2_UA_SUBSCRIBE_ENABLED` / `DS2_UA_ENDPOINT` — Collector UA 구독 on/off 및 Agent endpoint.
- `DS2_UA_USE_SECURITY` — Collector가 보안 endpoint를 선택할지 여부(기본 `true`).
- `DS2_UA_USE_CERTIFICATE_IDENTITY` — Collector client 인증서를 user identity로 사용할지 여부(기본 `true`).
- `DS2_UA_AUTO_ACCEPT_UNTRUSTED` — 미등록 서버 인증서 자동 신뢰 여부(기본 `false`, 로컬 개발에서만 임시 사용).

Agent의 파일 미존재 기본 설정은 `allowAnonymous=false`, `allowUnsecuredEndpoint=false`,
`autoAcceptUntrustedCertificates=false`다. 첫 연결 시 Agent와 Collector의 `rejected` 인증서를 검토해
상대편 `trusted` 저장소에 승인해야 한다. Agent의 주소공간/인증서 루트는
`{SharedDirectory}/agent/opcua`, Collector client 루트는 `{DS2_COLLECTOR_ROOT}/ua-client`다.

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
