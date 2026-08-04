# AAS × OPC UA 엔지니어 운영 스펙

기준일: 2026-08-04

코드가 이 문서와 다르면 코드가 우선한다.

## 1. 책임 분리

### Promaker.Agent

- `project.aasx`, `session.json`, `PlcConnection.json`, UA 설정을 감시한다.
- AASX를 `DsStore`로 import하고 AID와 CollectionPolicy를 해석한다.
- EventDrivenEngine, PLC gateway, AID southbound adapter, SignalR Hub, 내장 OPC UA 서버를 한 activation 단위로 관리한다.
- 후보 activation이 실패하면 현재 runtime을 유지한다. 이미 정지한 뒤 실패한 경우 메모리의 이전 계획으로 복구하고, 부팅 복구에는 영속 last-known-good snapshot과 모델 해시를 사용한다.

### Ds2.OpcUa.Server

- OPC Foundation .NET Standard server stack을 사용한다.
- AID interaction을 typed Variable로, CollectionPolicy와 unit을 Property로 투영한다.
- signalId 기반 결정적 NodeId와 자산별 namespace를 사용한다.
- 값 변환 실패, write miss, event contract 위반을 조용히 버리지 않고 품질코드·카운터·로그로 노출한다.

### Ds2.Collector

- Agent UA 서버를 browse하고 보안 subscription을 구성한다.
- notification callback에서 먼저 SQLite outbox에 durable enqueue한다.
- writer가 sample/event DB에 batch 저장한 뒤 outbox를 ack한다.
- series catalog, 시계열, event, 상태 API를 제공한다.

## 2. Activation 순서

```text
파일 변경/업로드
  → 패키지 안전성 검사
  → AASX import
  → PLC/AID/UA/credential preflight
  → staged live-file 교체
  → 기존 runtime 정지
  → Hub/engine/UA 재생성
  → typed bridge 및 adapter 시작
  → last-known-good snapshot 갱신
```

업로드 archive는 허용된 파일명과 수량, 압축 해제 크기를 제한하고 경로 이탈을 거부한다. live 파일 교체 중 오류가 나면 백업본으로 되돌린다.

## 3. AID projection

### 공통

- `signalId`는 UA Variable identifier이자 Collector series identity의 일부다.
- `valueType`은 UA BuiltInType과 southbound decoder를 결정한다.
- `semanticId`, `unit`, CollectionPolicy는 UA metadata로 보존한다.
- 중복 signalId, 잘못된 주소·타입·method·간격은 네트워크 연결 전에 activation을 거부한다.

### InterfaceXGT

- XGI, XGK, XGB 계열을 LS XGT gateway 계약으로 처리한다.
- AID의 endpoint와 interaction 주소로 PLC connection/read plan을 만든다.
- 직접 scan과 delegated `WriteTags` 모두 동일한 typed UA bridge를 거친다.
- PLC 단절은 관련 신호를 `BadNoCommunication`으로 바꾼다.

### OPC UA client

- data change와 event subscription을 지원한다.
- 보안 사용 시 실제 협상 결과가 `SignAndEncrypt`이며 허용 정책인지 검사한다.
- SecurityPolicy None은 `insecure-local`로 표시된 loopback endpoint만 허용한다.
- 인증서 자동 신뢰는 사용하지 않는다.

### Modbus TCP

- coil, discrete input, holding/input register read를 지원한다.
- register word order, scale, offset, 타입 변환을 적용한다.
- 평문 프로토콜이므로 DNS 해석 결과 중 사설·loopback 주소로만 실제 소켓을 연결한다.

### MQTT

- topic filter, QoS, typed payload와 JSON payload path를 지원한다.
- TLS가 기본이며 평문은 `insecure-private` 표시가 있어야 한다.
- 평문 연결은 실제 사설·loopback 주소로 고정한다.
- payload는 4 MiB로 제한한다.

### HTTP

- polling은 GET만 허용하고 redirect를 따르지 않는다.
- POST/PUT은 inbound webhook으로만 사용하며 `authReferenceVault`가 필수다.
- endpoint origin을 벗어나는 href와 예약 route를 거부한다.
- 평문은 `insecure-private` 표시와 실제 사설 주소 연결을 모두 요구한다.
- request/response body는 4 MiB로 제한하고 webhook credential은 hash 후 고정 시간 비교한다.

## 4. UA 값과 품질

- write 전에 선언된 BuiltInType으로 변환한다.
- 변환 실패: `BadTypeMismatch`.
- 초기 값 대기: `BadWaitingForInitialData` 또는 `UncertainLastUsableValue`.
- PLC/adapter 통신 단절: `BadNoCommunication`.
- bridge/runtime 정지: `BadOutOfService` 또는 `BadShutdown`.
- 등록되지 않은 key write: miss counter와 경고 로그.
- 모델 변경: 기존 서버에 노드를 patch하지 않고 서버 전체를 재기동한다.

## 5. CollectionPolicy

CollectionPolicy는 신호를 어떤 주기와 품질로 수집하고 얼마나 보관할지 정하는 계약이다.

| 필드 | 적용 위치 |
|---|---|
| acquisition mode | UA/Collector 수집 방식 metadata |
| sampling interval | UA MonitoredItem sampling |
| publishing interval | subscription grouping |
| absolute deadband | server/client data change filter |
| percent deadband | EURange가 있을 때 적용; 없으면 안전하게 경고 후 미적용 |
| queue size | MonitoredItem queue |
| retention | raw/downsample/event SQLite 정리 |

