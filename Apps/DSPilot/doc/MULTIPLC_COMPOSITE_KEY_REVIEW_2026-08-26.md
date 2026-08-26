# 멀티 PLC 복합키 패스 검사 잔여 항목 (2026-08-26)

`55371f20..115a2640` (SystemId, 주소) 복합키 4커밋 병합 후 코드 검사에서 나온 잔여 항목 정리.
검증 상태: 빌드 경고 0·오류 0, 테스트 통과(DSPilot.Tests 304 / Ds2.Aasx.Tests 79 / Ds2.Store.Editor.Tests 588).

**수정 완료(본 문서에서 제외)**: UserTagAlertService 주소 폴백 과잉 매칭 — 로그·정의 양쪽 System 이
명확한데 복합키 미스면 폴백하지 않도록 게이트 추가(2026-08-26). 폴백은 어느 한쪽이 System 미상일 때만.

## 처리 결과 (2026-08-26 리팩토링 패스)

| 항목 | 상태 |
|---|---|
| 1-1 `GetLatestValuePerTagAsync` 삭제 → `GetMaxLogIdAsync` | ✅ 완료 |
| 1-2 `GetCycleBoundaries` 들여쓰기 | ✅ 완료 (블록 `{}` 로 명시) |
| 2-1 SystemKey 규약 3중 복제 | ✅ 완료 (`Infrastructure/SystemKeyConvention.cs` 단일화 + 회귀 테스트 9건) |
| 2-2 plcTag 주소 단독 인덱스 | ✅ 이미 존재 (조치 불필요 — 아래 참조) |
| 2-3 `CallTestController` 3회 호출 | ✅ 완료 (2개 메서드에서 `var systemId` hoist) |
| 2-4 `SignalHub.QueryTag` 경고 무스로틀 | ✅ 완료 (60초 1회 + 누적 건수) |
| 2-5 스코프 SQL 조각 10회 반복 | ⛔ 미적용 (인라인 SQL 가독성 우선 — 대신 규약을 클래스 doc 으로 문서화) |

검증: `Ds2.sln` 빌드 경고 0·오류 0, DSPilot.Tests 313(신규 9) / Ds2.Aasx.Tests 79 / Ds2.Store.Editor.Tests 588 통과.

---

## 1. 수정 권장 (동작·성능에 실익)

### 1-1. `GetLatestValuePerTagAsync` — 낭비 + 주소 키 collapse 잔존

- 위치: `Repositories/PlcRepository.cs` (`GetLatestValuePerTagAsync`)
- 호출자 2곳(`PlcDatabaseMonitorService` 시드, `UserTagAlertService` 초기 워터마크) **모두 이제
  `MaxLogId` 만 사용**하고 태그별 최신값 딕셔너리는 버린다.
- 그런데 쿼리는 여전히 태그수 × `MAX(l2.id)` 상관 서브쿼리 조인을 매 초기화마다 실행한다.
- 반환 딕셔너리도 주소 키라 멀티 PLC 에서 같은 주소가 collapse 되는 형태 그대로 잔존
  (지금은 소비자가 없어 무해하지만, 새 소비자가 붙으면 오귀속 소스가 된다).
- **권장**: `Task<long> GetMaxLogIdAsync()` (`SELECT MAX(id) FROM plcTagLog`) 를 신설하고
  두 호출자를 갈아탄 뒤 본 메서드를 삭제. 인터페이스에서 제거해 새 소비자 유입을 차단.

### 1-2. `CycleAnalysisController.GetCycleBoundaries` — 들여쓰기 오독

- 위치: `Controllers/CycleAnalysisController.cs` (약 L203-207)

```csharp
if (!string.IsNullOrEmpty(startTagAddress))
    // 멀티 PLC: 이 Flow 의 PLC 로 한정 — ...
return await _plcRepository.FindRisingEdgesAsync(          // ← if 본문인데 if 와 같은 레벨
    startTagAddress, start, end, _project.TryGetSystemIdByFlowName(flowName));
return await _cycleAnalysis.GetCycleBoundaryTimesAsync(flowName, start, end);
```

- 동작은 정상(주석은 문장에 안 세므로 첫 return 이 if 본문)이나, 무조건 return + 죽은 코드처럼
  읽힌다. 첫 return 을 한 단계 들여쓰면 끝.

---

## 2. 리팩토링 권장 (우선순위 낮음)

### 2-1. SystemKey 규약 3중 복제

Guid → 소문자 `"D"` 문자열(= `plc.systemId` 컬럼 표기) 규약이 각자 구현돼 주석 상호참조로만 묶여 있다:

