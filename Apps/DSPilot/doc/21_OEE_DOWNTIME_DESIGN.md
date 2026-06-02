# 21. OEE · 정지 이벤트 — 데이터 모델 & 수집 설계 (P5)

> 상태: **설계(검증 완료)**. dspilot-ux.html P5(OEE 6지표·정지원인·정지로그·설비순위·AI 인사이트) 를 실제 데이터로 구현하기 위한 신규 모델 설계. 적대적 검증으로 **"자동수집"의 3대 과장(자동 MTTR / Phase1 가용성 / 정지원인 자동수집)을 제거**한 보정본.

## 0. 용어 통일 (mockup 내부 모순 해소)
mockup 에서 "설비"가 순위표=Station(Flow), 정지로그=Device(Work)로 혼용됨. 본 설계는:
- **설비(Station) = Flow** — OEE/가용성/순위의 단위.
- **장치(Device) = Work/Call** — 정지 이벤트의 세부 위치.
OEE 는 Flow 단위 산출, 정지 이벤트는 Flow + (옵션)Device 로 기록.

## 1. 영속 결정 — 별도 `oee.db` (수동입력 자산 보존)
- **`%ProgramData%\DualSoft\Shared\oee.db`** (DSPilot 단독 소유, Shared 디렉터리, 컨벤션 동일).
- 이유: 자동 파생(정지·사이클)은 재구축 가능하나 **작업자가 입력한 정지원인 분류·불량 수량은 사람의 노동** → plc.db `RebuildDatabaseAsync`(파일 삭제)로 날아가면 안 됨. raw 재구축이 목적인 plc.db 와 분리.
- 별도 커넥션이되 컨벤션은 plc.db 와 동일: `CREATE TABLE IF NOT EXISTS`, `id INTEGER PRIMARY KEY AUTOINCREMENT`, datetime = **TEXT ISO8601 UTC** + `SqliteDateTimeHelpers`, 인덱스 `idx_<table>_<cols>`, Dapper 리포지토리(`IOeeRepository`/`OeeRepositoryAdapter`, `IDatabasePathResolver` 주입, scoped).

## 2. 데이터 모델

### 2.1 `oeeDowntimeEvent` (정지/다운타임, 라이프사이클 open→recovered)
```sql
CREATE TABLE IF NOT EXISTS oeeDowntimeEvent (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  systemName   TEXT NOT NULL,           -- 1차 조회 키(자동 onset 은 보통 system 만 매핑됨)
  flowName     TEXT,                    -- 설비(Station)
  deviceName   TEXT,                    -- 장치(Work/Call), 옵션
  startAt      TEXT NOT NULL,           -- ISO8601 UTC
  endAt        TEXT,                    -- NULL = 진행중(open)
  durationMs   INTEGER,                 -- endAt 확정 시 계산
  reasonCode   TEXT,                    -- NULL = 미분류. (equipment_fault/material_wait/operator_wait/tooling/planned_maint/etc)
  category     TEXT,                    -- NULL=미분류 / planned / unplanned  (※ 자동 onset 기본값 NULL — 분류 전 failure 과대계상 방지)
  isFailure    INTEGER NOT NULL DEFAULT 0, -- ※ 기본 0. 분류 확정 시에만 1 (MTBF/MTTR 분모 오염 방지)
  detectSource TEXT NOT NULL,           -- 'nocycle' / 'usertag' / 'manual'
  sourceLogId  INTEGER,                 -- plcTagLog.id (usertag onset dedupe 키)
  note         TEXT,
  createdAt    DATETIME DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_oeeDowntimeEvent_system_time ON oeeDowntimeEvent(systemName, startAt);
CREATE INDEX IF NOT EXISTS idx_oeeDowntimeEvent_flow_time   ON oeeDowntimeEvent(flowName, startAt);
CREATE UNIQUE INDEX IF NOT EXISTS uq_oeeDowntimeEvent_src   ON oeeDowntimeEvent(detectSource, sourceLogId) WHERE sourceLogId IS NOT NULL; -- 멱등 가드
```

