# AAS × OPC UA 통합 아키텍처 — 구현 현황 v5

**리비전**: v5 (2026-08-04) · Agent 통합, CollectionPolicy 및 수집 경로 구현 반영
**대상 저장소**: `ds2` (Solutions + Apps)
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
| **0** | 도메인 스키마 | ✅ 구현 | `Ds2.Core` 확장 — SignalId, GlobalAssetId, SemanticId, Base64Url, StandardSubmodels 3종, SignalPolicy, Kpi/* |
| **1** | AASX 왕복 확장 | ✅ 구현 | StandardSubmodels 및 시스템별 SequenceLogging/SignalPoliciesCollection Import/Export, KPI CD, Provenance |
| **2** | AasHost REST | 🚫 폐기 | Method A(2026-07-27) — REST 삭제, 파일 SSOT (`aid-store/`) 로 대체 |
| **3** | OPC UA 서버 | ✅ 구현 | `Ds2.OpcUa.Server` — AID interaction 자동 투영, EmbeddedUaServer, DsNodeManager, NamespaceAllocator, DeterministicNodeId |
| **3.5** | Agent 인프로세스 호스팅 | ✅ 구현 | 공유 `OpcUaServerHost`/`SimEngineUaBridge`를 Agent가 소유. WPF 데모 경로는 기본 OFF |
| **4** | Southbound | 🟡 부분 | DualSoft `InterfaceXGT` → 기존 LS XGI/XGK gateway 자동 구성 및 UA value bridge 구현. 표준 OPC UA/Modbus/MQTT/HTTP client는 후속 |
| **5** | AasxEditor UI 확장 | 🚫 폐기 | Phase 5 skeleton 페이지 5개 삭제 |
| **6** | Collector | ✅ 구현 | UA browse/subscription, CollectionPolicy별 sampling/publishing/deadband/queue, SQLite retention, batch sink, reconnect, Data API |
| **7** | DataService | 🚫 통합 | Collector 로 통합 (Controllers.fs, SeriesIdRegistry.fs 이관) |
| **8** | 파일럿 검증 | 📋 계획 | 미착수 |
| **Tutorial** | `Ds2.AasOpcUa.Tutorial.Web` | 🟡 부분 | Blazor Server · Assets/Diagnostics/Live/Method 페이지, UaLiveClientService, PilotAssetSeeder |

**핵심 통합 테스트**: `Ds2.AasOpcUa.AllTests` — 5 도메인 (Core, Aasx, OpcUa.Server, Collector, Promaker) 을 하나로 실행.

---

## 2. 실제 아키텍처 (v4 · Agent 소유)

```
 AASX Editor/Promaker ── project.aasx ──▶ Agent watcher
                                           │ import AID + SequenceLogging + DsStore
 Pi5 WriteTags 또는 Agent 직접 XGT scan ──▶ runtime batch
                                           │
                                  ┌────────▼─────────┐
                                  │ Agent            │
                                  │ Embedded UA      │ :62541
                                  │ AID node project │
                                  │ engine/value     │
                                  │ bridges          │
                                  └────────┬─────────┘
                                           │ UA Subscribe
                                  ┌────────▼─────────┐
                                  │ Ds2.Collector    │
                                  │ SQLite + Data API│
                                  └──────────────────┘
```

Agent의 AASX watcher가 변경 시 Backend/engine/UA 주소공간을 함께 재기동한다. 따라서 AID 추가만으로 노드가 자동 생성되고, `InterfaceXGT`는 PLC scan 설정까지 자동 생성된다. SignalPolicy는 각 UA Variable의 HasProperty 메타데이터로 투영되어 Collector가 별도 AASX 복사 없이 적용한다.

---

## 3. 구현된 핵심 원칙

1. **OPC UA 서버 = Agent 내 애그리게이터** — 주소공간은 AID가 만들고 값은 engine/XGT bridge가 주입
2. **NodeId 결정론적** — namespace `urn:ds:asset:{Base64Url(gaid)}`, string identifier ("Asset" | signalId | "Events" | "Events/RaiseAssetEvent")
3. **AID/AAS 는 파일 SSOT** — REST 계층 제거 (방법 A)
4. **Provenance Auto/User 왕복** — Qualifier `dualsoft:origin`, Extension `dualsoft:auto-suppressed`
5. **KPI Convention-Driven** — `KpiKits.all` 규약이 SoT. 5 엔티티 (System/Work/Call/ArrowWork/UserTag) × 고정 메트릭 → AID + OperationalData + AIMC 자동 생성 (idempotent)
6. **Base64url ID 인코딩** — 파일명 및 OPC UA asset namespace URI에 사용
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
│   │   └── Ds2.Collector/                      ✅ 구현
│   │       ├── Envelope.fs · EdgeBuffer.fs · UaSubscriptionService.fs
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
│       ├── Promaker.Shared/OpcUaServerSettings.cs · OpcUaServerHost.cs
│       ├── Promaker.Shared/SimEngineUaBridge.cs · AidUaValueBridge.cs
│       └── Promaker.Agent/MonitoringSupervisor.cs
├── deploy/
│   ├── docker-compose.yml
│   ├── docker/Dockerfile.opcua-server · Dockerfile.collector
│   ├── prometheus/prometheus.yml
│   └── scripts/backup-sqlite.sh · verify-journal.sh
└── doc/AasOpcUa/                               ← 본 문서 및 ADR, Phase, plan
```

**존재하지 않는 프로젝트** (문서에는 언급되나 코드 없음): `Ds2.AasHost`, `Ds2.DataService`, `Ds2.Backend.Plc.UaBridge`, `Ds2.Adapter.{Common,OpcUaClient,Modbus,Mqtt,Http,AutoId}`. XGT는 별도 Adapter 프로젝트 대신 기존 `Ds2.Backend.Plc`에 통합했다.

---

## 5. 주요 상수 · 계약 (검증 사실)

### 5.1 OPC UA 서버 엔드포인트
- 기본 URL: `opc.tcp://localhost:62541/Ds2/OpcUa/Server` — [OpcUaServerSettings.cs](../../Apps/Promaker/Promaker.Shared/OpcUaServerSettings.cs)
- Agent ApplicationUri: `urn:dualsoft:promaker-agent:opcua`; WPF 데모는 기존 Promaker URI를 유지한다.
- Agent 인증서 저장소: Agent 공유 디렉터리의 `opcua/`; WPF 데모는 `%AppData%\Dualsoft\Promaker\OpcUa\`.
- Agent 설정 파일이 없을 때는 anonymous, MessageSecurityMode.None, 미등록 인증서 자동 신뢰가 모두 비활성화된다. WPF 데모 기본값은 로컬 호환을 위해 별도로 열린 상태다.
- AID/Runtime write는 선언된 UA BuiltInType으로 중앙 변환되며 실패 시 값을 Good으로 내보내지 않고 `BadTypeMismatch`와 진단 카운터/경고를 남긴다.
- PLC 단절은 해당 AID 신호를 `BadNoCommunication`, 연결 직후 첫 값 전까지는 `UncertainLastUsableValue`로 표시한다. WPF engine bridge 정지는 Runtime 노드를 `BadOutOfService`로 전환한다.
- WPF 재생 시작과 Agent AASX watcher 재시작은 기존 서버를 정지한 뒤 주소공간을 다시 만들어 모델 변경을 반영한다.

#### 인증서 trust 초기 설정

- Agent store: `{SharedDirectory}/agent/opcua/certs/{own,trusted,rejected}`
- Collector store: `{DS2_COLLECTOR_ROOT}/ua-client/{own,trusted,rejected}`
- 첫 연결에서는 양쪽 `rejected`에 생긴 peer 인증서를 확인한 뒤 각 `trusted`로 승인한다. 운영에서는 `autoAcceptUntrustedCertificates=false`를 유지한다.

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
| 4.1 | 2026-08-04 | Codex+듀얼소프트 | typed write, 품질코드 전환, 주소공간 재로드, Agent/Collector 인증서 보안 기본값, write miss 진단 |
| 4.0 | 2026-08-03 | Codex+듀얼소프트 | Agent UA 소유, AID 노드 자동 투영, InterfaceXGT 자동 scan/value bridge, Collector UA subscribe 반영 |
| 3.0 | 2026-07-28 | ahn+Claude | 실제 구현 반영 전면 재작성. Phase 0/1/3/3.5/6(부분)/Tutorial 구현 명시. AasHost/Adapter/Editor Phase 5/DataService 는 폐기·미착수 표기 |
| 2.0 | 2026-07-15 | Claude+듀얼소프트 | 완전 재작성. v1 초안 폐기. 10개 ADR 추가 |
