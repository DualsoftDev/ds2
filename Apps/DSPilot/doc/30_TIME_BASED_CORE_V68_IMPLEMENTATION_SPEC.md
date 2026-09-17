# 30. 시간 기반 코어 v68 구현 정본 — 2026-09-17

> 상태: **1~4단계 구현 완료(미커밋), 5단계 일부.** 진행 현황은 §14. 원본 스펙은 `doc/OEE _ TEEP Time-Based Core Spec v68 — 쉬운 정본.htm`(2026-09-16).
> 이 문서는 원본 스펙의 서로 충돌하는 조항을 2026-09-17 사용자 결정으로 닫은 구현 정본이다. doc/22·25·26·27·28 의 모델을 **대체**한다.
> 새 DSPilot 은 설치 시 처음부터 수집한다. 기존 DB 마이그레이션은 없다.

## 0. 한 문장

설비가 반복하는 **Cycle 의 시간만** 본다. HEAD 신호로 CT 를 재고, 14일 이동 중앙값 R 과 비교해 사이클을 가동·비가동·비생산 셋 중 하나로 판정하고, 그 CT 들의 합 위에서 A·P·OEE·U·TEEP 를 계산한다. 제품·수량·고장·알람 정보는 쓰지 않는다. 원인을 맞히는 모델이 아니라 리듬을 재는 모델이다.

## 1. 입력

| 항목 | 필수 | 용도 |
|---|---|---|
| HEAD 신호(사이클 경계) | 필수 | CT 측정. 이것이 없으면 이 모델은 적용할 수 없다 |
| work 별 지속시간 | 필수(비가동 판정) | 사이클 안에서 각 work 가 실제 움직인 시간. 그 work 에 속한 call 들의 OUT↑→IN↑ 스팬 합집합 |
| tail 신호 | 선택 | MT/WT 진단만. OEE·TEEP 인자 아님 |
| Q(양품률) | 전역 단일값, 기본 100% | 설정 JSON. 사용자가 바꾸면 모든 조회에 즉시 적용 |
| 심박·접속 상태 | 보조 | 시스템(PLC 연결)별 저장·표시. **계산 인자 아님** |

## 2. 사이클과 CT

- `CT_i = head_{i+1} − head_i`. tail 은 CT 에 관여하지 않는다.
- 행 하나 = `[head_i, head_{i+1})` 구간. 행들이 시간축을 빈틈없이 타일링한다.
- **UNK**: 이전 head 를 알 수 없는 구간(수집 시작 직후, 데이터 공백 직후). 계산 밖, 연표에는 표시.
- **진행 중**: 마지막 head 이후 다음 head 가 없는 열린 사이클. 경과 시간을 표시하되 R 을 적립하지 않고 계산 밖.
- **잘림**: 조회 구간 경계에 걸친 행. 연표에는 잘라 그리되 개수·합산에서 제외. 같은 사이클이 두 구간에 이중 계상되지 않는다.
- 하루 경계에 걸친 사이클은 억지로 둘로 나누지 않는다(원본 §14-9).
- 신호 누락으로 두 사이클이 하나로 보여도 자동 복원하지 않는다(원본 §14-6). 채터링은 수집단 디바운스(기존 SignalDebounce)로 막는다.

## 3. 기준선 R·W

| 기준선 | 정의 | 키 |
|---|---|---|
| R | 최근 14일 완료 CT 의 **이동 중앙값** | flow. **분기가 있으면 분기별** |
| W | 최근 14일 해당 work 지속시간의 이동 중앙값(그 work 가 실제 실행된 사이클만) | work |

- 기준선은 계속 움직인다. 그러나 **사이클이 완료되는 순간 그 행에 R 과 W 를 박제**한다. 이미 판정된 행은 기준선이 움직여도 다시 계산되지 않는다(원본 §16-⑥).
- 최소 표본 K = **10** 완료 사이클(원본 §18 의 K, 종전 표본 게이트와 같은 수). K 미달이면 R 을 박제하지 않고 행은 제외 사유 `기준 없음` 으로 연표에만 표시된다. 처음부터 수집하는 설치본에서 첫 10 사이클이 여기에 해당한다.
- R_최근(참고값)은 따로 두지 않는다. 박제된 R 의 일별 스냅샷을 `baseline` 에 남겨 드리프트를 대조한다.

