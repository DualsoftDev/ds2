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
| 성능 Performance | (idealCT × totalCount) / runtime | idealCT(2.4), totalCount(2.2) | min(1.0) 캡. idealCT 미설정 시 자동기입 = **best-demonstrated p10** (§12 — 구판의 "최빈/P50" 지침 폐기: 평균/중앙을 표준으로 쓰면 성능이 자기 자신과 비교되는 순환정의) |
| 품질 Quality | (total − 입력불량) / total | total=사이클수(자동), reject(2.2) | ~~total 자동, reject 수동 → good=total−reject~~ §12 개정: 분모=기간 사이클수, 불량 미입력 = **100% 가정**(QualitySource="assumed" 명시) |
| OEE | A × P × Q | 위 | 가용성/성능 미산출 시 "산출 불가" 정직 표기. 품질은 §12 가정 정책 |
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
| 정지원인 분류(reasonCode/category) | ✗ (usertag 원인비트 설정 시 △) | ✅ **작업자 분류 입력**(금형교체/자재대기/캘리브레이션은 본질적 수동) |
| totalCount | ✅ dspFlowHistory row count | — |
| rejectCount(불량) | ✗ (usertag 불량신호 설정 시 △) | ✅ 수동 — 미입력 시 §12 품질 100% 가정 |
| 계획정비/시프트 | ✗ | ✅ 설정 |
| idealCT(표준) | ✅ **클린사이클≥30 시 p10 자동 1회 기입(§12)** | ✅ 엔지니어 입력(자동을 항상 우선 덮음) |

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
- **Phase 1**: oee.db + `oeeDowntimeEvent` + 무사이클 onset 자동 + 정지 로그 UI + 미분류 큐/분류/수동마감 UX. **가용성은 "달력근사"만**(계획시간 없음), 정지원인 도넛(분류된 것만 → 2026-06-10 개정: 미분류도 회색 조각으로 포함 — 분류된 것만 그리면 상단 '기간 정지' 합계와 도넛 합계가 어긋나 시간이 증발한 것처럼 보이는 UX 문제가 확인됨).
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
(idealCT 수동 전제는 §12 자동기입으로 완화 — 시프트/계획정지는 여전히 수동.)

## 12. 개정 (2026-06-10) — 자동 OEE: 수동 입력 0 으로 OEE 산출

uptime/OEE 페이지의 OEE 가 idealCT(수동)·reject(수동)에 묶여 사실상 항상 "산출 불가"였던 것을,
**DSPilot 이 이미 수집하는 정보만으로 자동 산출**되도록 개정. 정직성 원칙(§10)은 "산출 불가 숨김 → null"에서
"**가정/자동값은 값 + 출처 명시**"로 확장한다 — 데이터 계층 날조 금지(§11.1)는 그대로 유지.

### 12.1 성능 — idealCT 실측 자동 1회 기입
- `OeeIdealCycleAutoFillService`(BackgroundService, 5분 폴링, 시작 2분 지연): Flow 별로 **idealCT 비어 있음 &&
  클린사이클(IsIdle=0, ct>0) ≥ MinCleanCycles(기본 30)** 이면 best-demonstrated **p10** 을 기입하고
  `FlowCycleOverride.IdealCycleTimeSource="auto"` 스탬프. 기입 후 DatabaseRebuilt 브로드캐스트(열린 페이지 즉시 반영).
- 공식 단일 소스: `OeeCtStatsService`(컨트롤러 ComputeCtStatsAsync 추출) — 추천 테이블(/ideal-cycle/table)과
  자동기입이 같은 구현을 공유해 "추천값=자동기입값" 항상 일치.
- 구판 §3 의 "자동추정 최빈/P50" 지침 폐기: 평균/중앙을 표준으로 쓰면 성능이 자기 자신과 비교돼 순환정의.
  p10(최속 반복가능 CT)이 속도손실을 정직하게 잡는다(ideal-cycle/table 구현과 동일 근거).