| 위치 | 이름 |
|---|---|
| `Repositories/PlcRepository.cs` | `Scope(Guid?)` |
| `Services/SimulationEngineService.cs` | `SystemKey(Guid?/string?)` |
| `Services/UserTagAlertService.cs` | `SysKey(Guid/string?)` |
| `Services/HubSubscriberService.cs` | resync dedupe 키는 **비정규화 원문** `it.SystemId` 사용 (자체 일관이라 무해) |

- **권장**: `SystemKeyConvention`(또는 유사) static 헬퍼 하나로 단일화. 표기가 한 곳이라도
  어긋나면 조회 스코프가 조용히 0건이 되는 규약이라 컴파일 수준으로 묶어둘 가치가 있다.

### 2-2. plcTag 주소 단독 인덱스 소멸 — ✅ 이미 적용돼 있었음

- `UNIQUE(address)` → `UNIQUE(plcId, address)` 전환으로 주소 선행 인덱스가 사라졌다
  (복합 UNIQUE 는 plcId 선행이라 주소 단독 조회에 못 쓴다).
- plcTag 는 수백 행 수준이라 실측 영향은 미미하나, 조회 경로 전부가 `t.address` 기반이므로
  `CREATE INDEX IF NOT EXISTS idx_plcTag_address ON plcTag(address)` 한 줄 가치 있음.
- 추가 위치: `DspRepositoryAdapter` 의 인덱스 생성 블록(★`MigratePlcTagUniquenessAsync` 호출
  **뒤**여야 함 — 테이블 재작성 시 인덱스가 함께 사라지는 것과 같은 이유).
- **검사 결과**: 이 인덱스는 이미 두 곳에 있고 순서도 맞다 — `DspRepositoryAdapter.cs`
  (`createPlcTagAddressIdx`, `MigratePlcTagUniquenessAsync` 호출 뒤에 실행) + `PlcRepository.cs`
  부팅 self-heal 인덱스 목록. 조치 불필요.

### 2-3. `CallTestController` — `TryGetSystemIdByFlowName` 요청당 3회 호출

- 같은 요청 안에서 동일 인자로 3번 호출(캐시 딕셔너리 조회라 비용은 미미). 지역변수로 hoist 하면
  `CycleRecomputeService` 의 `var systemId = ...` 패턴과 일관돼진다.

### 2-4. `SignalHub.QueryTag` 모호 경고 무스로틀

- 주소 소유자 2개 이상이면 매 호출 `log.Warn`. Control 이 부팅 홈포지션 추론에서 다수 주소를
  반복 조회하면 로그 폭주 가능. DSPilot 쪽 `WarnAmbiguousTagOwner`(60초 1회 + 카운트) 패턴 이식 후보.

### 2-5. PlcRepository 스코프 SQL 조각 10회 반복

- `LEFT JOIN plc p ON p.id = t.plcId` + `AND (@SystemId IS NULL OR p.systemId = @SystemId)` 가
  10개 쿼리에 반복. 인라인 SQL 스타일 유지 전제면 const 조각 추출 정도만 — 강제 아님.
- **결론: 미적용.** 10곳이 들여쓰기·컬럼 대소문자(`t.Address`/`t.address`)·감싸는 CTE 구조가 다 달라
  하나의 const 조각으로 안 맞고, 두 조각을 `{...}` 보간으로 끼우면 verbatim SQL 리터럴의 가독성이
  더 나빠진다. 대신 규약(LEFT JOIN 이유 · `SystemKeyConvention.Scope` 필수 · 빈 문자열 금지)을
  `PlcRepository` 클래스 `<remarks>` 로 문서화해 발견 가능하게 남겼다.

---

## 3. 설계상 의도된 공백 (조치 불필요 — 코드 주석에 근거 존재)

- **AASX 재로딩 사이 신규 UserTag 주소는 plcId=1**: `EnsureUserTagAddressesRegistered` 는 소유
  System 을 계산하지 않는다. 다음 `BootstrapPlcTags`(모델 재로딩)에서 소급 귀속. 그 사이 스코프
  조회(systemId 지정)에는 이 주소의 로그가 안 잡힌다.
- **구버전 송신자 + 중복 주소 = plcTagLog 기록 skip**: 오귀속 영구화 방지를 위한 의도적 선택.
  60초 스로틀 경고(`WarnAmbiguousTagOwner`)로 드러남. 해소는 Agent/수집기를 systemId 싣는
  버전으로 올리는 것.
- **PlcDatabaseMonitorService 시드 제거**: 부팅 후 태그별 첫 변화 1회가 직전값 "0" 대비로 흐를
  수 있으나 PlcDebug 브로드캐스트 전용 경로라 무해.
- **소유자 2+ 주소의 과거(plcId=1) 이력**: backfill 은 소유자 유일 주소만 소급한다. 여러 System
  이 쓰는 주소의 업그레이드 이전 이력은 어느 쪽인지 알 수 없어 귀속 미상으로 남는 게 맞다.
