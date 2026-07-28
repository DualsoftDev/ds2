# AAS × OPC UA 통합 아키텍처 — 구현 현황 v3

**리비전**: v3 (2026-07-28) · 실제 구현 반영
**대상 저장소**: `C:\ds\ds2\` (Solutions + Apps)
**기반 스펙**: `D:\AI\DsSpec\opcUA\AAS-OPCUA_아키텍처_제안서_v9.html` (DS-PRO-2026-014 REV 9.0)

---

## 0. 이 문서의 성격

이 문서는 **현재 저장소에 실제로 존재하는 코드** 를 기준으로 작성된 구현 현황 요약이다.
문서와 코드가 불일치 하면 코드가 사실이다. 계획/설계 의도만 남아있는 부분은 `[PLANNED]` 로 명시.

과거 스펙 문서(ADR · phases · plan · SIMPLIFICATION_REPORT · COMPATIBILITY)는 2026-07-28 정리에서 삭제. 필요 시 git 이력에서 복원.

---

## 1. 구현 현황 요약

| Phase | 이름 | 상태 | 실제 산출물 |
|---|---|---|---|
| **0** | 도메인 스키마 | ✅ 구현 | `Ds2.Core` 확장 — SignalId, GlobalAssetId, SemanticId, Base64Url, StandardSubmodels 3종 (AID/AIMC/OperationalData), Kpi/* (5 파일), SequenceLogging 흡수 |
| **1** | AASX 왕복 확장 | ✅ 구현 | `Ds2.Aasx` 확장 — StandardSubmodels Import/Export, KPI ConceptDescription 자동생성, Provenance (Auto/User) |
| **2** | AasHost REST | 🚫 폐기 | Method A(2026-07-27) — REST 삭제, 파일 SSOT (`aid-store/`) 로 대체 |
| **3** | OPC UA 서버 | ✅ 구현 | `Ds2.OpcUa.Server` — EmbeddedUaServer, DsNodeManager (순수 애그리게이터), AasFileScanner (수동 Reload), NamespaceAllocator, DeterministicNodeId |
| **3.5** | Promaker in-process 호스팅 | ✅ 구현 | `OpcUaServerHost`, `SimEngineUaBridge` — Promaker 가 UA 서버를 내장 구동, SimEngine 상태 → UA Variable 반영 |
| **4** | UA 어댑터 | 📋 계획 | 프로젝트 없음. Ds2.Adapter.* 6종은 미착수 |
| **5** | AasxEditor UI 확장 | 🚫 폐기 | Phase 5 skeleton 페이지 5개 삭제 |
| **6** | Collector | 🟡 부분 | `Ds2.Collector` — Envelope, EdgeBuffer (SQLite outbox), DownsampleScheduler, SqliteSinkWriter, DataApi Controllers · **UA subscribe wire-up 대기** |
| **7** | DataService | 🚫 통합 | Collector 로 통합 (Controllers.fs, SeriesIdRegistry.fs 이관) |
| **8** | 파일럿 검증 | 📋 계획 | 미착수 |
| **Tutorial** | `Ds2.AasOpcUa.Tutorial.Web` | 🟡 부분 | Blazor Server · Assets/Diagnostics/Live/Method 페이지, UaLiveClientService, PilotAssetSeeder |

**핵심 통합 테스트**: `Ds2.AasOpcUa.AllTests` — 5 도메인 (Core, Aasx, OpcUa.Server, Collector, Promaker) 을 하나로 실행.

---

## 2. 실제 아키텍처 (v3 · 방법 A)

```
                    aid-store (SSOT · 파일시스템)
                    ├── shells/{aasIdBase64Url}.json
                    ├── submodels/{smIdBase64Url}.json
                    ├── concept-descriptions/{cdIdBase64Url}.json
                    └── packages/{packageIdBase64Url}.aasx
                                 ▲
                    파일 직접 write (AasxEditor · Vendor CI · git · SMB)
                                 │
                                 │ Reload() 수동 호출 → 델타
                                 ▼
                    ┌────────────────────────────┐
                    │  Ds2.OpcUa.Server          │  (순수 애그리게이터 · ADR-001)
                    │  · EmbeddedUaServer        │  :62541/Ds2/OpcUa/Server
                    │  · DsNodeManager           │
                    │  · AasFileScanner          │
                    │  · NamespaceAllocator      │
                    └────────────┬───────────────┘
                                 │ Write / Subscribe
                    ┌────────────┴───────────────┐
                    │                            │
     [ UA Client 어댑터 · 계획 ]        [ Ds2.Collector · 부분 ]
       Ds2.Adapter.* (6종) · 미구현       Envelope · EdgeBuffer
                                          SqliteSinkWriter
                                          DownsampleScheduler
                                          Data REST (/v1/series)

     [ Promaker · 실 구현 ]
       OpcUaServerHost (in-process 서버 구동)
       SimEngineUaBridge (200ms poll · state event push)
