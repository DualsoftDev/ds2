# 28. 두 규칙 정지 모델 (고장 = MT · 비생산 = WT) 구현 계획 — 2026-09-11 개정판

> 상태: **구현 완료(2026-09-11, 미커밋)** — 빌드 통과, 단위테스트 OeeMathTests 355건 통과, JS 구문 검사 통과. 구현 메모는 §9. 이 문서는 지시서이자 정본 설명서다.
> 9/10 초판을 9/11 검토(주말·장기 정지, 불인정 행 기준선, 고장 단위, 가동간 공백)로 개정했다. 초판과 달라진 점은 §0.1 에 모았다.
> 출발점은 doc/27(WT/MT 2축, 커밋 f15f69a1). 산출 공식은 doc/22, 행 집합 모델은 doc/26 을 계승한다. 결정 근거는 §7, 미결 항목은 §6 에 기본값과 함께 있다.

## 0. 한 문장 요약

사이클 = 동작(MT) + 대기(WT). **동작이 평소보다 늘어지면 고장, 대기가 평소보다 길면 비생산, 나머지는 전부 정상.**
판정·손실·표시·전환의 단위는 **사이클 행 하나**다 — 행을 쪼개 일부만 고장이라 부르지 않는다. 자동 판정은 길이와 무관하게 승격하지 않고, 주말·장기 정지처럼 사람만 아는 것은 **사용자가 행을 전환**한다. "정지(비가동)" 중간 밴드와 신호 기반 유발자·형제 판별(doc/25)은 폐기한다. 사용자가 움직이는 배수는 고장·비생산 **2개**다.

### 0.1 9/10 초판 대비 변경

| # | 초판 | 개정 | 이유 |
|---|---|---|---|
| 1 | 불인정 행(mt NULL)은 `ct > 중앙MT × 고장배수` | **`ct > 중앙CT × 고장배수`**. 비생산도 `ct ≥ 중앙CT × 비생산배수` | 불인정 행의 ct 는 동작+대기의 합이다. MT 기준선에 대면 WT 비중이 큰 flow(셔틀 WT 87%)에서 정상 길이 사이클도 tail 누락 한 번에 고장이 된다(§7-B) |
| 2 | 고장 적립 = 초과분(mt − 중앙MT), onset = 시작 + 중앙MT, MTTR = 초과분 | **고장 = 행 전체(ct)**, onset = 행 시작, **MTTR → '고장 사이클 평균 시간' = 고장 행 ct 평균** | 행 안에서 고장이 언제 시작됐는지 모른다. 행을 쪼개면 A(행 전체 손실)와 고장 라벨(초과분)이 어긋나 '가동간 공백' 잔여가 생긴다(§7-C) |
| 3 | 정산 유지보수·고장·가동간 공백 3분할 | **공백 삭제.** 정산은 유지보수·고장 2분할. 잔여는 계측 품질의 '미귀속 시간' 진단 지표 | 행이 연속(ct = 시작~다음 시작)이라 공백은 데이터가 아니라 두 계산의 차이였다 |
| 4 | '진행 중' = 마지막 완료 후 경과 > 고장 경계 | **열린 사이클 head↑ 이후 경과 > 고장 경계**, 표시 시작 = head↑ | 마지막 완료 후 경과는 tail 후 대기 중인 상태까지 포함한다 |
| 5 | 주말·장기 정지 = 고장(교정은 지정 시간대·수동 보내기) | 자동 판정은 그대로 고장. **전환 UX 를 1급 기능으로**: 범위 조회 · 시스템 전체 일괄 전환 · 되돌리기 · '확인 필요' 표시 | 끄고 간 것과 고장 조치는 행으로 구분할 수 없다(§7-D). 추론 장치 대신 사람이 한 번에 바꾸는 길을 만든다 |
| 6 | 수동 이벤트가 행을 교집합으로 카빙(forcedNp), 전환은 시간 범위 | **전환 객체는 행.** 사용자는 시간 범위가 아니라 행을 고른다(필터 → 선택 → 일괄). 저장된 라벨을 재도출 행에 다시 붙일 때만 과반 겹침을 내부 조인 허용치로 쓴다 | 사이클 단위 모델에서 시간 범위 겹침은 사용자 규칙이 될 이유가 없다. 교집합 카빙은 행을 쪼개 잔여를 만든다 |

## 1. 판정 규칙 (SSOT)

| 우선 | 행 종류 | 조건 | 결과 | A(가용성) | 고장 건수·MTBF |
|---|---|---|---|---|---|
| ①-a | 완료 행 (mt 있음) | `mt > 고장 경계(MT)` | **고장** | 손실 (행 전체) | 반영 |
| ①-b | 불인정 행 (mt NULL, ct = 시작~다음 시작) | `ct > 고장 경계(CT)` | **고장** | 손실 (행 전체) | 반영 |
| ②-a | 완료 행 | ①이 아니고 `wt ≥ 비생산 경계(WT)` | **비생산** | 분모 밖 (행 전체) | 미반영 |
| ②-b | 불인정 행 | ①이 아니고 `ct ≥ 비생산 경계(CT)` | **비생산** | 분모 밖 (행 전체) | 미반영 |
| ③ | 전부 | 나머지 | **정상** | 분자 | 없음 |

```
고장 경계(MT)   = max(중앙MT × 고장배수, 1,000ms)           OeeMath.ResolveMtFaultBoundaryMs   (기존 유지)
고장 경계(CT)   = max(중앙CT × 고장배수, 1,000ms)           OeeMath.ResolveCtFaultBoundaryMs   (신규)
비생산 경계(WT) = max(중앙WT × 비생산배수, 중앙CT × 10)     OeeMath.ResolveWtNonProdBoundaryMs (정지 경계 인자 제거)
비생산 경계(CT) = max(중앙CT × 비생산배수, 중앙CT × 10)     OeeMath.ResolveCtNonProdBoundaryMs (신규)
```

