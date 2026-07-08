# DSPilot HTTP API 레퍼런스

DSPilot 웹서버가 제공하는 전체 HTTP API 목록.
모든 엔드포인트는 `http://<호스트>:<포트>/api/...` 형태로 호출하며 별도 인증은 없다.
소스 SSOT = [DSPilot/Controllers/](../DSPilot/Controllers/) — 컨트롤러 변경 시 이 문서도 함께 갱신할 것.

> 실시간 갱신은 REST 가 아닌 SignalR 허브로 제공된다(대시보드 갱신, `SensorErrorsChanged` 등).
> Swagger/OpenAPI 는 사용하지 않는다 — 이 문서가 유일한 API 목록이다.

- 작성: 2026-07-08 (컨트롤러 17개 기준)

---

## 목차

1. [대시보드 `/api/dashboard`](#1-대시보드-apidashboard)
2. [Flow `/api/flow`](#2-flow-apiflow)
3. [사이클 분석 `/api/cycle-analysis`](#3-사이클-분석-apicycle-analysis)
4. [가동 추이 `/api/flow-trend`](#4-가동-추이-apiflow-trend)
5. [히트맵 `/api/heatmap`](#5-히트맵-apiheatmap)
6. [OEE·TEEP `/api/oee`](#6-oeeteep-apioee)
7. [이상·알람(사용자 태그) `/api/user-tags`](#7-이상알람사용자-태그-apiuser-tags)
8. [CCTV `/api/cctv`](#8-cctv-apicctv)
9. [배치도 `/api/blueprint`](#9-배치도-apiblueprint)
10. [설정 `/api/settings`](#10-설정-apisettings)
11. [네비게이션 `/api/nav`](#11-네비게이션-apinav)
12. [챗봇 컨텍스트 `/api/chat`](#12-챗봇-컨텍스트-apichat)
13. [Call 테스트 `/api/call-test`](#13-call-테스트-apicall-test)
14. [PLC 디버그 `/api/plc-debug`](#14-plc-디버그-apiplc-debug)
15. [외부 연동 추천 API](#15-외부-연동-추천-api)

---

## 1. 대시보드 `/api/dashboard`

소스: [DashboardController.cs](../DSPilot/Controllers/DashboardController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/dashboard/snapshot` | 대시보드 전체 스냅샷(Flow 상태·상단 KPI 소스) |
| GET | `/api/dashboard/average?days=1` | Flow별 평균 사이클타임 |
| GET | `/api/dashboard/active-alarms?limit=20` | 활성 알람 목록 |
| GET | `/api/dashboard/sensor-errors?limit=100` | 활성 센서에러 목록 → [상세](#센서에러-api-상세) |
| POST | `/api/dashboard/demo-alarm?kind=&flowName=&workName=` | 데모 알람 주입 (kind: 0=센서단선, 1=센서오감지, 2=동작지연, 3=동작과속) |
| DELETE | `/api/dashboard/demo-alarm` | 데모 알람 전체 해제 |
| GET | `/api/dashboard/flows/{flowName}/history?limit=200` | Flow 사이클 이력 |
| GET | `/api/dashboard/flows/{flowName}/works` | Flow의 Work 이름 목록 |
| GET | `/api/dashboard/shift` | 근무시간(시프트) 조회 |
| POST | `/api/dashboard/shift` | 근무시간 저장 |
| GET | `/api/dashboard/exclusions` | 비가동 제외 구간 조회 |
| POST | `/api/dashboard/exclusions` | 비가동 제외 구간 저장 |
| GET | `/api/dashboard/today-cycles` | 금일 가동횟수 |

### 센서에러 API 상세

`GET /api/dashboard/sensor-errors?limit=100` — 외부 조회용으로 설계됨.

- 응답: 활성 센서에러(센서단선 SensorOpen / 센서오감지 SensorShort) 목록, `AbnormalEventDto[]`
- `limit`: 기본 100, 1~100 클램프
- **서버 메모리 전용** — DB(userTagAlertLog) 미기록, 서버 재시작 시 소실
- 디바이스(Call)당 **마지막 발생 1건만** 유지, 해당 디바이스가 재가동(Going)되면 자동 제거
- 실시간 갱신 트리거 = SignalR `SensorErrorsChanged`

## 2. Flow `/api/flow`

소스: [FlowController.cs](../DSPilot/Controllers/FlowController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/flow/{name}` | Flow 상세 |
| POST | `/api/flow/{name}/cycle-override` | 사이클 기준 수동 재정의 저장 |
| GET | `/api/flow/recompute-status` | 재계산 작업 상태 |

## 3. 사이클 분석 `/api/cycle-analysis`

소스: [CycleAnalysisController.cs](../DSPilot/Controllers/CycleAnalysisController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/cycle-analysis/flows` | 분석 가능한 Flow 목록 |
| GET | `/api/cycle-analysis/latest-time` | 최신 데이터 시각 |
| POST | `/api/cycle-analysis/gantt-data` | 간트차트 데이터 |
| POST | `/api/cycle-analysis/export-excel` | Excel 내보내기(단일, 화면 모델 WYSIWYG) |
| POST | `/api/cycle-analysis/export-excel-bulk` | Excel 내보내기(벌크) |

## 4. 가동 추이 `/api/flow-trend`

소스: [FlowTrendController.cs](../DSPilot/Controllers/FlowTrendController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | `/api/flow-trend/export-excel` | 추이 Excel 내보내기 |

## 5. 히트맵 `/api/heatmap`

소스: [HeatmapController.cs](../DSPilot/Controllers/HeatmapController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/heatmap/config` | 히트맵 설정 |
| GET | `/api/heatmap/data` | 동작편차 히트맵 데이터 |
| GET | `/api/heatmap/call-history` | Call 실행 이력 |
| POST | `/api/heatmap/export-excel` | Excel 내보내기 |

## 6. OEE·TEEP `/api/oee`

소스(4개 컨트롤러가 같은 라우트 공유):
[OeeMetricsController.cs](../DSPilot/Controllers/OeeMetricsController.cs) ·
[OeeDowntimeController.cs](../DSPilot/Controllers/OeeDowntimeController.cs) ·
[OeePlannedStopsController.cs](../DSPilot/Controllers/OeePlannedStopsController.cs) ·
[OeeProductionController.cs](../DSPilot/Controllers/OeeProductionController.cs)
(공통 로직 = [OeeControllerBase.cs](../DSPilot/Controllers/OeeControllerBase.cs))

### 지표 (Metrics)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/oee/summary` | OEE 6대 지표 요약 (산출 규격: [22_OEE_CALCULATION_SPEC.md](./22_OEE_CALCULATION_SPEC.md)) |
| GET | `/api/oee/teep` | TEEP(생산효율) |
| GET | `/api/oee/teep/matrix` | TEEP 시간대 매트릭스 |
| GET | `/api/oee/ranking` | Flow별 OEE 랭킹 |
| GET | `/api/oee/shift-summary` | 시프트별 요약 (⚠️ /oee 시프트 페이지 폐기 후 미사용 잔존) |
| GET | `/api/oee/daily` | 일별/시간별 추이 |
| GET | `/api/oee/plan-time` | 계획 가동시간 |
| POST | `/api/oee/ideal-cycle` | 이상 사이클타임 설정(단일) |
| POST | `/api/oee/ideal-cycle/batch` | 이상 사이클타임 설정(일괄) |
| GET | `/api/oee/ideal-cycle/table` | 이상 CT 테이블 |

### 정지 로그 (Downtime)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/oee/downtime` | 정지 로그 목록 |
| POST | `/api/oee/downtime/reclassify` | 비생산↔비가동 재분류 (규칙: 당일 판정 10×CT) |
| POST | `/api/oee/downtime/{id}/classify` | 정지 건별 분류 |
| POST | `/api/oee/downtime/{id}/set-fault` | 고장 지정 |
| POST | `/api/oee/downtime/{id}/close` | 정지 종료 |
| POST | `/api/oee/downtime/bulk-classify` | 일괄 분류 |
| POST | `/api/oee/downtime/bulk-set-fault` | 일괄 고장 지정 |
| POST | `/api/oee/downtime/bulk-close` | 일괄 종료 |

### 계획정지·근무 예외 (PlannedStops)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/oee/planned-stops` | 계획정지(비생산) 설정 조회 |
| PUT | `/api/oee/planned-stops` | 계획정지 설정 저장 |
| GET | `/api/oee/planned-stops/auto-pattern` | 자동 판정 패턴 |
| GET | `/api/oee/planned-stops/actual` | 실제 비생산 구간(타임라인 제외분) |
| GET | `/api/oee/shift-exception` | 근무 예외일 조회 |
| POST | `/api/oee/shift-exception` | 근무 예외일 추가 |
| POST | `/api/oee/shift-exception/{id}/delete` | 근무 예외일 삭제 |

### 생산·품질 (Production)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/oee/output-count` | 생산수량 |
| GET | `/api/oee/output-flows` | 출력 Flow 지정 조회 |
| POST | `/api/oee/output-flows` | 출력 Flow 지정 저장 |
| POST | `/api/oee/production` | 생산실적 입력 |
| POST | `/api/oee/quality` | 수동 품질(양품률) 입력 |
| POST | `/api/oee/export-excel` | OEE Excel 내보내기 |

## 7. 이상·알람(사용자 태그) `/api/user-tags`

소스: [UserTagsController.cs](../DSPilot/Controllers/UserTagsController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/user-tags/snapshot` | 이상·알람 내역 스냅샷(페이징/필터, `?flow=X` 는 자동감지만) |
| GET | `/api/user-tags/error-status` | 현재 Error 태그 상태 |
| GET | `/api/user-tags/definitions` | 태그 정의 목록 |
| GET | `/api/user-tags/alerts` | 알림 목록 |
| GET | `/api/user-tags/excel` | Excel 다운로드 |

## 8. CCTV `/api/cctv`

소스: [CctvController.cs](../DSPilot/Controllers/CctvController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/cctv/config` | 표시 설정 |
| GET | `/api/cctv/status` | MediaMTX 연결 상태 |
| POST | `/api/cctv/sync` | MediaMTX 재동기화 |
| GET | `/api/cctv/settings` | 카메라 설정 조회 |
| POST | `/api/cctv/settings` | 카메라 설정 저장 |
| POST | `/api/cctv/fallback` | 대체 이미지 저장 |
| POST | `/api/cctv/fallback/auto` | 대체 이미지 자동 캡처 저장 |
| POST | `/api/cctv/fallback/delete` | 대체 이미지 삭제 |
| GET | `/api/cctv/overlays?camera=` | 오버레이 목록 |
| POST | `/api/cctv/overlays` | 오버레이 추가/수정 |
| POST | `/api/cctv/overlays/delete` | 오버레이 삭제 |
| GET | `/api/cctv/available-flows` | 오버레이 연결 가능 Flow 목록 |
| GET | `/api/cctv/available-calls` | 오버레이 연결 가능 Call 목록 |
| GET | `/api/cctv/overlay-state?camera=` | 오버레이 실시간 상태(상태색) |
| GET | `/api/cctv/snapshot/{camera}` | 프레임+오버레이 합성 스냅샷 → [상세](#cctv-스냅샷-api-상세) |

### CCTV 스냅샷 API 상세

`GET /api/cctv/snapshot/{camera}?overlay=1&width=` — API 소비자(외부/DSPilot 공용)용.

- 응답: 카메라 현재 프레임에 오버레이(설비 상태색 포함)를 합성한 **JPEG 이미지**
- `{camera}`: 표시명 또는 slug(대소문자 무시), 미매칭 숫자면 등록 순서 index(0부터). 이름 매칭 우선이라 숫자 표시명과 충돌 없음
- `overlay=1`(기본) 합성 / `overlay=0` 원본 프레임만
- `width`: 비율 유지 다운스케일(업스케일 안 함)
- 프레임 소스: MediaMTX RTSP 재게시에서 ffmpeg 원샷 그랩, 실패 시 대체(폴백) 이미지 베이스
- 응답 헤더 `X-Cctv-Source: live | fallback` 으로 라이브/폴백 구분
- 오류: 카메라 미매칭 404, 프레임 획득 실패/디코드 불가 503
- 오버레이 상태색은 `overlay-state` 와 동일 스냅샷 소스 → 대시보드 화면과 일치

예시: `GET /api/cctv/snapshot/0?overlay=1&width=1280`

## 9. 배치도 `/api/blueprint`

소스: [BlueprintController.cs](../DSPilot/Controllers/BlueprintController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/blueprint` | 배치도 조회 |
| POST | `/api/blueprint/placement` | Flow 카드 배치 |
| POST | `/api/blueprint/placement/delete` | Flow 카드 배치 삭제 |
| POST | `/api/blueprint/save` | 레이아웃 저장 |
| POST | `/api/blueprint/replace` | 레이아웃 교체 |
| POST | `/api/blueprint/reset` | 레이아웃 초기화 |
| POST | `/api/blueprint/autofill` | 자동 채움 |
| POST | `/api/blueprint/image` | 도면 이미지 업로드(≤20MB) |

## 10. 설정 `/api/settings`

소스: [SettingsController.cs](../DSPilot/Controllers/SettingsController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/settings` | 설정 전체 조회 |
| POST | `/api/settings/save` | 설정 저장 |
| GET | `/api/settings/plc-scan-interval` | PLC 스캔 주기 조회 |
| POST | `/api/settings/plc-scan-interval` | PLC 스캔 주기 설정 |
| GET | `/api/settings/auto-calibrate` | 자동 보정 토글 조회 |
| POST | `/api/settings/auto-calibrate` | 자동 보정 토글 설정 |
| POST | `/api/settings/auto-calibrate/run` | 자동 보정 즉시 실행 |
| POST | `/api/settings/auto-calibrate/clear-ranges` | 보정 범위 삭제 |
| GET | `/api/settings/calibration-status` | 보정 상태 |
| GET | `/api/settings/aasx-status` | AASX 상태 |
| GET | `/api/settings/aasx-changelog` | AASX 변경 이력 |
| GET | `/api/settings/download-aasx` | AASX 다운로드 |
| POST | `/api/settings/reload` | AASX 재로드 |
| POST | `/api/settings/rebuild-aasx` | AASX 재빌드 |
| POST | `/api/settings/invalidate-caches` | 캐시 무효화 |
| POST | `/api/settings/health-baseline-freeze` | 통신 심박 기준선 고정 |
| POST | `/api/settings/clear-flow-history` | ⚠️ Flow 이력 삭제 (파괴적) |
| POST | `/api/settings/delete-data-before` | ⚠️ 기준일 이전 데이터 삭제 (파괴적) |
| POST | `/api/settings/rebuild-database` | ⚠️ DB 전체 재구성 (파괴적) |
| POST | `/api/settings/restore-flow-defaults` | Flow 기본값 복원 |
| POST | `/api/settings/reset-defaults` | 전체 기본값 복원 |
| POST | `/api/settings/restart-services` | 서비스 재시작 |
| GET | `/api/settings/abnormal-device-filters` | 디바이스별 이상감지 차단 필터 조회 |
| POST | `/api/settings/abnormal-device-filters` | 디바이스별 이상감지 차단 필터 저장 |
| GET | `/api/settings/usertag-filters` | 사용자 태그 표시 필터 조회 |
| POST | `/api/settings/usertag-filters` | 사용자 태그 표시 필터 저장 |

## 11. 네비게이션 `/api/nav`

소스: [NavController.cs](../DSPilot/Controllers/NavController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/nav` | 네비게이션 메뉴 |
| GET | `/api/nav/summary` | 요약 배지(알람 수 등) |

## 12. 챗봇 컨텍스트 `/api/chat`

소스: [ChatContextController.cs](../DSPilot/Controllers/ChatContextController.cs)

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/chat/context?alarmLimit=20` | 챗봇용 현장 컨텍스트(상태+알람 요약) |

## 13. Call 테스트 `/api/call-test`

소스: [CallTestController.cs](../DSPilot/Controllers/CallTestController.cs) — 실측 duration 보정 도구.

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/call-test/flows` | Flow 목록 |
| GET | `/api/call-test/latest-time` | 최신 데이터 시각 |
| POST | `/api/call-test/load` | 구간 데이터 로드 |
| POST | `/api/call-test/cycle-boundaries` | 사이클 경계 계산 |
| POST | `/api/call-test/boundaries` | 경계 계산 |
| POST | `/api/call-test/resolve-overlays` | 오버레이(겹침) 해석 |
| POST | `/api/call-test/apply-durations` | 실측 duration 적용(AASX 역기록) |

## 14. PLC 디버그 `/api/plc-debug`

소스: [PlcDebugController.cs](../DSPilot/Controllers/PlcDebugController.cs) — PLC 태그 로그 뷰어.

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | `/api/plc-debug/upload` | 로그 DB 업로드 |
| POST | `/api/plc-debug/set-db-path` | 로그 DB 경로 지정 |
| GET | `/api/plc-debug/tags` | 태그 목록 |
| GET | `/api/plc-debug/statistics` | 통계 |
| GET | `/api/plc-debug/log-time-range` | 로그 시간 범위 |
| POST | `/api/plc-debug/log-counts` | 태그별 로그 건수 |
| POST | `/api/plc-debug/sampled-logs` | 샘플링 로그(차트용) |

---

## 15. 외부 연동 추천 API

외부 시스템에서 DSPilot 데이터를 소비할 때 대표적으로 쓰는 조회 API:

| 용도 | 엔드포인트 |
|---|---|
| 설비 현재 상태 | `GET /api/dashboard/snapshot` |
| 활성 알람 | `GET /api/dashboard/active-alarms` |
| 센서에러(단선/오감지) | `GET /api/dashboard/sensor-errors` |
| CCTV 캡쳐(오버레이 포함) | `GET /api/cctv/snapshot/{camera}` |
| OEE 요약 | `GET /api/oee/summary` |
| 챗봇/요약용 통합 컨텍스트 | `GET /api/chat/context` |