- 우선순위/라이프사이클: **수동 입력이 항상 우선**(자동은 빈 칸만 채움, `FillIdealCycleTimesAuto` 가
  Update(원자 load-modify-save) 안에서 재검사 — 레이스 안전). 사용자가 직접 저장하면 출처가 수동(null)으로
  환원되고, 값을 해제(비움)하면 다음 주기에 다시 자동 기입(재보정 경로). 한 번 채워진 값은 재기입하지 않는다
  (1회성 — 값 드리프트로 추세가 흔들리지 않게).
- ⚠ AutoCalibration(디바이스 duration)의 글로벌 CompletedAt 게이트를 **공유하지 않는다** — "idealCT 비어 있음"
  자체가 게이트(기존 설치에서도 동작). 튜닝: `Oee:AutoIdealCycle:{Enabled(기본 true)|MinCleanCycles|Percentile}`
  (IConfiguration "Oee" 섹션 — OeeSignalSettings 주석의 노브 분리 컨벤션).
- UI: uptime/oee 표준CT 편집 테이블에 '자동' 칩(점선) + 자동기입 안내. 다품종 한계(§6)는 자동값도 동일.

### 12.2 품질 — 기본 100% 가정, 불량 입력 시 실측(소급)
- **공식 교체**: quality = clamp((totalCount − Σ입력불량) / totalCount). 분모 = **기간 dspFlowHistory 사이클수**
  (자동·OEE 전반과 동일 분모). production 행의 스냅샷 totalCount 를 분모로 쓰지 않는다 — 일부 날만 불량을
  입력하면 미입력일이 분모에서 통째로 빠져 기간 품질이 급락(예: 주간 700사이클 중 1일 100/불량5 → 구식 95%,
  신식 99.3%)하는 왜곡 제거. "100%에서 시작해 입력된 불량만큼 깎인다"는 운영 모델과 일치.
- 불량 데이터 전무(production 행 0) → quality=1.0 + `QualitySource="assumed"` + 노트 "불량 미입력 — 100% 가정".
  행 ≥1 → `"measured"`. 기간 사이클 0 → 기존대로 null(무의미한 100% 금지).
- §11.1 의 "가짜 100% 품질 방지"는 **데이터 계층 원칙으로 존속**: usertag 폴러의 "불량신호 없으면 행 미기록"
  가드 유지 — 행이 생기면 "measured 100%"로 둔갑하므로 여전히 필요. 본 개정은 **계산 계층의 명시적 가정**이다.
- 소급 보정: OEE 는 on-demand 계산(§8)이라 과거 날짜로 불량 입력(POST /production, date 지정) 후 재조회하면
  해당 기간 OEE 가 즉시 보정된다(별도 재계산 잡 없음).
- UI: 품질 카드 '가정' 칩 + 톤 중립(실측처럼 초록 금지), OEE 카드에 "품질 100% 가정" 주석. 워터폴 품질손실은
  Q=1 이면 0 으로 자연 소멸(특수 분기 없음).

### 12.3 효과/잔여
- 효과: 설치 후 Flow 별 클린사이클 30개부터 **수동 입력 0** 으로 가용성·성능·품질(가정)·OEE·설비 순위 전부
  자동 표시. 불량을 입력하는 현장만 실측 품질로 승격. ranking 도 같은 빌더라 자동 혜택.
- 잔여(미개정): 무사이클 임계 per-flow 연동(ResolveEffectiveCycleRangeMs 재사용 — Phase 3 후보),
  MTBF/MTTR 자동 분류(AbnormalEvent 영속화 선행 — Phase 4 후보), 시프트/계획정지는 여전히 수동(§5.4).

## 12.4 개정 (2026-06-15) — oee-test 목업 이식 + 가용성 폴백 체인 단일화 + 정직성 3계층

`oee-test.html`(P5 v3 검토 목업)을 실제 `/app/uptime.html` + `OeeController`/서비스로 이식하며 가용성 분모를
**단일 폴백 체인**으로 정본화하고, 정지 로그를 **감지·분류·단서 3계층**으로 분리했다(미결정 §2 확정:
가용성=폴백 체인 채택, `/oee` 페이지=폐기).