## 4. 계수 κ — 기본값 있는 사용자 변수

| 계수 | 기본값 | 범위(제안) | 뜻 |
|---|---|---|---|
| κ_비생산 | **10** | 3 ~ 100 | `CT ≥ κ_비생산 × R` 이면 비생산 |
| κ_비가동 | **2.5** | 1.5 ~ 10 | 어느 work 하나라도 `지속시간 > κ_비가동 × W` 이면 비가동 |

- 원본 §18 의 "스펙 버전 고정 상수" 조항은 **폐기**. 두 값은 설정 화면에서 조절한다.
- κ 를 바꾸면 **과거 행도 즉시 재라벨**된다. 상태는 저장하지 않고 조회 시 현재 κ 로 도출하기 때문이다(§5). R·W 는 박제라 기준선 자체는 불변이다.
- 설정 변경은 사용자의 명시적 행위이므로 원본 §16-⑥ "몰래 재계산 금지" 와 충돌하지 않는다.

## 5. 판정 — 행 단위, 우선순위

| 순서 | 상태 | 조건 |
|---|---|---|
| 0 | **제외** | 잘림 · UNK · 진행 중 · 기준 없음(K 미달) |
| 1 | **비생산** | `CT ≥ κ_비생산 × R` (CT 길이 자체) |
| 2 | **비가동** | ∃ work : `지속시간 > κ_비가동 × W_work` (OR 조건) |
| 3 | **가동** | 나머지 |

- 비생산과 비가동이 동시에 성립하면 **비생산**. 아주 긴 사이클은 work 가 멈춰 있었어도 장기 비활성으로 본다(원본 §15 태도).
- **행을 쪼개지 않는다.** 원본 §4 예시의 사이클 내부 시간대 분할은 채택하지 않는다.
- work 조건은 비가동 전용이다. 종전 "고장(MT 초과)" 어휘·판정은 폐기. 비가동 행에는 초과한 work 이름과 배율을 함께 보인다.
- 원본 §4 의 work 별 정의와 §17 의 `κ×R` 정의 중 **work 별 정의**를 채택. `H_비가동 = κR` 표기는 폐기.
- 상태 컬럼은 저장하지 않는다. 행에는 `ct, R, 최악 work, 최악 배율` 을 두고 상태는 조회 시 `f(ct, R, 최악 배율, κ)` 로 도출한다.

## 6. 지표 — CT 합 위에서만

T 는 캘린더가 아니다. **조회 구간 안에 완전히 들어온 유효 행 CT 의 합**이다.

| 지표 | 식 | 비고 |
|---|---|---|
| T | Σ CT = T가동 + T비가동 + T비생산 | 제외 행은 어디에도 없다 |
| A | T가동 ÷ (T가동 + T비가동) | |
| P | Σ R(가동 행마다 박제된 R) ÷ T가동 | **100% 초과 허용, 클립 금지** |
| Q | 전역 설정값 | 기본 1.0 |
| OEE | A × P × Q = Q × ΣR ÷ (T가동 + T비가동) | |
| U | (T가동 + T비가동) ÷ T | |
| TEEP | U × OEE = Q × ΣR ÷ T | 원본의 `TEEP_생산` 항은 **삭제**(T_생산 = T) |
| TO 건수 | 비가동 행 수 | "고장" 이라 부르지 않는다 |
| TTR | 비가동 행 CT 평균 | MTTR 아님 |
| TBF | 비가동 행 시작 시각 간격 평균 | MTBF 아님 |
| MT / WT | tail 있을 때만, `MT = tail − head`, `WT = CT − MT` | 진단 표시만 |

검산(원본 §16): ① `A × P == ΣR ÷ (T가동+T비가동)` ② `TEEP == Q × ΣR ÷ T` ③ 모든 유효 행은 세 상태 중 정확히 하나 ④ 제외 행은 어떤 합에도 없다.