### 2.2 `oeeProductionCount` (생산/품질)
```sql
CREATE TABLE IF NOT EXISTS oeeProductionCount (
  bucketDate TEXT NOT NULL,   -- yyyy-MM-dd (로컬일)
  flowName   TEXT NOT NULL,
  shift      TEXT NOT NULL DEFAULT '',
  totalCount INTEGER NOT NULL DEFAULT 0,  -- ※ dspFlowHistory row count 로 자동 채움 가능
  goodCount  INTEGER NOT NULL DEFAULT 0,
  rejectCount INTEGER NOT NULL DEFAULT 0, -- ※ 수동 입력 (불량만)
  source     TEXT NOT NULL DEFAULT 'cycle', -- cycle(자동) / manual / plc
  PRIMARY KEY (bucketDate, flowName, shift)
);
```

### 2.3 `oeeShiftException` (계획생산시간/계획정비 — 가용성 분모)
```sql
CREATE TABLE IF NOT EXISTS oeeShiftException (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  flowName   TEXT,            -- NULL = 전체 라인
  startAt    TEXT NOT NULL,
  endAt      TEXT NOT NULL,
  kind       TEXT NOT NULL,   -- planned_maint(점검) / planned_stop(비가동 계획) / non_production
  note       TEXT
);
```

### 2.4 표준(ideal) 사이클 — `FlowCycleOverride.IdealCycleTimeMs` 확장
- 기존 `AppSettingsModel.FlowCycleOverride`(`[JsonExtensionData]` 보유)에 `IdealCycleTimeMs` 추가. P5 Performance + **P2 표준편차 기준**의 단일 소스(단, §6 한정).
- `AppSettingsService.SaveFlowCycleOverride` 시그니처 변경 → 호출부 전수 확인 필요(컴파일 영향).

### 2.5 `oeeDaily` (집계 캐시, backfill)
- `(bucketDate, flowName)` 복합 PK. 일자별 runtimeMs/downtimeMs/플드된 OEE 구성요소. **증분 backfill(어제까지만)** + plc.db 자정 잡과 **실행 시각 분리**(writer 직렬화 경합 회피).

## 3. OEE 산출 + 입력 매핑
| 지표 | 공식 | 입력 | 비고 |
|---|---|---|---|
| 가용성 Availability | runtime / plannedTime | runtime=달력/시프트−다운타임, planned=시프트−계획정지 | ⚠ **Phase 1 은 planned 데이터 0 → "달력시간(24h) 근사 가용성"만**. 진짜 계획대비는 시프트 설정(Phase 4) 후 |
| 성능 Performance | (idealCT × totalCount) / runtime | idealCT(2.4), totalCount(2.2) | min(1.0) 캡. idealCT 미설정 시 자동추정은 **P5분위수 금지 → 최빈/P50** |
| 품질 Quality | good / total | (2.2) | total 자동, reject 수동 → good=total−reject |
| OEE | A × P × Q | 위 | 한 요소라도 소스 없으면 "산출 불가" 정직 표기 |
| MTBF | Σ runtime / 고장건수 | runtime, `isFailure=1` 건수 | runtime 정의는 가용성과 동일(달력근사 or 계획대비) 명시 |
| MTTR | Σ 고장 durationMs / 고장건수 | `isFailure=1` 이벤트 durationMs | 자동 clear 안 되면 수동 마감 의존(§5) |

## 4. 설비/장치 단위
- 가용성·성능·OEE·순위 = **Flow(Station)**. 품질·생산수 = Flow. MTBF/MTTR·정지로그 = Flow + 옵션 Device.

## 5. 데이터 수집 전략 — ⚠ 검증으로 정정된 핵심