Collector는 UA Variable의 Property를 읽기 때문에 별도 AASX 사본 없이 정책을 적용한다.

## 6. Collector 저장 계약

- outbox: `pending.db`, WAL, EnvelopeId primary key.
- samples: `telemetry.db`의 typed value columns.
- events: `events.db`, JSON payload와 event semantic id.
- downsample: `signals_1h`, `signals_1d`.
- registry: SQLite 영속 series catalog.
- enqueue 실패는 runtime readiness와 critical log에 반영한다.
- outbox는 event 우선순위를 가지며 전체 용량의 20%를 event용으로 남긴다.

주요 API:

- `GET /v1/series/catalog?afterSeriesId=&pageSize=`
- `GET /v1/series?seriesId=&fromUs=&toUs=&maxPoints=`
- `GET /v1/events?asset=&eventType=&fromUs=&toUs=&beforeTsUs=&beforeId=`
- `GET /v1/info`
- `GET /healthz`
- `GET /readyz`

catalog 기본 page size는 500, 최대 1000이다. public URL을 설정하면 API key가 필수이며 평문 public URL은 거부한다. health/readiness는 내부 예외 내용을 반환하지 않는다.

## 7. 주요 환경 변수

### Agent UA

- 설정 파일: `{DUALSOFT_SHARED_DIR}/agent/opcua-settings.json`
- 기본 endpoint: `opc.tcp://localhost:62541/Ds2/OpcUa/Server`
- 기본값: anonymous off, unsecured endpoint off, auto trust off.

### Agent 파일 전송

- `DS2_AGENT_TRANSFER_BIND_HOST` — 기본 `127.0.0.1`.
- `DS2_AGENT_TRANSFER_ALLOW_PRIVATE_HTTP` — 외부 bind의 명시적 opt-in.
- `DS2_AGENT_TRANSFER_API_KEY_FILE` — 외부 bind 시 필수, 최소 32자와 private file mode.
- `DS2_AGENT_TRANSFER_MAX_UPLOAD_BYTES` — 기본 256 MiB.
- `DS2_AGENT_TRANSFER_REQUESTS_PER_MINUTE` — 기본 600.

외부 bind는 사설·loopback·link-local·IPv6 ULA peer만 허용한다.

### Delegated Hub

- `DS2_AGENT_HUB_BIND_HOST` — delegated 기본 `0.0.0.0`.
- `DS2_AGENT_HUB_SCHEME` — 운영 외부 노출은 `https` 권장.
- `DS2_AGENT_HUB_ALLOW_PRIVATE_HTTP` — 사설망 평문의 명시적 opt-in.
- `DS2_AGENT_DEVICE_CREDENTIALS_PATH` — version 2 장비별 credential hash 파일.

평문 Hub는 실제 사설 peer만 허용한다. 원격 client는 관측 ingress method만 호출할 수 있고 runtime 제어 method는 local-only다.

### Collector

- `DS2_COLLECTOR_ROOT` — DB와 UA client certificate root.
- `DS2_UA_ENDPOINT` — 기본 Agent UA URL.
- `DS2_UA_USE_SECURITY` — 기본 true.
- `DS2_UA_USE_CERTIFICATE_IDENTITY` — 기본 true.
- `DS2_UA_AUTO_ACCEPT_UNTRUSTED` — 기본 false.
- `DS2_UA_PAIR_LOCAL_CERTIFICATES` — 동일 호스트 설치 시 공개 인증서 pairing.
- `DS2_OUTBOX_MAX_ROWS` — 기본 2,000,000.
- `DS2_OUTBOX_MAX_PAYLOAD_BYTES` — 기본 1,073,741,824.
- `DS2_DOWNSAMPLE_ENABLED` — 기본 true.
- `DS2_RETENTION_ENABLED` — 기본 true.
- `DS2_DATA_API_PUBLIC_URL` — 외부 API URL. HTTPS 또는 loopback HTTP만 허용.
- `DS2_DATA_API_KEY_FILE` — 외부 bind 시 필수.

## 8. 인증서 초기 배치

Linux 기본 설치는 Agent와 Collector가 같은 서비스 계정으로 동작하며 공개 인증서를 상호 trusted store에 등록할 수 있다. 일반 배포에서는 다음 저장소를 확인한다.

- Agent: `{DUALSOFT_SHARED_DIR}/agent/opcua/certs`
- Collector: `{DS2_COLLECTOR_ROOT}/ua-client`

자동 미신뢰 허용을 켜서 우회하지 않는다. ApplicationUri와 인증서 주체를 확인한 뒤 공개 인증서만 승인한다.

## 9. 자동 검증과 현장 검증

자동 검증 대상:

- AASX import/export와 AID/CollectionPolicy roundtrip
- 결정적 NodeId, typed write, stale quality, event 제한
- XGT 및 표준 AID config validation
- UA endpoint security negotiation
- Collector durable outbox, dedup, retention, downsample, Data API
- Agent activation, upload safety, last-known-good, credential 회전

현장 E2E 대상:

- 실제 XGI 주소와 데이터 타입/스케일
- 장시간 PLC/네트워크 단절 후 복구
- 인증서와 장비 credential 배포 절차
- AASX 수정 후 node add/change/remove와 MES client 재구독
- Linux 재부팅, disk pressure, service restart 순서

Editor 전용 AID CRUD 화면과 사용자 입력 검증 UX는 별도 범위다.