- **사이클 행이 최소 단위.** 판정·손실 적립·정지 로그 표시·사용자 전환이 모두 행 단위다. 행 내부를 자르는 것은 미계측(심박 공백) 하나뿐이며, 그것은 분류가 아니라 "모르는 시간"이라는 시간 상태다.
- **고장은 길이 무관 비생산으로 승격되지 않는다.** 주말 60시간이 mt 로 걸려도 고장이다 — 교정은 §2.6 전환으로 한다.
- **완료 행은 두 축(MT·WT), 불인정 행은 CT 축 하나.** 불인정 행의 ct 안에는 정상 동작과 정상 대기 분량이 들어 있으므로 사이클 전체 기준선(중앙CT)에 댄다.
- **불인정 행 판정은 MT 기준선 보유 flow 에서만.** mt NULL 이 증거가 되는 이유는 "tail 이 와야 했는데 안 왔다"이다. tail 미정의 flow(mt·wt 항상 NULL)는 고장 판별 불가(0건, 칩에 "고장 판별 불가")이고 ②-b 만 적용한다.
- **기준선은 전부 중앙값**(중앙 MT · 중앙 WT · 중앙 CT), 창은 최근 14일, 키는 부모(물리 설비) flow. 분기는 부모 기준선을 공유.
- **표본 게이트**: 14일 창의 완료 사이클이 **10건 미만**이면 기준선은 만들되 ①②를 적용하지 않는다(전부 정상). 화면 '잠정' 배지 기준 5→10건. 0건 = 종전대로 '—'.

## 2. 파생 규약

### 2.1 고장 시간 · 건수 · MTBF · 고장 사이클 평균 시간
- **고장 시간(벽시계·TEEP 정지 항) = 고장 행 전체 구간** `[행 시작, 행 끝]` ∩ 계측 잔여. 종전 초과분 적립(`ResolveDowntimeAccrualMs`, 행 끝에서 거꾸로 잡던 구간) 폐기.
- **onset = 행 시작**(= rec − ct). 종전 `startMs + 평균CT` / `시작 + 중앙MT` 교체. MTBF 는 onset 간격이므로 상수 이동만 생긴다.
- **MTTR → '고장 사이클 평균 시간' = 고장 행 ct 의 평균.** API 필드명 `mttr` 은 유지(호환), 화면·메일·Excel 라벨만 바꾼다. ⓘ: "고장으로 판정된 사이클의 전체 길이 평균. 평소 대비 초과량은 정지 로그의 각 행 노트에 있다."
- 정지 로그 노트 3종: 고장 MT 초과 `"사이클 27.8분 · 동작 27.3분 (평소 2.4분, +24.9분)"` / 고장 미완료 `"사이클 18.1분 · 완료 신호 없음 (평소 사이클 3.0분)"` / 비생산 대기 초과 `"대기 22.2분 (평소 14초)"`. 대기 노트·정지 배수 문구 삭제.
- 유지보수 이벤트 **과반 겹침** 시 건수·MTBF·평균 시간에서 제외(`IsMaintenanceCovered`) 유지. A 는 그대로 깎인다(라벨·색만).

### 2.2 성능 P 표준치 = 중앙 CT
- `ComputeCyclePerformance(N, thr, Σct)` 의 thr 를 14일 **중앙 CT** 로. `ComputeCtThresholdAsync` 반환 튜플에 `MedianMs` 추가. P·정지 로그·폴백은 중앙값을, 대시보드 등 다른 소비자는 종전 AvgMs 유지(P 경로만 교체).
- 근거: 평균 CT 는 정지 행에 끌려 P 가 100% 에 고정된다(현장 3000 라이브 performance=1). 두 규칙 모델에서 라인 정지는 P 에만 나타나므로 표준치가 대기에 오염되면 손실이 사라진다.
- P 손실 분해(`NormalMtMs/NormalWtMs`)를 설비효율 P 카드에 "손실 중 동작 n% · 대기 m%" 로 노출.

### 2.3 분기(branch)와 불인정 행
- 분기 라벨이 붙은 행 → 그 분기의 고장. 경계는 부모 기준선이라 분기 무관.
- **미분류(branchName NULL) 행이 고장이면 물리 설비의 고장**으로 보고 그 설비의 **모든 분기 스코프에 같은 행으로 표시**한다. 지금은 미분류 행이 분기 집계에서 통째로 빠지므로 신규 작업.

### 2.4 '진행 중'(열린 사이클) 표시 기준
- **열린 사이클의 head↑ 이후 경과 > 고장 경계(MT)** 이면 정지 로그에 '진행 중' 합성 행. 표시 시작 = head↑. tail 이 이미 찍히고 다음 head 를 기다리는 상태는 진행 중 고장이 아니다(열린 대기 — 표시 없음).
- 경과가 비생산 경계(CT) 이상이면 합성 행에도 '확인 필요' 표시(§2.8).

### 2.5 신호(abnormal·usertag)
- 판정 입력에서 **완전 제거**. 정지 로그의 단서(clue) 표시(`AttachCluesAsync`)로만 남긴다. `SignalClassifyEnabled` 삭제.

### 2.6 분류 어휘와 사용자 전환
화면 어휘는 여섯 개로 닫힌다.

