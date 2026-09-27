# Reference Call의 IO 매핑 계약

## 문제

Reference Call은 원본 ApiCall로 출력을 켜고 완료를 판단하지만, 기존 IO 맵은 Reference Call 자신의 빈 ApiCalls만 읽었다. 그 결과 완료 후 Normal 출력 해제를 찾지 못했다.

예: 원본의 `D0=1 / X0=true`를 Reference Call에서 실행하면 장치 응답 후 Call은 Finish가 됐지만 `D0=0`이 빠졌다.

## 확정 동작

- IO 맵은 SimIndex와 동일하게 ReferenceOf의 원본 ApiCall 목록을 사용한다. 원본을 찾지 못하면 기존 SimIndex와 같은 자기 목록 fallback을 유지한다.
- ApiCall ID·주소·ApiDef는 원본 것을 사용하고, CallGuid·SystemId는 실제 Reference Call의 소속을 유지한다.
- 실행 필터에 원본 Call이 없어도 선택한 Reference Call의 IO 매핑을 구성한다. 선택하지 않은 원본을 실행 대상으로 추가하지 않는다.
- 같은 Call 목록에 중복된 ApiCall ID는 한 번 매핑한다. 서로 다른 Call 위치는 별도 매핑을 유지한다.
- 완료 해제는 기존 ActionType 규칙을 따른다. Normal은 완료 즉시, Normal 연장은 지정 시간 후 해제한다. Pulse는 시작 시 예약한 해제를 사용하고 Latch는 완료만으로 해제하지 않는다.

| 장치 응답 50ms의 예 | 출력 기록 |
| --- | --- |
| Normal | D0=1@0ms → D0=0@50ms |
| Normal +20ms | D0=1@0ms → D0=0@70ms |
| Pulse 10ms | D0=1@0ms → D0=0@10ms, 완료 응답은 50ms |
| Latch | D0=1@0ms, 완료만으로 해제하지 않음 |

## 근거

`SignalIOMap.fs`에서 유효 참조를 해석한다. `ReferenceCallInteropTests.fs`는 Normal의 실제 두 엔진 왕복 완료·출력 해제와 필터 범위·소유 System을 검사한다. Normal 연장·Pulse·Latch는 실제 런타임 완료 컨텍스트가 반환하는 해제 정책을 별도로 검사한다. Control의 지연 출력은 Task.Delay를 사용하므로 위 70ms·10ms는 정책상 기대 시간이며, 가상시간 왕복 시험으로 실제 시간 오차까지 측정한 값은 아니다.

## 검증 결과 (2026-09-27)

- ReferenceCallInteropTests 6/6 (두 엔진 왕복 1건, IO 매핑·해제 정책 5건).
- 세 수정의 최종 조합에서 Ds2.Store.Editor.Tests 전체 806/806 통과, 실패·건너뜀 0개. 기존 F04 14건도 포함한다.