### A. 가용성 분모 = 계획시간 폴백 체인 (분모 2중화 해소)
- **체인: `UserSet 시프트 ▸ 14일 자동추정 ▸ 달력근사`** (`OeeController.ResolveAvailabilityAsync`). 가동시간(runtime)도
  같은 체인 산출값을 쓰므로 **성능·MTBF 분모가 가용성과 일관**(혼합 분모 방지). `OeeSummaryDto.AvailabilitySource`
  = `shift|auto|calendar` 로 내려 A 카드 칩에 표시.
- **`OeeAutoShiftInferenceService`**(신규, RAM-only 싱글톤 + HostedService 동일 인스턴스): `dspFlowHistory` 14일
  시간대별(로컬 0~23시) 활동 히스토그램 → 가장 깊은 야간 lull(인접 2h 합 최소)을 분할점으로 회전 후 누적분포
  **p5~p95 활동창**(야간 wrap·점심 dip 자연 처리). **DB 영구기입 금지(RAM 캐시)**. ⚠ `recordedAt`(UTC·Z없는
  7자리 소수)는 `substr(recordedAt,1,19)` + `strftime(...,'localtime')` 로 파싱(소수 3자리 초과 strftime 실패 회피) —
  실측 검증 완료(36k 사이클 집계, 활동창 17–13시).
- **`ShiftSettings.UserSet`**(신규 bool, 기본 false): 코드 기본값 08:00/17:00 박제와 "사용자 설정"을 구분. `/oee` 폐기로
  시프트 편집 UI 가 빠졌으므로 **`DashboardController.SaveShift`(시프트 목표 카드)에서만 true 로 마킹** — 평소 false →
  자동추정/달력으로 폴백(사용자 수용). 대시보드 시프트 목표 카드 동작과는 분리(이 플래그를 그쪽이 보지 않음).
- **`/api/oee/plan-time`**(신규): 폴백 체인 활성 단계 + 14일 히스토그램 + 활동창/활동일/계획시간 (목업 계획시간 카드용).
- **`/oee` 폐기**: `oee.html` 삭제, `Program.cs` 에서 `/oee → uptime.html` 매핑(구 북마크 soft redirect). `shift-summary`
  백엔드(`BuildShiftSummaryAsync`)·`oeeShiftException` API 는 잔존(미사용, 헬퍼 공유) — 추후 정리 가능.

### B. 5 KPI + 정직성 배지
- **MTTR 카드 제거**(UI). DTO `Mttr/MttrNote` 는 잔존(미표시). KPI = OEE·A·P·Q·MTBF.
- **MTBF 무고장 배지**: `FailureCount=0` 이면 `max(n,1)` 같은 가짜 수치 금지 → "🟢 무고장" 배지(`OeeMath.ComputeMtbf`
  가 null + NoFault 반환, 클라가 `failureCount===0` 분기). Q 카드는 가정/사용자 칩 + 톤 중립(가정은 초록 금지).

### C. 정지 3계층 — 감지 / 분류 / 단서 (의미 분리)
- **`classifySource` 신규 컬럼**(`oeeDowntimeEvent`, `PRAGMA table_info` 가드로 기존 DB ALTER 마이그레이션):
  `detectSource`(감지=정지 구간 소스: nocycle/usertag/manual)와 **의미 구분**. 값 = `manual`(작업자) / `auto-bit`
  (CauseBit) / `auto-heuristic`(5분/8h) / NULL. **수동 우선**: `AutoClassifyHeuristicAsync` 가 `category IS NULL AND
  classifySource ≠ 'manual'` 가드로만 채워 작업자 분류를 자동이 덮지 않음(이 가드를 휴리스틱보다 먼저 도입).
- **분류 휴리스틱(5분/8h)**: `OeeMath.ClassifyByDuration` — nocycle clear 시 ≥5분 → 고장(equipment_fault/unplanned/
  isFailure=1), ≥8h → 점검(planned_maint/planned). **신규 마감 건만(백필 금지)**. ⚠ "8h→점검(planned)"은 이미 n 에
  잡힌 고장을 사후 제외 → MTBF 추세 점프 가능.