| 어휘 | 성격 | 정하는 주체 | A | 고장 통계 | P |
|---|---|---|---|---|---|
| 정상 | 행 분류 | 자동 | 분자 | — | 표본 |
| 고장 | 행 분류 | 자동, 또는 '고장으로' 전환 | 손실 | 반영 | 제외 |
| 고장 · 유지보수 | 고장 행의 꼬리표 | 사용자 체크 | 손실 그대로 | **제외** | 제외 |
| 비생산 | 행 분류 | 자동, 또는 '비생산으로' 전환 | 분모 밖 | — | 제외 |
| 미계측 | 시간 상태 | 심박 | 분모 밖 | — | — |
| 진행 중 | 시간 상태 | 열린 사이클 | 분모 밖 | — | — |

전환 규칙:
- **행 단위.** 전환 대상은 정상이 아닌 행(고장↔비생산)과 고장 행의 유지보수 꼬리표. 정상 행은 전환 대상이 아니다.
- **사용자는 행을 고른다, 시간 범위를 고르지 않는다.** 정지 로그를 날짜·시간대·길이·확인 필요·flow/시스템으로 필터한 뒤 행을 선택(전체 선택 포함)해 일괄 전환한다. 시간 범위는 필터일 뿐이고 전환의 객체는 행이다. 범위 경계가 행 한가운데를 지나는 문제는 사용자에게 생기지 않는다 — 목록에 뜬 행을 넣을지 뺄지 사람이 정한다.
- **저장은 행 단위 수동 라벨**(기존 classify/bulk-classify 이벤트 = 그 행의 flow·구간). 재도출로 행 경계가 조금 움직여도 같은 행에 다시 붙도록 **저장 구간과 행이 과반(≥ 50%) 겹치면 같은 행**으로 조인한다(`OeeMath.IsMajorityCovered`, 유지보수 이벤트에 이미 쓰는 규칙). 내부 허용치이며 사용자 규칙이 아니다. 종전 "교집합 카빙(forcedNp)" 폐지 — 라벨은 행 전체에 붙는다.
- **비생산 지정 시각대(일일 창)는 시간 상태다.** 분모(생산가능)에서 **구간으로** 빠지는 벽시계 규약 유지. 행 분류는 현행 "행 시작 시각이 창 안이면 그 행은 비생산" 유지 — 단 그렇게 넘긴 행은 반드시 행 전체를 비생산 구간으로 등록한다(미귀속 원인 제거, §2.7). 창 안에서 시작해 창 밖으로 길게 이어진 고장이 비생산으로 들어가면 사용자가 '고장으로' 전환한다.
- 우선순위: 사용자 라벨 > 비생산 지정 시각대 > 자동 판정. '고장으로' 라벨된 행은 어떤 규칙으로도 비생산이 되지 않는다.

### 2.7 벽시계 분해(A · TEEP) — 가동간 공백 삭제
- 카빙 사슬: **미계측 ▸ 비생산 ▸ 형제가동(분기) ▸ 진행 중 ▸ 비가동.** 비가동 = 유지보수 + 고장. 고장 = 비가동 ∩ 고장 행 전체 구간.
- **가동간 공백 = 비가동 − 유지보수 − 고장은 정의상 0**이어야 한다(행이 연속 + 고장 행 전체 적립). 0 이 아니면 데이터 결함(행 겹침·누락, 심박이 못 덮은 엔진 재시작 조각). `UnattributedWallMs` 로 이름을 바꿔 계측 품질(`measurement-quality`)에 flow 별 진단 항목으로 올리고, 정산 막대·도넛·툴팁에서는 지운다.
- TEEP: 정지 항 = 고장 행 전체(종전 초과분만 → 잔여가 줄어든다). TEEP 값(가동 ÷ 캘린더)은 불변. TEEP 카드 ⓘ 에 "비생산도 달력 손실에 포함 — 전환해도 TEEP 는 변하지 않는다" 한 줄.

### 2.8 장기 정지 · 주말 방침
- **자동 판정은 고장이다.** 끄고 간 것과 고장 조치는 행으로 구분할 수 없고(§7-D), 무신호·달력·형제 flow 어느 정보도 완전하지 않다. 추론하지 않고 측정한 대로 두되, 사람이 한 번에 바꿀 수 있게 한다.
- **'확인 필요'(needsReview)** = 고장 행 ∧ `ct ≥ 비생산 경계(CT)`(= max(중앙CT × 비생산배수, 중앙CT × 10)) ∧ 수동 라벨 없음. 조회 시 계산(저장 안 함). 뜻: **"길이만 보면 비생산 기준을 넘는 고장"** — 이 행이 대기로 서 있었다면 비생산이 됐을 길이다. 새 상수·슬라이더 없이 사용자의 비생산 배수를 그대로 쓴다(§6-E 결정). ⓘ 문구: "길이만 보면 비생산 기준을 넘는 고장입니다. 설비를 끄고 간 정지가 아닌지 확인하세요. 비생산으로 전환하거나 고장으로 확정하면 사라집니다." 해소 = '비생산으로' 전환 또는 '고장으로' 확정(둘 다 수동 라벨이 생겨 배지 소멸). 별도 '확인함' 버튼 없음. 노출: 요약 DTO `reviewPendingCount/Ms`, 일일 브리핑 메일 **상단** "확인 필요 장기 정지 n건 · m시간", 대시보드 A 카드 칩, 정지 로그 필터, '진행 중' 합성 행(§2.4).
- **브리핑 메일 하루치 규약(§6-H 결정)**: 전일 00:00~24:00 로 클립한 하루치를 보여 준다(현행). 하루 경계를 넘는 고장 행 — 전일부터 이어짐 / 다음 날로 이어짐 / 아직 열린 '진행 중' — 은 클립된 길이와 함께 **특이사항 줄**로 따로 알린다(예: "#131 고장 9/5 17:02 부터 이어짐 · 이날 24.0h · 계속 진행 중"). 발송 지연·재발송은 하지 않는다.
- **전환 UX(1급 기능)**
  1. 정지 로그 필터: 기간 · 시간대(예 18:00~08:00) · 최소 길이 · 확인 필요만 · flow/시스템.
  2. 일괄 선택 → 비생산으로 / 고장으로 / 유지보수 체크 (기존 bulk API 재사용).
  3. **필터 결과 전체 선택 → 일괄 전환**이 주말 처리의 기본 동작: 필터 `9/5~9/7 · 고장 · 시스템 전체` → 전체 선택 → '비생산으로'. 행마다 수동 라벨 1건(기존 bulk-classify, 합성 행 확정 포함). 시간 범위 이벤트(flow NULL 시스템 전체 구간)는 만들지 않는다 — 전환 객체는 행이다.
  4. 되돌리기: 행의 수동 라벨 삭제 → 자동 판정 복귀. 정지 로그 행에 "수동 · 되돌리기" 표시.
  5. 반복 승격(매주 같은 요일·시간대 → 지정 창 생성)은 **보류**(§6-F): 공휴일·불규칙 비생산에 맞지 않고 자동화가 우선이다. 주말은 당분간 매주 범위 전환 1건으로 처리하며, 브리핑 메일의 '확인 필요' 블록이 그 트리거다. 자동화 경로는 §6-J.