## 7. 심박·접속 상태 — 시스템별, 보조

- 시스템(PLC 연결)별로 **접속 전이**(연결↔단절, 오류, 시각)만 저장한다. 1초 심박 자체는 쌓지 않는다.
- Agent 가 이미 방송하는 `OnPlcConnectionStatus`(어댑터별 전이) 와 `OnScanHeartbeat`(1초) 를 DSPilot 이 구독한다. 종전 60초 자체 샘플(`oeeCommHealthLog`)은 폐기.
- 용도는 두 가지뿐이다. 연표 위 공백 오버레이, 그리고 그 공백을 가로지른 행의 배지("이 사이클 중 통신 단절 n분").
- **계산에 넣지 않는다.** 결과적으로 통신 단절을 가로지른 사이클은 길이 규칙대로 비생산이 된다. 사람이 배지로 대조하는 것이 설계 의도다.
- 종전 "미계측을 A 분모에서 차감" 은 폐기.

## 8. 화면

### 8.1 한줄 연표 — 세 페이지 공용
OEE · TEEP · 가동시간 분석이 **같은 컴포넌트, 같은 API** 를 쓴다. 각 페이지는 그 아래 KPI 만 다르다.

```
GET /api/kpi/timeline?from=&to=&flow=[&branch=]
→ {
    counts:   { run, down, nonprod, excluded: { cut, unknown, inProgress, noBaseline } },
    kpi:      { T, tRun, tDown, tNonProd, sumR, A, P, Q, OEE, U, TEEP, toCount, ttr, tbf },
    segments: [ { s, e, state, cycles, ct, r, worstWork, worstRatio } ],   // 인접 같은 상태 병합
    excluded: [ { s, e, reason } ],
    links:    [ { s, e, system } ]                                           // 접속 공백(보조)
  }
```

- 위: 상태별 개수 칩(가동 n · 비가동 n · 비생산 n · 제외 n). 아래: 구간 비례 스트립. 인접한 같은 상태는 서버에서 병합해 긴 구간도 한 줄에 들어간다.
- 오버레이: 접속 공백 해치, 잘림·UNK·진행 중·기준 없음 표시.
- 세그먼트 툴팁: CT, 박제 R, 초과 work 와 배율, 단절 배지.

### 8.2 가동시간 분석 간트 CT 리본
- 리본 셀 색 = 상태(가동·비가동·비생산·제외).
- 비가동 셀에 초과 work 이름 표기.
- 상단 칩을 상태별 개수로 교체. "비가동·비생산만" 필터를 켜면 해당 CT 로 점프.

### 8.3 설정
- κ_비생산 · κ_비가동 슬라이더(기본값·범위 §4), 변경 즉시 전 구간 재라벨.
- Q 전역 입력(기본 100%).
- 종전 배수 미리보기 API 는 불필요(즉시 반영으로 대체).

## 9. 저장 — DB 1개

파일: `Shared/dspilot.db`. `PRAGMA user_version = 1` 로 시작. **ALTER 체인 금지**, 스키마 변경은 버전 증가 + 명시적 마이그레이션 코드.

| 테이블 | 핵심 컬럼 | 보존 |
|---|---|---|
| cycle | id, flow, branch, startAt(head), endAt(next head), ct, mt, wt, rUsed, worstWork, worstRatio, excludeReason, specVersion | 영구 |
| cycleWork | cycleId, work, durationMs, wUsed | 영구 |
| baseline | flow, branch, work(NULL=R), value, sampleCount, asOfDate | 영구(일별 스냅샷) |
| linkEvent | system, isConnected, error, atUtc, source(agent/pi) | 영구 |
| signalLog | tagId, at, value(현 plcTagLog) | **롤링** 보존 + `auto_vacuum=INCREMENTAL` + 주기 `wal_checkpoint(TRUNCATE)` |
| plc, plcTag, flow, call | AASX 파생 현재 모델 | 로드 시 갱신, 유령 정리 |
| alertLog | 이상·알람 페이지용(현 userTagAlertLog) | 롤링 |