### 5.1 정지 onset 1차 = "무사이클 N초"(자동, 이미 가능) / UserTag = 보조
- **1차**: `dspFlowHistory` 기반 — 마지막 사이클 이후 N초 이상 신규 사이클 없음 → 정지 onset 자동 생성(`detectSource='nocycle'`). 이미 자동 데이터라 추가 PLC 태그 불필요. mockup 정지원인 8건 중 자동 가능한 "시간초과/무가동"을 커버.
- **보조**: UserTag Error → onset. 단 아래 C 제약.

### 5.2 자동 clear(복구) — UserTag 쌍 등록은 불가, 상태머신이 직접 판정
- ⛔ **"같은 Error 주소에 RisingEdge(onset)+FallingEdge(clear) 두 UserTag 등록"은 현재 코드에서 작동 안 함**: `UserTagAlertService.RefreshDefinitionsIfChanged` 가 `byAddr.TryAdd` 로 **주소당 정의 1개만** 등록 → 둘 중 하나만 매칭.
- ✅ 대안: `OeeDowntimeStateMachine` 이 **`plcTagLog` 원천을 직접 읽어 자체 에지검출**(`prev=1 && cur=0` → clear)하거나, "무사이클" onset 은 **사이클 재개 시점**으로 clear. UserTag 는 onset 1개만.

### 5.3 onset 소스 — `userTagAlertLog` 재폴링 금지(이중 파이프라인)
- `UserTagAlertService` 는 이미 **`plcTagLog` 를 750ms 폴링**(2차 알림 테이블 아님). 그 위에 `userTagAlertLog` 를 또 폴링하면 ~1.5s 지연 + 로직 이중화.
- ✅ `UserTagAlertService` 가 fire 확정 시점에 **event/Channel 로 onset/clear 직접 방출** → 상태머신 구독. 또는 상태머신이 `plcTagLog` 직접 폴링(단일화).

### 5.4 자동 vs 수동 명확 구분
| 데이터 | 자동 | 수동 |
|---|---|---|
| 정지 onset(무사이클) | ✅ dspFlowHistory | — |
| 정지 clear | ✅ 사이클 재개 / 태그 에지 | (미감지 시) 수동 마감 |
| 정지원인 분류(reasonCode/category) | ✗ | ✅ **작업자 분류 입력**(금형교체/자재대기/캘리브레이션은 본질적 수동) |
| totalCount | ✅ dspFlowHistory row count | — |
| rejectCount(불량) | ✗ | ✅ 수동 |
| 계획정비/시프트 | ✗ | ✅ 설정 |
| idealCT(표준) | △ 최빈/P50 추정 | ✅ 엔지니어 입력 |

## 6. 표준시간 (P2 와의 관계)
- `FlowCycleOverride.IdealCycleTimeMs` 단일 소스가 P2 편차기준 + P5 성능을 **per-flow 기본값 차원에서만** 공유.
- ⚠ **다품종 라인은 표준이 제품별로 다름** → "P2/P5 동시 해결"은 단일제품 라인 한정. 제품별 표준은 별도 차원(향후). 1차는 per-flow 단일값.

## 7. 신규 수동입력 UX (mockup 에 없음 — 신설 필요)
mockup P5 에는 분류 입력·수동 마감·생산수 입력 UI 가 **전혀 없다**. 자동/수동 하이브리드 성립을 위해 신설:
1. **미분류 큐 + 분류 드롭다운**: 자동 onset(`reasonCode=NULL`) 목록 → 작업자가 원인 선택. 미분류 방치 시 OEE 가 "기타/미분류"로 오염되므로 큐를 눈에 띄게.
2. **진행중 이벤트 수동 마감 버튼**: 자동 clear 미감지 시 "수리 완료(시각 입력)". 미마감 시 durationMs 무한증가 → MTTR 폭주 방지.
3. **생산수 입력**: total 은 사이클 자동, **reject 만 입력**(입력 마찰 1차원으로 축소).

