# AAS × OPC UA 런타임 구현 현황

기준일: 2026-08-04

범위: Promaker.Agent, 내장 OPC UA 서버, AID southbound, Ds2.Collector

AASX Editor UI 확장은 이 문서의 구현 범위에서 제외한다.

## 1. 현재 결론

런타임 소프트웨어 경로는 구현되어 있다. `project.aasx`가 바뀌면 Agent가 후보 모델을 먼저 검증하고, 성공한 경우에만 엔진·PLC 연결·AID 어댑터·OPC UA 주소공간을 함께 교체한다. Collector는 Agent의 보안 OPC UA 엔드포인트를 구독해 durable outbox와 typed SQLite 이력으로 저장하고 Data API를 제공한다.

남은 단계는 실제 XGI 장비, 실제 AASX, Linux 배포본을 함께 연결하는 현장 E2E와 그 과정에서 발견되는 장비별 조정이다. 모델 작성용 Editor 화면의 편의 기능은 별도 작업이다.

## 2. 실행 구조

```text
AASX Editor / Promaker
        │ project.aasx 업로드 또는 파일 교체
        ▼
Promaker.Agent
  ├─ 업로드 staging, 패키지·설정 preflight, last-known-good 복구
  ├─ AASX import 및 AID/CollectionPolicy 해석
  ├─ XGI/XGK/XGB 직접 scan 또는 위임 수집 ingress
  ├─ AID OPC UA / Modbus TCP / MQTT / HTTP adapter
  ├─ EventDrivenEngine 및 typed value/quality bridge
  └─ OPC Foundation .NET Standard 내장 UA 서버 :62541
        │ secure OPC UA subscription
        ▼
Ds2.Collector
  ├─ SQLite durable outbox
  ├─ telemetry.db / events.db
  ├─ downsample / CollectionPolicy retention
  └─ Data API :62542
```

Agent가 UA 서버를 소유한다. Promaker WPF의 UA 호스팅 경로는 호환용이며 기본 경로가 아니다.

## 3. 구현 상태

| 영역 | 상태 | 동작 |
|---|---|---|
| Agent 호스팅 | 구현 | Agent가 엔진, Hub, PLC gateway, AID adapter, UA 서버 lifecycle을 소유 |
| AASX 활성화 | 구현 | zip/path traversal·크기 검증, import/config preflight, staged 교체, 실패 시 현재 runtime 유지 또는 last-known-good 복구 |
| AID → UA 노드 | 구현 | AID interaction의 signalId, valueType, semanticId, unit을 typed UA Variable로 자동 투영 |
| 모델 변경 반영 | 구현 | in-place 노드 수정이 아니라 runtime/UA 서버 전체 재기동으로 추가·변경·삭제를 일관되게 반영 |
| LS PLC | 구현 | InterfaceXGT에서 XGI/XGK/XGB 계열 연결·주소를 구성하고 값과 연결 품질을 UA에 반영 |
| 표준 AID adapter | 구현 | OPC UA subscription/event, Modbus TCP polling, MQTT subscription, HTTP GET polling 및 인증된 POST/PUT webhook |
| 값 타입 | 구현 | 선언된 UA BuiltInType으로 변환; 실패는 `BadTypeMismatch`와 경고로 노출 |
| stale 품질 | 구현 | 정지·단절·초기 대기 시 Good을 유지하지 않고 Bad/Uncertain 상태로 전환 |
| UA Event | 구현 | Asset Events notifier와 제한된 JSON wire contract, event type semantic id, source timestamp 지원 |
| CollectionPolicy | 구현 | sampling, publishing, deadband, queue, retention을 UA metadata와 Collector 설정에 반영 |
| Collector | 구현 | 보안 UA 구독, reconnect, durable outbox, dedup, typed sample/event 저장 |
| 이력 처리 | 구현 | raw, 1시간, 1일 downsample과 신호별 retention |
| Data API | 구현 | catalog cursor paging, series/events 조회, info, 최소 정보 health/readiness, 인증·rate limit·크기 제한 |
| AASX Editor UI | 별도 범위 | 기존 AASX 편집 기능은 사용 가능하나 AID 전용 CRUD/검증 UX 확장은 이번 런타임 작업에 포함하지 않음 |

## 4. AASX 변경과 OPC UA CRUD 반응

OPC UA 주소공간을 부분적으로 patch하지 않는다.

1. 새 AASX를 staging 위치에서 검증한다.
2. import, PLC/AID config, UA 설정, 장비 credential을 모두 preflight한다.
3. 검증 성공 시 live 파일을 교체한다.
4. 기존 Hub·engine·AID adapter·UA 서버를 정지한다.
5. 새 모델로 전체 주소공간을 다시 만든다.

따라서 AID interaction 추가는 새 노드 생성, 수정은 타입·metadata·binding 재생성, 삭제는 노드 제거로 반영된다. 검증 실패 시 현재 구동 중인 모델은 유지한다. 부팅 시 live 모델이 손상되었으면 저장된 last-known-good snapshot의 해시를 확인하고 복구를 시도한다.

## 5. 주요 계약

- 기본 UA URL: `opc.tcp://localhost:62541/Ds2/OpcUa/Server`
- Agent ApplicationUri: `urn:dualsoft:promaker-agent:opcua`
- 자산 namespace: `urn:ds:asset:{Base64Url(globalAssetId)}`
- Variable NodeId identifier: AID `signalId`
- Event nodes: `Events`, `Events/RaiseAssetEvent`
- sample/event identity: `EnvelopeId` 기반 at-least-once dedup
- 기본 Data API: `http://127.0.0.1:62542`

## 6. 보안·운영 기본값

- UA anonymous와 SecurityPolicy None은 기본 비활성화한다.
- 서버와 Collector는 `SignAndEncrypt`와 최신 정책만 선택하며 미등록 인증서를 자동 신뢰하지 않는다.
- AID OPC UA도 실제 선택된 endpoint의 mode/policy를 검사한다.
- credential은 파일/Vault reference로 읽고 크기·형식·권한을 검증하며 회전된 값을 다시 읽는다.
- Agent 외부 파일 전송은 명시적 opt-in, API key, 요청/크기/동시성 제한, 실제 사설 peer 주소를 모두 요구한다.
- delegated Hub의 평문 모드는 명시적 opt-in과 장비별 credential 및 실제 사설 peer 주소를 요구한다.
- AID 평문 HTTP/MQTT는 `insecure-private` 표시가 필요하며 실제 연결 주소도 사설망으로 고정한다. Modbus TCP도 사설망 주소만 연결한다.
- outbox 기본 한도는 2,000,000행/1 GiB이고 sample은 80%에서 멈춰 event 공간을 예약한다.

## 7. 검증 경계

자동 테스트는 import/export, AID projection/config, UA 보안·타입·event, Collector outbox/retention/downsample/API, Agent activation/보안 계약을 검증한다. 실제 PLC firmware, 현장 네트워크, 인증서 배포, 장시간 단절·복구는 현장 E2E에서 최종 확인한다.

세부 운용과 환경 변수는 [ENGINEER_SPEC.md](ENGINEER_SPEC.md)를 참고한다.