- 무신호 힌트(후속, 이번 범위 밖): 정지 행 구간에 시스템 BOOL 태그 변화가 0건이면 "무신호" 배지 → 전환이 확인 클릭 한 번. 판정에는 쓰지 않는다.

## 3. 설정 · API

| 항목 | 결정 |
|---|---|
| `OeeManual.FaultMtMultiplier` | 유지. **기본 2.5 → 5.0, 범위 1~10 → 1~20**(현장 3000 은 10 사용 중 — 상한 확장을 기본값 변경보다 먼저 배포) |
| `OeeManual.NonProdWtMultiplier` | 유지. 기본 30, 범위 5~300 |
| `OeeManual.IdleWtMultiplier`, `SignalClassifyEnabled` | **삭제**(ExtensionData 로 구 키 무해 보존) |
| `OeeMath.WtStopFloorCtMultiples`, `IdleWtMultiplierDefault`, `ResolveWtStopBoundaryMs`, `ResolveDowntimeAccrualMs` | 삭제 |
| `OeeMath.WtNonProdFloorCtMultiples`(10), `FaultMtBoundaryFloorMs`(1s) | 유지 |
| `OeeMath.MinBaselineSamples`(신규) | 10 |
| `OeeMath.MajorityCoverRatio`(신규) | 0.5 — 저장된 수동 라벨·유지보수 이벤트를 재도출 행에 조인하는 내부 허용치(사용자 규칙 아님) |
| `PlannedStopWindow.Weekdays` | **보류**(§6-F) — 이번 범위에서 만들지 않음 |
| `GET/PUT /api/oee/ct-multipliers` | 경로 유지. 필드 `faultMtMultiplier / nonProdWtMultiplier` + flow 별 `medianMtMs / medianWtMs / medianCtMs / avgCtMs / sampleCount / hasMtBaseline` + 하한 상수. `idleWtMultiplier` 제거 |
| `GET /api/oee/ct-multipliers/preview` | `idle` 제거. 응답에 축별 건수 `faultMt / faultCt / nonProdWt / nonProdCt` |
| `GET /api/oee/planned-stops` | `idleWtMultiplier` 제거 |
| `POST /api/oee/downtime/reclassify`, `bulk-classify` | 유지(행 id 기반, 합성 행 확정 경로 포함). 응답에 영향 행 수 |
| `DELETE /api/oee/downtime/manual/{id}`(신규) | 되돌리기 |
| `GET /api/oee/summary`, `/downtime` | `reviewPendingCount / reviewPendingMs`, 행별 `needsReview / manualSource`. `/downtime` 필터 파라미터 `minDurationMs`, `todFrom/todTo`(시간대), `needsReview` 추가 |
| `GET /api/oee/measurement-quality` | flow 별 `unattributedWallMs` |
| 집계 캐시 키 | **v33**, 서명에서 idle 배수 제거 |

## 4. 코드 작업 목록 (파일별)

**[OeeMath.cs](../DSPilot/Services/OeeMath.cs)**
- `CycleClass` → `Normal / Fault / NonProduction` 3분류. `ClassifyCycle(mt, ct, wt, mtFaultMs, ctFaultMs, wtNonProdMs, ctNonProdMs, sampleCount)` — 표본 게이트 포함, 완료 행은 MT·WT 축, 불인정 행은 CT 축.
- `ResolveCtFaultBoundaryMs`, `ResolveCtNonProdBoundaryMs` 신규. `ResolveWtNonProdBoundaryMs` 정지 경계 인자 제거.
- `IsMajorityCovered(rowMs, overlapMs)` 신규(`IsMaintenanceCovered` 는 이를 호출).
- 삭제: `ResolveDowntimeAccrualMs`, `ClassifyStopWindow`, `StopClass`, `IsLongStopNonProduction`, `ResolveWtStopBoundaryMs`, 정지 배수 상수. `IsNonProductionLength` 유지.
- `ComputeCyclePerformance` 주석: 표준치 = 중앙 CT. 고장 사이클 평균 시간 순수함수 `ComputeFaultCycleMeanMs(IEnumerable<double> faultRowCtMs)`.

**[OeeCtStatsService.cs](../DSPilot/Services/OeeCtStatsService.cs)**
- `ComputeCtThresholdAsync` 튜플에 `MedianMs`(비가중). `ComputeMtThresholdAsync` → `(MedianMs, Sample)`. `ComputeWtBaselineAsync` 표본 수를 게이트에 사용.