## 8. API (`OeeController`)
| Method | Path | 동작 |
|---|---|---|
| GET | `/api/oee/summary?from&to&flow` | OEE 6지표 + 구성요소(산출 불가 항목은 null + 사유) |
| GET | `/api/oee/downtime?from&to&status&reason` | 정지 이벤트 로그(필터) |
| POST | `/api/oee/downtime/{id}/classify` | `{reasonCode, category}` 분류(PATCH) |
| POST | `/api/oee/downtime/{id}/close` | `{endAt}` 수동 마감 |
| POST | `/api/oee/production` | `{date,flow,shift,reject}` 불량 입력 |
| GET/POST | `/api/oee/shift-exception` | 계획정비/시프트 |
| GET | `/api/oee/ranking?from&to` | 설비별 OEE 순위 |

## 9. 단계별 구현 (가치 주장과 데이터 현실 정합)
- **Phase 0 (선결, 필수)**: **가용 PLC 태그 인벤토리 실측** — 생산 카운터/Run 비트/검사 PASS·FAIL 태그가 현장에 실재하는지 확인(코드 증거 0건). 없으면 품질·자동 생산수는 영구 수동.
- **Phase 1**: oee.db + `oeeDowntimeEvent` + 무사이클 onset 자동 + 정지 로그 UI + 미분류 큐/분류/수동마감 UX. **가용성은 "달력근사"만**(계획시간 없음), 정지원인 도넛(분류된 것만).
- **Phase 2**: totalCount 자동 + reject 수동 → 품질. idealCT(최빈/P50 또는 입력) → 성능. OEE 종합.
- **Phase 3**: MTBF/MTTR(분류 failure 기반), 설비 순위, 일자별 스택(가동/유휴/정지/점검).
- **Phase 4**: 시프트 설정 → **진짜 계획대비 가용성**. 전주대비 변화율.
- **Phase 5**(옵션): AI 인사이트(데이터 충분 후 + 스케줄러/분석 파이프라인 신설).

## 10. 정직성 경계 — 가짜로 보이게 만들지 말 것 (검증이 잡은 3대 과장)
- ⓐ **자동 MTTR**: UserTag 쌍 등록 불가(`byAddr.TryAdd`) → 자동 clear 는 상태머신 직접 에지검출/사이클 재개로만. 그 전엔 수동 마감.
- ⓑ **Phase 1 가용성**: 계획시간 데이터 0 → Phase 1 은 "달력근사"만, 진짜 가용성은 Phase 4.
- ⓒ **정지원인 자동수집**: mockup 8건 중 자동 가능 1~2건뿐, 금형교체/자재대기/캘리브레이션은 본질적 수동 입력. "자동 트리거=UserTag Error" 과장 금지.
- 데이터 미확보 지표는 화면에서 `—` + "데이터 소스 필요"(현재 uptime.html 의 .ds-empty 패턴 유지).

### 관련 파일
`Adapters/DspRepositoryAdapter.cs`(스키마/EnsureColumn 컨벤션), `Repositories/UserTagAlertRepository.cs`(리포지토리 선례), `Services/UserTagAlertService.cs:174-178,219`(byAddr.TryAdd·plcTagLog 폴링), `Services/FlowMetricsService.cs`+`Models/Dsp/DspFlowHistoryEntity.cs`(사이클/IsIdle 자동소스), `Models/AppSettingsModel.cs:85-93`(FlowCycleOverride+IdealCycleTimeMs), `Repositories/PlcRepository.cs`(GetLogsAfterIdAsync), `Infrastructure/SqliteDateTimeHelpers.cs`(datetime 포맷).

## 11. UserTag 자동수집 구현 (`detectSource='usertag'`)

§5.2 의 "✅ 대안: `plcTagLog` 원천 직접 에지검출" 을 **별도 폴러** `OeeUserTagPollerService`(BackgroundService,
15s 폴링, scope→`IOeeRepository`)로 구현. 기존 `OeeDowntimeStateMachine`(`nocycle`)와 **소스 구분으로 공존**.
설정(`OeeSignals.Flows`, `Models/AppSettingsModel.cs`)이 없는 Flow 는 무동작(가짜값 금지, §10).

