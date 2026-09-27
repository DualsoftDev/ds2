# Call 실행 요청의 출력 범위

상태: 확정 (2026-09-27). 구조 검토 F04에 대한 계약이다.

## 확정 규칙

1. 신규 출력은 이번 Call의 **유효 ApiCall 참조 목록**에서만 선택한다.
2. 같은 Work를 가리킨다는 이유로 다른 ApiCall을 추가하지 않는다.
3. ApiCall 공유·복수 출력·Reference Call을 유지한다. 한 실행 요청에서 같은 ApiCall ID는 한 번만 적용한다.

유효 참조 목록은 `SimIndex.CallApiCallGuids[callId]`다. 인덱스가 해석한 Reference Call의 원본 참조를 그대로 사용한다. ApiCall은 여러 Call이 공유할 수 있으므로 Call의 전용 소유 객체로 제한하지 않는다.

## 실행 단위

- 출력 효과는 요청에 포함된 ApiCall ID별로 한 번 적용한다.
- 서로 다른 ApiCall이 같은 Tx Work를 가리켜도 각각의 출력은 유지한다.
- 대상 Work의 시작 처리는 요청 안에서 Tx Work별로 한 번 수행한다.
- ApiDef ID나 출력 주소가 같다는 이유로 서로 다른 ApiCall을 합치지 않는다.
- 중복 제거 범위는 **한 실행 요청**이다. 여러 Call의 동시 요청이나 다음 실행 회차까지 합치는 규칙이 아니다.

## 보존하는 동작

- 기존 모드·Tx 존재 여부·대상 Work 상태에 따른 실행 자격을 유지한다. Tx가 없거나 Finish인 대상의 출력을 새로 허용하지 않는다.
- AutoAux, SkipAction, 완료 판정과 Normal/Pulse/Latch/Virtual 정책은 변경하지 않는다.
- 선택된 명령에 따른 이전 Latch 해제는 신규 활성 출력과 구별한다. 다른 주소의 해제까지 금지하는 계약이 아니다.
- Homing의 실행 Call 선택 계획은 변경하지 않는다. 계획에서 선택한 각 Call 요청 역시 그 Call의 유효 참조 목록과 허용된 Tx만 실행한다. Call 상태가 Going인지로 Homing을 제한하지 않는다.

## 상세 예시

A와 B가 같은 ApiDef와 Tx Work를 가리키되 서로 다른 ApiCall을 가진다고 하자. A의 출력은 `D0=1`, B의 출력은 `D0=2`이고 A만 실행한다.

| 구성 | 수정 전 엔진 콜백 | 확정 동작 |
| --- | --- | --- |
| A만 존재 | D0=1 | D0=1 |
| 미실행 B가 다른 주소 D1=2 사용 | D0=1, D1=2 | D0=1 |
| 미실행 B가 같은 주소 D0=2 사용 | D0=1, D0=2 | D0=1 |
| A에 같은 Tx의 서로 다른 ApiCall 두 개 명시 | 각 출력이 두 번씩 기록됨 | 각 출력 한 번, Work 시작 한 번 |
| A와 미실행 B가 같은 ApiCall ID 공유 | 공유 명령 한 번 | 공유 명령 한 번 |
| Reference Call 실행 | 원본 참조 사용 | 원본 참조 사용 |

이 기록은 Control 모드의 메모리 `WriteTag(address, value)` 콜백이다. PLC 쓰기 성공이나 화면의 숫자 변화를 뜻하지 않는다. 실제 장치 전달은 연결·주소 매핑·자료형·통신 계층의 별도 검증이 필요하다.

## 구현 근거

- [CompositionContext.fs](../Solutions/Runtime/Ds2.Runtime/Engine/EventDriven/Composition/CompositionContext.fs): Work에서 전체 Call을 역검색하던 방식을 제거하고, 요청 Call의 유효 ApiCall ID로 binding을 해석한다.
- [Execution.fs](../Solutions/Runtime/Ds2.Runtime/Engine/EventDriven/Lifecycle/Execution.fs): 요청 안의 ApiCall ID를 중복 제거하고 Tx별로 묶어 출력 효과와 Work 시작을 분리한다.
- [CallOutputScopeTests.fs](../Solutions/Tests/Ds2.Store.Editor.Tests/CallOutputScopeTests.fs): Control 메모리 출력과 실행 컨텍스트를 검사한다.

## 별도 계약으로 남는 항목

서로 다른 Call이 같은 주소에 상충하는 값을 동시에 요청할 때의 중재, 공유 출력의 해제 소유권, 전역 Latch 저장소 수명, 지연 출력 취소, timeout 회차 관리는 이번 변경에 포함하지 않는다.

## 검증 결과

- `CallOutputScopeTests`: 14개 통과. 실제 Control 엔진 검사 8개와 실행 컨텍스트 검사 6개다.
- `Ds2.Store.Editor.Tests`: 신규 14개를 포함한 전체 773개 통과, 실패·건너뜀 0개.
- 수정 전 재현에 사용한 동일 SDF 8개를 수정 후 엔진으로 재실행해 모두 확정 결과와 일치했다.
- Runtime Release 빌드: 경고 0개, 오류 0개.

```powershell
dotnet test Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj -c Release --filter FullyQualifiedName~CallOutputScopeTests
dotnet test Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj -c Release --no-build --no-restore
```
