# Control · VirtualPlant 초기 동기화 계약

## 문제와 수정 원칙

같은 모델의 실시간 처리에서는 ValueSpec을 비교하지만, 기존 초기 동기화는 문자열 `true`만 활성으로 판단했다. 따라서 `D0=2`나 실제 Bool 대표값 `True`를 수신해도 시작 상태를 놓쳤다.

- VirtualPlant는 주소와 값이 있는 현재값을 실시간 HubSession에 전달한다. 숫자·Bool·비활성값 모두 같은 매핑 규칙을 적용한다.
- Control은 각 Work의 Tx/Rx 매핑에 해당하는 OutputSpec/InputSpec을 비교한다. 같은 주소라도 다른 Work의 매핑 조건을 대신 사용하지 않는다.
- 출력만 활성: Going. 입력 활성: Finish. 둘 다 비활성: Ready. 기존 상태 추론 우선순위를 유지한다.
- 누락·null·빈 문자열·공백값은 활성 조건으로 평가하지 않는다. Control의 정보 없음→Ready 기존 정책은 유지한다.
- Monitoring의 현재값 baseline 처리와 모드별 source 무시 정책은 변경하지 않는다.

| 현재값 예 | 모델 조건 | 결과 |
| --- | --- | --- |
| D0=2 | OUT Int16=2 | VP 장치 시작 및 IN 응답 예약, Control Going |
| X0=9 | IN Int16=9 | Control Finish |
| D0=False | OUT Bool=false | 활성으로 평가 |
| D0=True | OUT Bool=true | 활성으로 평가 |
| D0=0 | OUT Int16=2 | VP 입력 해제, Control Ready (IN 비활성일 때) |
| D0 값 누락 | OUT Bool=false | 누락을 false 활성으로 해석하지 않음 |

## 근거

`ModeSession.fs`의 VP 현재값 필터와 `BootstrapSession.fs`의 Control 상태 추론을 수정한다. `RuntimeSnapshotBindingTests.fs`가 실시간과 현재값 복원의 의미 일치, 공유 매핑, Work별 범위와 모르는 값의 처리 방식을 검사한다.

가상시간 연동 검사와 별도로 세션이 반환하는 초기화 효과를 검증한다. 실제 소켓 재접속·누락 패킷·동시 변경의 순서 보장까지 검증한 것은 아니다.

## 검증 결과 (2026-09-27)

- RuntimeSnapshotBindingTests 16/16.
- 세 수정의 최종 조합에서 Ds2.Store.Editor.Tests 전체 806/806 통과, 실패·건너뜀 0개. 기존 F04 14건도 포함한다.