**[OeeControllerBase.cs](../DSPilot/Controllers/OeeControllerBase.cs)**
- `DtCondSql` = `ct > 0 AND ((mt IS NOT NULL AND mt > @MtThr) OR (mt IS NULL AND ct > @CtFaultThr) OR (mt IS NOT NULL AND wt >= @NpThr) OR (mt IS NULL AND ct >= @CtNpThr))`. MT 기준 없는 flow 는 `@MtThr/@CtFaultThr` 를 `9e15` 로 바인딩(0 금지 규약). 표본 게이트 미달 flow 는 dtCond 자체를 건너뛴다.
- 삭제: 신호 로드 블록(userTagAlertLog, abnByFlow/usertagEvents, LookbackMsFor), MT 과주행·자세 사전수집(mtOverrunByFlow/postureByFlow), `HasOwnSignal/LineHasCulprit/LineHasMtOverrun/LineHasMidCycleStall/OwnMidCycleStall/LineHasUnresolvedUsertag`, `signalRulesActive`, `accrualStartMs` 계산.
- 행 루프(정상 아닌 행): ⓐ 수동 라벨 조인(저장 구간과 과반 겹침) → 고장 확정 / 비생산 확정 ⓑ 비생산 지정 시각대(행 시작 시각, 현행) → 비생산 ⓒ 자동 판정. 고장 → `idle 구간 = [rec−ct, rec] ∩ 계측`, onset = rec−ct, repair = ct, `downtimeCycles`, needsReview. 비생산 → 행 전체 `AddNonProdFor` + 감지 로그(`idle-cycle`). **비생산으로 넘긴 행은 반드시 `AddNonProdFor` 로 구간 등록**(종전 `plannedCtMs += …; continue;` 가 공백의 한 원인).
- 삭제: `WaitSlack/WaitNonProd/waitSlackCtMs/AddSlackFor`, `toNonProdIv` 교집합 카빙(→ 행 단위 조인), `CycleAgg` 의 `WaitScoped / SlackScoped / WaitWallMs / WaitSlackWallMs / EventSlackWallMs`, `DowntimeCycles` 튜플의 `Wait`.
- 패스 2: 대기 벽시계 제거. 잔여 = `UnattributedWallMs`(정산 미노출, 계측 품질 노출). 계측 품질 `ThresholdMs` = 고장 경계(MT).
- 정지 로그 빌더(`BuildDowntimeRowsAsync` 부근): 고장 구간 = 행 구간, 노트 3종(§2.1), '진행 중' = head↑ 기준(§2.4), 미분류 고장 행의 분기 스코프 표시(§2.3), `needsReview / manualSource` 필드.
- `BuildAggKey` v33.

**[OeePlannedStopsController.cs](../DSPilot/Controllers/OeePlannedStopsController.cs) · [OeeModels.cs](../DSPilot/Models/Oee/OeeModels.cs) · [AppSettingsModel.cs](../DSPilot/Models/AppSettingsModel.cs) · [AppSettingsService.cs](../DSPilot/Services/AppSettingsService.cs)**
- §3 대로. `ResolveWtMultipliers()` → `ResolveNonProdWtMultiplier()`. PUT 검증 idle<nonProd 삭제. `SaveStopMultipliers(nonProd, fault)`.

**[OeeDowntimeController.cs](../DSPilot/Controllers/OeeDowntimeController.cs)**
- `GET downtime` 필터 파라미터(`minDurationMs`, `todFrom/todTo`, `needsReview`). `DELETE manual/{id}` 신규(되돌리기). bulk-classify 응답에 영향 행 수. 행별 `needsReview / manualSource`.

**[OeeNonProdPatternService.cs](../DSPilot/Services/OeeNonProdPatternService.cs)**: 문턱 = 새 비생산 경계(WT/CT 축 분기). 그 외 무변경.

**[OeeMetricsController.cs](../DSPilot/Controllers/OeeMetricsController.cs) · TEEP · 브리핑 메일 · Excel**: 대기 필드 소비처 제거(`grep -rn "waitWall\|WaitSlack\|isWait\|SlackScoped\|eventSlack"`). MTTR 라벨 → 고장 사이클 평균 시간. 브리핑 메일: 전일 00:00~24:00 하루치 클립 유지, 상단 '확인 필요' 블록 + 하루 경계를 넘는 고장 행·진행 중 특이사항 줄(§2.8). 일별 추이 DTO 의 '점검' → 유지보수 명칭 통일, 기타·미분류는 수동·유산 이벤트 전용임을 주석.

**UI** — [uptime-oee.html](../DSPilot/wwwroot/app/uptime-oee.html) `#ct-mult-section`, [uptime-workspace.js](../DSPilot/wwwroot/app/uptime-workspace.js) `cm*`, [uptime-workspace.css](../DSPilot/wwwroot/css/uptime-workspace.css), 대시보드 KPI
- 사이클 해부도: 판정 순서 **3단계**(①동작 초과=고장 ②대기 초과=비생산 ③정상). 레인 2개·경계 각 1개. 슬라이더 2개.
- flow 칩: `동작 2.3분 · 대기 14초 · 표본 501 | 고장 > 22분 · 비생산 ≥ 29분`. 불인정 행 기준(`미완료 > n분`)과 CT 축 비생산은 ⓘ 툴팁. 표본 10건 미만 '판정 보류', WT 기준선 없음 = 점선 + "고장 판별 불가". 미리보기 카운트는 축별 분리 표기.
- 정산 막대 유지보수·고장 **2분할**. 도넛/TEEP 대기 세그먼트·툴팁 제거. TEEP ⓘ 문구(§2.7).
- 정지 로그: '대기' 구분·탭 제거. 고장 행 = 행 구간 + 노트(§2.1). 필터(기간·시간대·최소 길이·확인 필요·flow/시스템) + **필터 결과 전체 선택** + 일괄 전환(비생산으로/고장으로/유지보수, 선택 행 수·합계 시간 표시). 되돌리기. 수동 표시.
- P 카드 손실 분해(동작/대기). MTTR 라벨 변경. 대시보드 A 카드 '확인 필요 n건' 칩. 계산 노트 표(uptime-oee.html L1105 부근) 문구 갱신: 표준 가동시간 = 14일 **중앙** CT, 고장 = 사이클 전체.
- 계측 품질 페이지: '미귀속 시간' 행.
- 문구 규약: 지속시간 `dspFmt` SSOT, 저장 버튼 dirty `*`, 새 버튼은 stitch.css 규약.