- **고장 onset/clear**: 고장비트 rising(0→1) → `InsertDowntimeAsync(detectSource='usertag', isFailure=1,
  category='unplanned', deviceName=고장신호주소, sourceLogId=plcTagLog.id)`. falling(1→0) → 해당 open 이벤트
  `CloseDowntimeAsync`. **clear 스캔 윈도를 onset(`startAt`)부터** 잡아 onset '1' 로그가 LAG seed 가 되게 한다
  (경계 누락 방지). 멀티 onset/clear 쌍은 각 이벤트의 윈도가 자기 onset 에서 시작하므로 정확히 페어링.
  신규 메서드: `PlcRepository.FindFallingEdgesAsync`(rising 의 조건 반전), `FindRisingEdgesWithLogIdAsync`
  (멱등 키용 id 동반).
- **정지원인 자동분류**: onset 직전 원인비트 스냅샷(`GetLatestLogsByAddressesBeforeAsync`, 비트당 1쿼리)에서
  1인 것 중 `Priority` 최소 → `ClassifyDowntimeAsync(reasonCode, category)`. `isFailure=(category==unplanned)`
  로 `OeeController.Classify` 와 동일.
- **생산/불량 주입**: 카운터(baseline+구간표본 누적, wrap/reset 보정) 또는 펄스(rising edge 카운트) →
  `UpsertProductionFromPlcAsync(source='plc')`. 로컬일 버킷, shift="".

### 11.1 정직성/정확성 보정 (구현 검증 라운드에서 정정)
- **멱등 키 — `last_insert_rowid()` 금지**: Microsoft.Data.Sqlite 연결풀이 네이티브 핸들을 재사용하면
  `last_insert_rowid` 가 sticky 라 `ON CONFLICT DO NOTHING`(미삽입) 시에도 직전 rowid 를 반환 → "0=중복"
  판별이 깨진다. **`RETURNING id`** 로 교체(미삽입=0행). 안 그러면 overlap/재시작 룩백마다 기존 이벤트가
  엉뚱한 id 로 재분류돼 isFailure 가 뒤집힐 수 있음.
- **카운터 wrap/reset**: `OeeCounterSignal.Width`(16|32) 명시 시에만 해당 모듈러스로 wrap 보정. 미지정/비부합
  음수 delta 는 **리셋**으로 보되 현재값이 0 근처일 때만 cur 계상, 그 외(부분감소/글리치)는 0 — 전체 카운터값을
  통째 phantom 생산으로 주입하지 않음. (폭 추측으로 32-bit 리셋을 16-bit wrap 으로 오판 방지.)
- **생산수 이중계상 방지**: `QueryProductionAsync` 가 plc 행이 있으면 plc 만 합산(plc>manual). plc 는 shift=""
  버킷, manual 은 명명 shift 일 수 있어 shift 분산 합산 시 이중계상되는 것을 소스 단일화로 차단. plc 없으면
  manual 멀티시프트 합산 유지.
- **가짜 100% 품질 방지**: 폴러는 **불량(reject) 신호가 있을 때만** 생산 행을 기록한다. 생산수만 기록하면
  `HasReject` 가 켜져 quality=1.0 을 날조하기 때문(§10). reject 없는 Flow 의 quality 는 `null` + "데이터 소스 필요".

### 11.2 한계 (Phase 0 전제·정밀도)
신호가 현장 PLC 에 주소로 실재 + AASX UserTag 정의 + plcTag 등록돼야 동작. 100ms 폴링·변경분만 기록(<100ms
펄스 누락 → 고속라인은 counter 권장), 타임스탬프=DSPilot 수신시각(+250ms flush 지터), flush 실패 silent drop.
계획생산시간/시프트·idealCT 는 여전히 수동(§5.4) → 상대/추세 OEE 는 자동, 절대 OEE% 는 계획시간 입력 후.
