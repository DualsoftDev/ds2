# Control · VirtualPlant의 실시간 신호 매핑 계약

## 역할과 전달 경로

같은 SDF를 별도의 Store와 엔진에 읽는다. Control은 Call을 실행해 OUT을 보낸다. VirtualPlant는 수신한 주소·값을 OutputSpec과 대조해 장치 Work를 시작하고, Work duration에 따라 InputSpec의 값을 IN으로 돌려준다. Control은 실제 수신 IN으로 Call 완료를 판단한다. VirtualPlant는 자신이 보낸 IN도 Hub 재수신으로 반영한다.

Control의 신규 출력 선택은 F04 계약 그대로 이번 Call의 유효 ApiCall 참조 목록에 한정한다. VirtualPlant가 수신하는 정보는 주소·값·source이며 호출 Call ID는 없다. 따라서 VP는 수신 신호에 맞는 물리 매핑을 찾는다. 같은 Tx라는 이유로 다른 주소나 다른 값 조건을 활성화하지 않는다.

## 주소가 겹치는 경우

- `D0=1 → X0`, `D0=2 → X1`: 수신값 1이면 X0, 2이면 X1을 응답한다. 등록 순서는 결과에 영향을 주지 않는다.
- 같은 주소·같은 조건에 여러 입력이 명시돼 있으면 모두 해당 신호의 물리 응답이다. 수신 정보만으로 원래 Call을 구별하지 않는다.
- 공유된 ApiCall ID는 한 수신 이벤트 안에서 한 번 처리한다. Tx Work 시작은 Work별 한 번, 동일한 지연·주소·값의 쓰기도 한 번만 예약한다.
- 조건에 맞지 않는 매핑은 기존 정책대로 입력을 해제한다. 같은 이벤트에서 활성으로 판정된 입력 주소를 다른 비활성 매핑이 해제하지 않는다.
- 서로 다른 이벤트나 다음 회차까지 합치지 않는다. Pulse 해제 이후의 완료 응답 취소 정책은 변경하지 않는다.

## 확인 범위

실제 RuntimeModeSession, RuntimeHubSession, EventDrivenEngine과 passive inference를 사용하고, 전송만 가상시간 FIFO로 대체한다. 테스트가 센서 완료를 직접 만들거나 Call을 강제로 Finish로 바꾸지 않는다.

이 검사는 공유 런타임의 연동 계약을 확인한다. Promaker 화면·SignalR 소켓·실제 PLC 통신과 하드웨어 동작까지 검증한 것은 아니다.

## 코드 근거

- `HubSession.fs`: 수신 주소의 첫 매핑만 선택하던 처리를 OutputSpec 전체 대조로 바꾼다. Monitoring도 같은 조건으로 장치 Work를 선택하며 입력 쓰기는 만들지 않는다.
- `ControlVirtualPlantTestHarness.fs`: 동일 SDF를 두 Store에 읽고 실제 엔진·세션 사이의 신호를 가상시간 FIFO로 전달한다.
- `ControlVirtualPlantInteropTests.fs`: Bool·숫자 출력, 미실행 매핑, 복수 응답, 공유 ApiCall, 입력 주소 중복을 확인한다.

이번 변경은 실시간 수신 매핑 오류에 한정한다. 초기 동기화와 Reference Call의 IO 매핑은 별도 오류 항목으로 검증한다.

## 검증 결과 (2026-09-27)

- ControlVirtualPlantInteropTests 11/11 (두 엔진 왕복 7건, 세션 효과 4건).
- 세 수정의 최종 조합에서 Ds2.Store.Editor.Tests 전체 806/806 통과, 실패·건너뜀 0개. 기존 F04 14건도 포함한다.