**테스트** — [OeeMathTests.cs](../DSPilot.Tests/OeeMathTests.cs): ClassifyStopWindow·대기·적립 테스트 삭제. 추가: ClassifyCycle 3분류 × 완료/불인정 행, 표본 게이트, CT 축 불인정 행(셔틀형 WT 87% 케이스에서 정상 길이 tail 누락 = 정상), 고장 비승격, 수동 라벨 조인 허용치(재도출로 경계가 움직인 행에 다시 붙고, 이웃 행엔 안 붙음), onset = 행 시작, 고장 사이클 평균 시간, 중앙 CT P. 전체 통과(`dotnet test DSPilot.Tests`).

**문서·메모리** — doc/22 §3·§4 포인터를 doc/28 로, doc/27 상단에 "doc/28 로 대체", 메모리 `project_dspilot_two_rule_stop_model_plan` 갱신, 릴리스 노트(§8).

**작업 순서**: OeeMath → OeeCtStatsService → OeeControllerBase(판정·패스 2) → 설정·DTO·API(요일 창, 전환 API) → 패턴 학습기 → UI(해부도·정산·정지 로그·전환) → 브리핑 메일·대시보드 → 테스트 → 문서. 설정 상한 확장(1~20)은 기본값 변경(5.0)과 같은 커밋에 두되 읽기 클램프가 먼저 적용되는지 확인.

## 5. 검증