- **기존 DB 삭제**: 기동 시 `Shared/plc.db`, `Shared/oee.db`, 구 위치 `DSPilot/plc.db` 가 있으면 `-wal`·`-shm` 포함 삭제 후 새 DB 생성. 삭제 전 커넥션 풀 비움·인메모리 미러 정지(현 재초기화 코드 순서 재사용). Promaker·Agent 는 SQLite 를 쓰지 않아 영향 없음.
- DB 밖에 남는 것: 설정 JSON(κ·Q 포함), demo-admin.json, project.aasx, PlcConnection.json, 업로드 이미지.
- 63일 인메모리 미러는 `cycle`·`cycleWork`·`linkEvent`·`alertLog` 만 태운다.

## 10. 폐기 목록

| 대상 | 이유 |
|---|---|
| `oee.db` 전체(oeeDowntimeEvent, oeeNonProdDetectionLog, oeeCommHealthLog, oeeProductionCount, oeeShiftException) | 수동 라벨·파생 로그·자체 심박·시프트 모델 전부 스펙 밖 |
| 고장·유지보수·확인 필요·수동 전환(고장↔비생산)·되돌리기 | 원인 어휘 금지. 상태는 규칙에서만 나온다 |
| 비생산 지정 시각대, 패턴 학습기, 시프트·plan-time, 자동 시프트 추론 | 비생산은 CT 길이로만 |
| 표본 게이트·판정 불가, 미귀속, 형제가동 카빙, 진행 중 분모 밖, 미계측 분모 차감 | T = ΣCT 모델에서 카빙 사슬이 사라진다. 제외 행 하나로 통일 |
| WT 축 비생산·MT 축 고장·불인정 행 CT 축(doc/28 두 규칙) | work 별 비가동 + CT 길이 비생산으로 대체 |
| 배수 미리보기 API, ideal-cycle 수동 표준 CT | κ 즉시 반영·R 이동 중앙값으로 대체 |
| 프론트 미호출 엔드포인트 8개(plan-time, shift-summary, shift-exception 3종, ideal-cycle POST, production POST, downtime classify 2종) | 소비자 없음 |
| 죽은 스키마: flowBoundaryChangeLog, userTagAlertDaily, dspCall.next/prev/autoPre/commonPre, plc.projectId/connection, aasxChangeLog 미독 7컬럼 | 처음부터 수집이므로 새 스키마에 만들지 않는다 |
| 조회 시 14일 기준선 재계산·aggKey 서명 캐시·프리컴퓨트 9라우트 | 박제 + 합산 모델에서 불필요. 연표 API 하나에 짧은 TTL 캐시만 |

## 11. 유지 목록

CycleDerivation·CycleBoundaryEdges(head→head), SignalDebounce, 원시 신호 수집(PlcTagLogWriterService → signalLog), 간트·사이클 분석(CallLaneBuilder, CycleAnalysisService), 분기 판별(branch 라벨은 R 키로 계속 사용), 이상·알람, CCTV, 나브, 설정 JSON 체계, 인메모리 미러 뼈대.

## 12. 원본 스펙 충돌 조항 → 결정

| # | 충돌 | 결정 |
|---|---|---|
| ① | κ 고정 상수(§18) vs 사용자 조절(§4) | **사용자 변수**, 기본 10 / 2.5 |
| ② | 사이클 내부 시간대 분할(§4 예시) vs 행 전체 판정(부속 지시) | **행 전체** |
| ③ | H비가동 = work 중앙값(§4) vs κ×R(§17) | **work 별**, OR 조건 |
| ④ | `TEEP_생산 = T/(T_생산+T_비생산)` | **삭제**, TEEP 하나 |
| ⑤ | "시간 구간 안의 CT 기준" vs 정본 R 동결 | 구간은 **합산 대상 선택**. R 은 14일 이동 중앙값, 행에 박제 |
| ⑥ | Q 미입력 = 미산출(§8·§16-④) | **전역 기본 100%** |
| ⑦ | 분기·멀티 PLC 언급 없음 | 분기별 R. 심박은 시스템별 **보조** |
| ⑧ | 비생산·비가동 동시 성립 | **비생산 우선** |
| ⑨ | κ 변경 시 과거 | **자동 재라벨**(상태 미저장, 조회 시 도출) |