- **단서(clue) — abnormal/usertag 시간겹침 읽기전용 join**(§4 명세 그대로): `Downtime` 엔드포인트가 `userTagAlertLog`
  (plc.db)에서 정지 행 `[startAt, endAt|now]` 와 겹치는 점 이벤트를 붙임(abnormal=`valueType='Abnormal' AND
  matchOp='AbnormalDetect'`, usertag=`logLevel='Error'`; flowName 컬럼이 없어 abnormal 은 `tagAddress` 첫 세그먼트,
  그 외 `systemName` 으로 스코프 매칭). `OeeDowntimeClue{label,src}` 로 내려 **표시 전용 — 건수·길이·MTBF 미반영**.

### D. 표준CT median 임시 폴백 (1회성 1단계 추가)
- `OeeMath.PickAutoIdealCycle`(단일 소스): 클린샘플 ≥30 → **p10 확정("auto")**, ≥5 && <30 → **중앙값 임시
  ("auto-median")**. `FillIdealCycleTimesAuto` 가 빈 칸 기입 + **auto-median → auto(p10) 승급만 덮어쓰기**(수동/확정 보존).
  CT 테이블 출처 칩: 자동 p10 / 중앙값 임시 / 수동.

### E. daily-composition 5분해
- `GetDowntimeBySlotsAsync` 가 슬롯별 정지를 **planned / failure / other / unclassified** 상호배타 분해(가동 = SlotMs −
  4분해 합). uptime 일자별 스택이 가동/고장/기타/점검/미분류 5세그로 표시.

### F. 순수 함수 단일 소스 + 테스트
- `OeeMath.{ComputeOee, ComputeMtbf, PickAutoIdealCycle, ClassifyByDuration}` 추출 — 컨트롤러/상태머신/자동기입이
  공유(이중 산출 방지). `OeeMathTests` 14건 추가(A×P×Q·무고장·median/p10 픽·5분/8h) — 총 22건 통과.

### G. UI 이식 범위 + 스코핑 결정
- uptime.html 에 목업 전 섹션 이식: 자동화 파이프라인 스트립 / 5 KPI / [정지 도넛(출처 칩) | 계획시간 폴백 체인 +
  14일 히스토그램] / 일자별 5세그 스택 / 정지 로그(감지·분류·단서 컬럼 + 범례) / [표준CT(출처 칩) | 설비 순위] /
  인사이트 / 설계 노트. 기존 동작 보존: UserTag 알람 패널·CSV·차트, 디바이스 알람 차단
  모달, custom 기간, SignalR(`DatabaseRebuilt`/`AbnormalDetected`/`UserTagAlertsChanged`).
- **6/15 UI 후속 조정**: ① **품질 직접 입력 = OEE 종합 'Q' 카드 클릭 → 다이얼로그**(설비·일자 + **품질 % 단일 입력**으로
  간략화; 품질%→불량수 환산 후 POST production). 별도 "품질(양품률) 입력" 섹션은 제거(다이얼로그로 흡수, 구 불량수량
  폼·prodForm/submitProduction 삭제). ② **정지 이벤트 로그는 기본 숨김** — 도넛(정지 원인 구성)의 **[로그 보기 및 설정]**
  버튼으로 토글(열 때 해당 섹션으로 스크롤, 미분류 건수 배지). 도넛/순위/인사이트는 로그 표시와 무관하게 항상 산출
  (downtime 는 가시성과 별개로 항상 로드).