```

Promaker 는 Phase 4 UA Client 어댑터 없이 **직접 UA 서버를 인프로세스 구동**하고 SimEngine 상태를 자체 push 한다. 이것이 파일럿 검증용 실제 데이터 경로다.

---

## 3. 구현된 핵심 원칙

1. **OPC UA 서버 = 순수 애그리게이터** — DsNodeManager 는 southbound 드라이버 없음, 외부에서 Write/RaiseAssetEvent
2. **NodeId 결정론적** — namespace `urn:ds:asset:{Base64Url(gaid)}`, string identifier ("Asset" | signalId | "Events" | "Events/RaiseAssetEvent")
3. **AID/AAS 는 파일 SSOT** — REST 계층 제거 (방법 A)
4. **Provenance Auto/User 왕복** — Qualifier `dualsoft:origin`, Extension `dualsoft:auto-suppressed`
5. **KPI Convention-Driven** — `KpiKits.all` 규약이 SoT. 5 엔티티 (System/Work/Call/ArrowWork/UserTag) × 고정 메트릭 → AID + OperationalData + AIMC 자동 생성 (idempotent)
6. **Base64url ID 인코딩** — 파일명 및 (도입 시) REST path segment. OPC UA namespace URI 는 원본 사용
7. **At-least-once + EnvelopeId dedup** — Ds2.Collector.Envelope 스키마에 반영

---

## 4. 실제 프로젝트 트리 (2026-07-28)

```
ds2/
├── Solutions/
│   ├── Core/Ds2.Core/                          ✅ 구현
│   │   ├── SignalId.fs · GlobalAssetId.fs · SemanticId.fs
│   │   ├── Encoding/Base64Url.fs
│   │   ├── StandardSubmodels/
│   │   │   ├── AssetInterfacesDescription.fs
│   │   │   ├── AssetInterfacesMappingConfiguration.fs
│   │   │   ├── OperationalData.fs
│   │   │   ├── Nameplate.fs · TechnicalData.fs · HandoverDocumentation.fs
│   │   ├── SequenceSubmodels/04_Logging.fs      ← SignalPolicy / AcquisitionMode / Iso8601Duration 흡수
│   │   ├── Kpi/
│   │   │   ├── KpiKits.fs · KpiIdentifiers.fs
│   │   │   ├── KpiWalker.fs · KpiAppenders.fs
│   │   │   └── SequenceKpiGenerator.fs
│   │   └── Services/ConceptDescriptionRegistry.fs
│   ├── Convert/Ds2.Aasx/                       ✅ 구현
│   │   ├── AasxSemantics.fs                    ← 표준 서브모델 상수 (AID/AIMC/OperationalData)
│   │   ├── Concepts/Catalog.fs                 ← KPI CD 자동생성
│   │   ├── Export/StandardSubmodels.fs
│   │   └── Import/StandardSubmodels.fs
│   ├── Runtime/
│   │   ├── Ds2.OpcUa.Server/                   ✅ 구현
│   │   │   ├── Server/EmbeddedUaServer.fs · DsNodeManager.fs · DsUaServer.fs
│   │   │   ├── NodeIds/DeterministicNodeId.fs · NamespaceAllocator.fs
│   │   │   ├── AasClient/AasFileScanner.fs
│   │   │   └── Hotpath/AasChangeHandler.fs
│   │   └── Ds2.Collector/                      🟡 부분
│   │       ├── Envelope.fs · EdgeBuffer.fs · UaWriterContract.fs
│   │       ├── DataApi/Controllers.fs · SeriesIdRegistry.fs
│   │       └── Sinks/DownsampleScheduler.fs · SqliteSinkWriter.fs
│   ├── Tests/
│   │   ├── Ds2.Core.Tests/                     ✅
│   │   ├── Ds2.Aasx.Tests/                     ✅ (Phase0Roundtrip · KpiExport · OpcUaScale · SequenceKpi)
│   │   ├── Ds2.OpcUa.Server.Tests/             ✅
│   │   ├── Ds2.Collector.Tests/                ✅
│   │   └── Ds2.AasOpcUa.AllTests/              ✅ 통합 aggregator
│   └── Ds2.sln
├── Apps/
│   ├── Ds2.AasOpcUa.Tutorial.Web/              🟡 Blazor Server
│   │   ├── Program.cs · Components/ (App/Layout/Pages)
│   │   └── Services/UaLiveClientService.cs · PilotAssetSeeder.cs
│   └── Promaker/
│       ├── Promaker.Shared/OpcUaServerSettings.cs
│       └── Promaker/Services/OpcUaServerHost.cs · SimEngineUaBridge.cs
├── deploy/
│   ├── docker-compose.yml
│   ├── docker/Dockerfile.opcua-server · Dockerfile.collector
│   ├── prometheus/prometheus.yml
│   └── scripts/backup-sqlite.sh · verify-journal.sh
└── doc/AasOpcUa/                               ← 본 문서 및 ADR, Phase, plan
```

**존재하지 않는 프로젝트** (문서에는 언급되나 코드 없음): `Ds2.AasHost`, `Ds2.DataService`, `Ds2.Backend.Plc.UaBridge`, `Ds2.Adapter.{Common,OpcUaClient,Modbus,Mqtt,Http,AutoId}`.

---

## 5. 주요 상수 · 계약 (검증 사실)

### 5.1 OPC UA 서버 엔드포인트
- 기본 URL: `opc.tcp://localhost:62541/Ds2/OpcUa/Server` — [OpcUaServerSettings.cs](../../Apps/Promaker/Promaker.Shared/OpcUaServerSettings.cs)
- ApplicationUri: `urn:dualsoft:promaker:opcua` (Promaker in-process 기준)
- 인증서 저장소: `%AppData%\Dualsoft\Promaker\OpcUa\`

### 5.2 AAS 서브모델 idShort · SemanticId
- `AssetInterfacesDescription` → `https://admin-shell.io/idta/AssetInterfacesDescription/1/1/Submodel`
- `AssetInterfacesMappingConfiguration` → `https://admin-shell.io/idta/AssetInterfacesMappingConfiguration/2/0/Submodel`
- `OperationalData` → `https://dualsoftdev.github.io/aas-semantics/sm/OperationalData/1/0`
- CD Base URL: `https://dualsoftdev.github.io/aas-semantics`