## 13. 구현 순서

1. 새 DB 스키마 + 기동 시 구 DB 삭제 + `linkEvent` 구독(`OnPlcConnectionStatus`·`OnScanHeartbeat`). 기존 화면 영향 없음.
2. `cycleWork` 기록기: 사이클 완료 시 work 별 지속시간 산출·저장.
3. 기준선 R·W 이동 중앙값 + 완료 시 박제 + 일별 스냅샷.
4. 연표 API `GET /api/kpi/timeline` + 상태 도출 함수(순수 함수, 단위 테스트: 우선순위·검산 4항).
5. 한줄 연표 컴포넌트 → OEE·TEEP·가동시간 분석 3 페이지 적용, 간트 CT 리본 교체, 설정 κ·Q.
6. 브리핑 메일·Excel 을 새 DTO 로 전환.
7. 구 엔진(OeeControllerBase·OeeMath 판정부·OeeCtStatsService·OeeRepositoryAdapter·프리컴퓨트)·`oee.db` 코드 삭제.

## 14. 구현 현황 (2026-09-17)

| 단계 | 상태 | 산출물 |
|---|---|---|
| 1. 새 DB + 구 DB 정리 + 접속 이력 | **완료** | `Kpi/KpiDb.cs`(스키마 v1·epoch ms·WAL·incremental vacuum), `Kpi/LegacyDbPurge.cs`, `Kpi/LinkEventRecorder.cs`, `Kpi/KpiTime.cs`, `PlcConnectionStatusTracker.AdapterTransitioned`(양방향 전이), `HubSubscriberService` 의 `OnScanHeartbeat` 구독 |
| 2. work 지속시간 기록 | **완료** | `Kpi/WorkSpanMath.cs`(OUT↑→IN↑ 짝짓기·work 합집합·겹침), `Kpi/CycleIngestService.cs` |
| 3. 기준선 R·W + 완료 시 박제 | **완료** | `Kpi/BaselineService.cs`(14일 이동 중앙값·1분 캐시·일별 스냅샷), `cycle.rUsedMs`/`cycleWork.wUsedMs` |
| 4. 연표 API + 판정 순수함수 | **완료** | `Kpi/KpiRules.cs`, `Controllers/KpiController.cs`(`/api/kpi/timeline`·`/api/kpi/cycle/{id}/works`·`/api/kpi/settings`), 테스트 41건 |
| 5. 화면 | **일부** | `wwwroot/app/kpi-timeline.js`(3페이지 공용 연표), 설비효율·생산효율 카드, 간트 리본 판정 오버레이 + '문제만 보기'. **남음**: 설정 화면의 κ·Q 카드 |
| 6. 브리핑·Excel 전환 | 미착수 | |
| 7. 구 엔진·oee.db 삭제 | 미착수 | `Kpi:PurgeLegacyDatabases` 기본값을 true 로 |

### 전환 기간 메모
- 구 OEE 파이프라인은 그대로 동작한다. 새 코어는 자기 DB(`dspilot.db`)에만 쓰고, 사이클 출처로 `dspFlowHistory` 를 읽기 전용으로 본다.
- 그래서 **구 DB 삭제는 아직 꺼져 있다**(`Kpi:PurgeLegacyDatabases` 기본 false). 7단계에서 켠다.
- 설치 직후에는 `cycle` 이 비어 있어 R 이 없다 → 모든 행이 `기준 없음`으로 제외되고, 완료 사이클 10건이 쌓이면 자동으로 판정이 시작된다.
- DI 주의: `KpiDb` 의 public 생성자는 하나뿐이어야 한다(둘이면 DI 가 모호하다고 거부). 테스트는 `KpiDb.ForPath` 를 쓴다.