1. 단위 테스트 전체 통과.
2. Playwright 정적 모의(서버 불필요): `page.route` 로 wwwroot fulfill + `/api/*` JSON 모의 — 카드·슬라이더·미리보기·칩·정지 로그 필터·범위 전환 다이얼로그 렌더 확인. [메모리 project_dspilot_ui_inspection 참조].
3. **현장 3000 재현 기준표**(§7 데이터, 고장 10× · 비생산 30×, 9/8 14:34 ~ 9/10 09:00 KST):
   - 고장 행: #131 9/9 10:45 (mt 27.3분, 행 ct ≈ 27.8분), #137 (mt 22.3분) — **표시 길이 = 행 ct**, 종전 초과분(≈ 24.9분) 대비 +중앙CT. 비생산 0건. 나머지 정상.
   - 9/9 02:34~07:23 야간 방치 3건(#131 3.8h, #136 3.8h, 셔틀 4.8h): 고장 + **확인 필요**(≥ 중앙CT × 30 ≈ 90분). 전환 UX 의 대표 시나리오.
   - 9/10 19:34 라인 정지: #132~#136 5행 동시 mt 33.6분(≈ 12×) → 고장 5건, 각 행 34.0분. 승격 없음 확인.
   - P(중앙 CT, 정상 행) 87~95%, A 99% 이상. P 손실 분해: #132~#136 MT 우세, #121·#137·D-L·셔틀 WT 우세. TEEP 정지 항이 초판 계산보다 커지고 잔여가 줄며 TEEP 값 동일.
   - 미귀속 시간(§2.7) = 전 flow 0 (0 이 아니면 결함 위치 보고).
   - 현장 API: `GET http://100.66.47.73:3000/api/dashboard/flows/{flow}/history?limit=5000`(500행 캡, IsIdle 제외), `/api/oee/downtime?from&to`, `/api/oee/summary`, `/api/oee/measurement-quality`, `/api/oee/planned-stops/actual`. 현장 서버 빌드는 정지 로그 노트 문구로 판별(9/9 10시 이후 행은 doc/27 MT/WT 문구).
4. 전환 시나리오(모의 DB): 필터 `9/5~9/7 · 고장 · 시스템 전체` 에 금 17:00~월 08:00 행 10개와 월 05:50~06:20 고장 행 1개가 뜬다 → 전체 선택 후 월요일 행만 해제 → '비생산으로' → 10행 비생산, A·고장 건수 복귀, TEEP 불변, 되돌리기 후 원복. 재도출로 행 경계가 수 초 움직인 뒤에도 라벨이 같은 행에 붙는지(조인 허용치) 확인.
5. 배포 후 현장 3000: 고장 배수 10 유지 확인, 비생산 배수는 칩 환산(≈ 29분 · 셔틀 78분)을 보고 조정, 확인 필요 건수가 브리핑 메일 상단에 뜨는지 확인.

## 6. 미결 항목 — 지시 없으면 기본값으로 진행

| # | 항목 | 기본값(진행) | 대안 |
|---|---|---|---|
| A | 동시 다발 MT 늘어짐(같은 시스템 2대 이상, 3분 이내)을 "라인 정지 1건"으로 묶는 규칙 | **넣지 않음** | 넣으면 A 가 종전 수준(92~93%)으로 내려오고 MTBF 요동 감소. 별도 플래그로 후속 |
| B | 비생산 하한 10사이클 노출 | **상수, ⓘ 설명만** | '사이클 수' 슬라이더 |
| C | 고장 배수 기본값 | **5.0** | 2.5 / 10 |
| D | 행↔구간 겹침 비율 | **해소(9/11)** — 사용자 규칙이 아니다. 전환 객체가 행이라 시간 범위 겹침이 사용자에게 생기지 않는다. 과반 0.5 는 저장 라벨을 재도출 행에 조인하는 내부 허용치로만 존속 | — |
| E | '확인 필요' 임계(표시 플래그, 판정과 무관) | **결정(9/11)**: 고장 행 `ct ≥ 중앙CT × 비생산배수`(하한 CT × 10). 현장 3000 ≈ 82~94분 — 9/9 야간 방치 3건 표시, 9/10 34분 라인 정지 미표시, 9/8 102분 정지의 고장 5건 표시. 배수를 내리면 배지가 늘고 올리면 준다(사용자 인지) | — |
| F | 반복 승격 · 요일 창 | **보류(9/11 결정)** — 자동화가 우선이고 공휴일·불규칙 비생산에 맞지 않음 | 자동화 경로 = J |
| G | 고장 사이클 평균 시간의 API 필드 | **`mttr` 필드명 유지, 라벨만 변경** | 필드 `faultCycleMeanMs` 신설 + `mttr` 병행 |
| H | 브리핑 메일 | **결정(9/11)**: 전일 00~24 하루치 클립 + 하루 경계를 넘는 고장 행·진행 중은 특이사항 줄. 발송 지연 없음 | — |
| I | 정상 행의 전환 | **불허(9/11 결정)** — 후속 '행 선택 라벨링'(사용자가 사이클을 골라 라벨)으로 분리 | — |
| J | 무신호 힌트 → 자동 제안 | **이번 범위 밖, 후속 1순위**(F 보류로 자동화 경로가 이것) | 고장 행 구간에 시스템 BOOL 태그 변화 0건이면 배지 → '비생산 제안' → 자동 전환 + 되돌리기, 단계적 |

## 7. 결정 근거 — 현장 3000 실측

### 7-A. 9/8~9/10 사이클 구조 (초판)
- #121·#13x 중앙 CT 163~188s, 중앙 MT 131~162s, **중앙 WT 14~32s(D-L 0s)**. 셔틀만 WT 156s(CT 의 87%). 완료 신호가 후공정 반출에 묶인 설비(#132~#136)는 서 있는 시간이 전부 MT 에 기록된다.
- 라인 정지(11~22분, 하루 6회)는 #132~#136 전부의 MT 가 **같은 시각·같은 길이**로 늘어남(4~10×), 동시에 #121·#131·#137·D-L·셔틀은 WT 로 늘어남. 하나의 정지가 설비별로 다른 축에 찍힌다.
- 고장 배수별 고장 행(2.3일): 2.5× 101행/29이벤트 · 5× 27행 · 8× 11행 · **10× 2행(단독 발생)** · 12× 0. → §6-A 의 배경.
- 14일 정지 로그 31건: 9/8 19:18 102분 정지가 #132~#136 '고장' 5건 + 나머지 5 flow '비생산(대기)' 로 갈림. 두 규칙 모델도 같은 분할(MT 39× 고장 / WT 102분 비생산) — 사용자 인지.
- 라이브 요약: A 95.7%, **P 1.0(고정)**, failureCount 22. 중앙 CT 로 재계산하면 P 87~95%.
- 표본 규모: 3분 사이클 라인은 하루 수백 건 → 표본 게이트 10건은 첫 시간 안에 통과.

### 7-B. 불인정 행을 MT 경계에 대면 (9/11)
- 셔틀 중앙MT ≈ 24s, 중앙CT ≈ 180s. MT 경계 5× = 120s < 정상 사이클 길이. tail 신호 한 번 누락된 정상 길이 사이클이 고장이 된다. CT 경계 5× = 900s 면 정상.
- 9/4~9/8 현장 불인정 행(`incompleteCycles`) 0건 — 지금 노출은 없지만 구조적 결함.
- 회색 지대 = 중앙WT × 배수(#13x 150s, 셔틀 780s)의 불인정 행이 정상으로 바뀐다. 그 길이는 tail 누락과 중도 리셋을 구분할 수 없으므로 정상이 정직하다.

### 7-C. '가동간 공백'의 실체 (9/11)
- 8/19 이후 행 = [시작, 다음 시작], ct = mt + wt → 사이클 사이 빈 시간은 존재하지 않는다.
- 벽시계 A 는 고장 행 **전체**를 비가동으로 세고, 고장 라벨·TEEP 정지는 **초과분**(`rec − 초과분` 부터)만 세서 차이(중앙MT 상당 + 그 행의 wt)가 '가동간 공백'으로 남았다. 7/14 당시엔 무사이클 갭 모델이라 실제 빈 시간이 있었고, doc/25 형제 대기 기준 미만도 여기로 들어왔다 — 둘 다 폐기되어 이름만 남음.
- 비생산 시각대 시작 통짜 규칙도 공백을 만든다: 12:30 시작 고장 행이 14:00 까지 이어지면 `plannedCtMs += …; continue` 로 구간 등록이 안 돼 13:00~14:00 이 미귀속.
- 초과분을 행 끝에서 거꾸로 잡는 코드(`accrualStartMs = rec − accrual`)와 초판 onset(시작 + 중앙MT)이 어긋나 있었다 — 행 단위로 가면 소멸.

### 7-D. 끄고 간 정지 vs 고장 조치 (9/11)
- 행(mt·wt·ct)만으로는 구분 불가. 행 밖 증거: 시스템 태그 변화(plcTagLog 는 변경 로그, resync 는 값 동일 시 생략 → "행 없음 = 값 불변"), 알람 신호, 심박, AUTO/MANUAL 비트, 정지·재개 시각. 전부 부분적이며, 손 안 대고 리셋한 고장·등록 태그 밖 조작은 어떤 증거로도 안 갈린다.
- 현장 3000 은 9/8 가동 시작이라 주말 데이터가 없다. 대신 9/9 02:34~07:23 야간 방치가 같은 모양: #131·#136 3.8h, 셔틀 4.8h 가 고장(현재 빌드). 사이클 단위 모델에서도 고장 → 전환 대상.
- 9/4~9/8 미계측 0, 비생산 창 0 — PLC·서버를 켜 둔 현장이라 심박 공백에 기댈 수 없다.
- 수동 전환 배관은 이미 있음: classify/set-fault/reclassify, bulk-*, 집계의 flow NULL 이벤트 전 flow 적용(`ScopedIv`), 정지 로그 일괄 선택 UI. 부족한 것은 범위·시스템 전체 1건 전환, 되돌리기, 반복 승격, 확인 필요 표시.

## 8. 릴리스 노트 항목 (소급 변경 — 조회 시 재계산이라 과거 기간도 바뀜)
- 가용성 A 가 오르고(현장 3000: 95.7% → 99%대) 성능 P 가 내려온다(100% 고정 → 87~95%). OEE 곱은 크게 변하지 않는다 — 손실이 A 에서 P 로 이동.
- 고장 건수가 줄고(14일 22건 → 2~3건 수준), 고장 시간은 사이클 전체 길이로 표시된다. MTTR 은 '고장 사이클 평균 시간'으로 이름이 바뀐다.
- 하나의 라인 정지가 설비별로 고장·비생산으로 갈릴 수 있다(완료 신호가 후공정에 묶인 설비는 고장).
- **장기 정지(주말·야간 방치)는 고장으로 기록되며 '확인 필요'로 표시된다.** 정지 로그에서 행을 골라(필터 → 전체 선택) 비생산으로 전환한다. 브리핑 메일 상단에 확인 필요 건수가 뜨고, 하루 경계를 넘는 정지는 특이사항으로 표기된다.
- 정산의 '가동간 공백' 항목이 사라지고 유지보수·고장 2분할이 된다. TEEP 는 전환과 무관하게 달력 손실을 그대로 보인다.

## 9. 구현 메모 (2026-09-11)
- **정본 위치**: 판정 순수함수 `OeeMath.ClassifyCycle/Resolve*BoundaryMs/IsReviewPending/IsMajorityCovered`, 집계 SQL `OeeControllerBase.DtCondSql`(네 경계 바인딩, 비활성 = `ThrOff`), 경계 SSOT `OeeControllerBase.BuildFlowBounds → FlowBounds`, 정지 로그 합성 `GetOverThresholdCycleDowntimeAsync`(행 전체 구간·노트 3종·`DowntimeCycleRow`), 캐시 키 v33.
- **A 공식**: 요약 KPI 를 `ComputeWallClockAvailability(RunWallMs, AvailableWallMs)` 로 통일(종전 CT축 3항 → 벽시계 양변 Σ_flow). 정산 막대·도넛도 같은 입력. 미귀속 = `UnattributedWallMs`(요약 DTO·계측 품질 flow 별) — 정산 막대엔 그리지 않고 1분 초과 시 진단 표기.
- **'진행 중'**: 열린 사이클 head↑(= 마지막 행 끝) 이후 경과 > 고장 경계(MT). `dspFlow.state = 'Going'` 인 flow 만 후보(tail 후 대기 중인 열린 사이클은 표시 없음). 상태 조회 실패 시 경과만으로 폴백.
- **전환 UX**: 전환 객체는 행. 정지 로그 필터(확인 필요 · 최소 길이 · 시간대)는 클라(uptime-workspace.js `dtPassesConvertFilters`)에서 걸고, 서버 `GET /api/oee/downtime` 도 `minDurationMs/todFrom/todTo/needsReview` 를 지원한다. 되돌리기 = `DELETE /api/oee/downtime/manual/{id}` = `POST /api/oee/downtime/{id}/revert-manual`(계산 유래 행은 삭제, 그 외는 분류만 비움). 전환·확정·되돌리기 직후 `OeeChangeSignal.NotifyInvalidate()`.
- **미리보기**: `GET ct-multipliers/preview?nonProd&fault` — 축별 건수(`faultMtCount/faultCtCount/nonProdWtCount/nonProdCtCount`)·확인 필요 건수 포함. 구 `idle` 파라미터는 무시(호환).
- **브리핑 메일**: ⓪ 블록 = 확인 필요 건수·시간 + 특이사항 행(전일부터 이어짐 / 다음 날로 이어짐 / 진행 중, 이날 몫·전체 길이 병기). 발송 지연 없음(§6-H).
- **삭제된 것**: `IdleWtMultiplier`·`SignalClassifyEnabled`(ExtensionData 보존), `ResolveWtStopBoundaryMs`·`ResolveDowntimeAccrualMs`·`ClassifyStopWindow/StopClass`·`ResolveLogStopClass`·`ResolveWaitMs`, DTO 의 `WaitWallMs/WaitSlackWallMs/EventSlackWallMs/IsWait`, JS 의 `WAIT_DEF`·대기 세그먼트·`cm.idle`. `ConfidentMinCleanCycles` 5 → 10(표본 게이트와 동일 상수).
- **현장 3000 검증(§5)**: 배포 후 확인 항목 — 고장 배수 10 유지, 9/9 야간 방치 3건 '확인 필요' 표시, 미귀속 0, 정지 로그 필터 → 전체 선택 → 비생산으로 전환 → A·고장 건수 복귀 → 되돌리기.