### 5.3 KPI SemanticId 규칙
- 실제 코드 (`KpiKits.fs`): `urn:ds:kpi/{EntityShort}/{Metric}/1/0`
- 예) `urn:ds:kpi/System/OEE/1/0`, `urn:ds:kpi/Work/CT/1/0`, `urn:ds:kpi/ArrowWork/AvgLatencyMs/1/0`
- 일반 CD 등록용 사내 네임스페이스는 `{CdBaseUrl}/cd/{path}/{major}/{minor}` (예: `ext.signal-id/1/0`). KPI 만 `urn:ds:kpi/*` 로 별도 유지

### 5.4 NodeId · Namespace
- Namespace URI: `urn:ds:asset:{Base64Url(globalAssetId)}` (자산별)
- NodeId 형식: `ns={index};s={identifier}` where identifier ∈ {"Asset", `{signalId}`, "Events", "Events/RaiseAssetEvent"}
- 등록 상태: `nodeset-state.json` (JSON, atomic rename)

### 5.5 Provenance
- Qualifier `dualsoft:origin` value ∈ {"Auto","User"} — 명시되지 않으면 User (사용자 편집물 보호)
- Submodel Extension `dualsoft:auto-suppressed` — 세미콜론 구분 IdShort 목록 (tombstones)

### 5.6 라이브러리 버전
- OPCFoundation.NetStandard.Opc.Ua.{Core,Server,Configuration,Client} : `1.5.375.457`
- AasCore.Aas3_1: `1.0.0`
- .NET: 9

---

## 6. 문서 구성

- **본 문서**: 구현 현황 · 아키텍처 · 상수 · 원칙.
- **[ENGINEER_SPEC.md](ENGINEER_SPEC.md)**: 엔지니어 온보딩용 실전 스펙.
- 그 외 스펙 문서(ADR · phases · plan · SIMPLIFICATION_REPORT · COMPATIBILITY)는 2026-07-28 삭제. 필요 시 git 이력에서 조회.

---

## Revision History

| REV | Date | Author | Changes |
|---|---|---|---|
| 3.0 | 2026-07-28 | ahn+Claude | 실제 구현 반영 전면 재작성. Phase 0/1/3/3.5/6(부분)/Tutorial 구현 명시. AasHost/Adapter/Editor Phase 5/DataService 는 폐기·미착수 표기 |
| 2.0 | 2026-07-15 | Claude+듀얼소프트 | 완전 재작성. v1 초안 폐기. 10개 ADR 추가 |