- **6/15 UI 후속 2차**: ③ **설비(Flow) 선택기** — 날짜 툴바와 같은 행 오른쪽(.ds-topbar space-between)에 `전체 + Flow버튼`.
  `curFlow` → summary/downtime/daily/plan-time 에 `flow` 파라미터 전달(ranking 은 설비 비교용이라 항상 전체). KPI·도넛·
  계획시간·5세그 스택이 선택 설비로 필터, 헤더/Q다이얼로그 기본값도 연동. ④ **표준CT per-flow 자동/수동 선택** — CT 테이블에
  `[자동][수동]` 모드 토글 추가. 자동=클린사이클 실측 자동기입·관리(수동값 비움→OeeIdealCycleAutoFillService 가 채움),
  수동=직접 값 입력(자동 미덮음). 모드는 **명시적**으로 백엔드에 전달: `IdealCycleRequest.Mode`(manual/auto/null) +
  `SaveFlowIdealCycleTimesBatch` 가 mode 별 처리 — manual 은 **값이 같아도 source=null 로 수동 잠금**(auto-median 자동 승급
  차단), auto 는 수동값만 비워 churn 없이 자동 관리로 환원. 구 "값 변경 시에만 수동" 휴리스틱 대체.
- **6/15 UI 후속 3차 — 품질 = 사용자 직접 설정(전역 단순화)**: 기존 "품질%→불량수 환산→선택 일자 production 저장"
  방식을 폐기하고, **전반 품질(양품률) %를 사용자가 직접 지정하는 전역 오버라이드**로 단순화("이 생산의 전반 불량률은
  대략 N%"). 저장 = `AppSettingsModel.OeeManual.QualityPercent`(신규 관리 섹션, 0~100, null=해제), `POST /api/oee/quality
  {qualityPercent?}`. 우선순위 = **manual(전역) ▸ measured(불량 입력) ▸ assumed(100% 가정)** — `OeeMath.ResolveQuality`
  순수함수 단일 소스(BuildSummary/BuildShiftSummary 공유, 테스트 3건 추가 → 총 25). QualitySource="manual" → KPI 칩 "사용자"+톤.
  다이얼로그(Q 카드 클릭)는 설비·일자 picker 제거, **품질 % 단일 입력 + [가정으로 되돌리기](=null 해제)**. 전역이라 라인·전 설비·
  ranking 품질에 동일 적용(설비 선택과 무관). 불량 카운트(production·PLC 폴러)는 measured 폴백으로 잔존.
- **스코핑 결정**: ① ~~라인/설비 토글 미도입~~ → **6/15 추가됨**. uptime OEE 는 라인 합산 기본(전체)이며 설비 선택 시 per-flow(단, 품질은 전역).
  ② 품질은 전역 manual 오버라이드(위 3차) — per-flow 품질이 필요해지면 FlowCycleOverride 에 per-flow 품질 추가 + 라인 가중평균이 향후 경로.
  ③ by-reason·insight 는 서버 이관 대신 **클라 단일 산출 유지**(open 진행분 규칙이 한 곳). daily-composition 만 서버 확장.
- **6/15 후속 4차 — MTBF '고장' 정의 = 설비고장만**(사용자 선택): 구 규칙 `isFailure=(category=='unplanned')`(계획외 전부=고장 →
  자재대기·작업자대기까지 MTBF 고장으로 과대계상)을 폐기하고 `isFailure = (reasonCode=='equipment_fault')` 로 변경.
  **단일 소스 `OeeMath.IsFailureReason`** — Classify/BulkClassify/CauseBit(poller)/ClassifyByDuration(휴리스틱) 공유(3중 복제 제거).
  **category(계획/계획외)는 그대로** — 자재대기는 여전히 계획외 정지로 가용성(A)·일자스택 '기타'에 반영되지만 MTBF 고장은 아님.
  고장비트 onset(detectSource='usertag', reasonCode NULL)은 감지기반 isFailure=1 유지(마이그레이션이 reasonCode NULL 은 건드리지 않음).
  기존 데이터: `OeeRepositoryAdapter.CreateSchemaAsync` 에 **isFailure 재정렬 마이그레이션**(reasonCode 있는 행만, 멱등) 추가 →
  재시작 시 기존 분류 이벤트가 새 규칙으로 자동 보정. 정의 변경 시 IsFailureReason + 마이그레이션 SQL 두 곳만 맞추면 됨. 테스트 총 33.
