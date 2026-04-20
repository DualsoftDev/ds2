# DSPilot.TestConsole 2기능 정리 문서

작성일: 2026-03-19

## 1. 목적

`DSPilot.TestConsole`을 아래 2개 기능만 남는 구조로 정리한다.

1. 기존 DB를 읽어서 PLC에 신호를 리플레이하며 쓰는 기능
2. PLC 값을 읽어서 DB에 남기고, 그 값을 기준으로 이벤트를 발생시키는 기능

핵심은 "테스트 콘솔 안에서 이것저것 다 하는 구조"를 버리고, **Replay 모드**와 **Capture 모드** 두 축으로 책임을 고정하는 것이다.

---

## 2. 현재 상태 요약

현재 `Program.cs`는 5개 메뉴를 직접 노출하고 있다.

- `SimplePlcWriteTest`: DB 로그를 읽어 PLC에 씀
- `Ev2WalEventReceiveTest`: PLC 스캔 + WAL 기반 DB 저장 + 실시간 모니터링
- `FullIntegrationTest`: 이벤트 처리 전체 통합 테스트 시도
- `DllInspector`
- `DbVerifier`

문제는 PLC 쓰기/읽기 책임이 아래처럼 중복되어 있다는 점이다.

### PLC 쓰기 쪽 중복

- `SimplePlcWriteTest.cs`
- `PlcLogReplayTest.cs`

둘 다 `plcTagLog`를 읽고 시간 간격을 유지하며 PLC에 값을 쓰는 흐름을 갖고 있다.  
실제 운영 형태로 남길 것은 하나면 충분하다.

### PLC 읽기/DB 저장 쪽 중복

- `EventReceiveTest.cs`
- `Ev2WalEventReceiveTest.cs`

`EventReceiveTest`는 직접 채널을 만들고 Dapper로 `plcTagLog`에 넣는다.  
`Ev2WalEventReceiveTest`는 EV2 `ModuleInitializer.Initialize()`와 `TagHistoricWAL` 패턴을 사용한다.

두 방식이 동시에 남아 있으면 다음 문제가 생긴다.

- DB 저장 기준이 2개가 됨
- 장애 시 어떤 경로가 정답인지 애매함
- 테스트 콘솔 문서와 실제 DSPilot 구조가 달라짐

---

## 3. 정리 기준

정리 기준은 단순하다.

### 남길 축

- `Replay`: DB -> PLC
- `Capture`: PLC -> DB -> Event

### 빼야 할 축

- 개발 참고용 도구
- 미완성 통합 테스트
- 같은 역할의 중복 구현

즉, `DSPilot.TestConsole`은 "테스트를 위한 런처"가 아니라, 아래 두 시나리오를 검증하는 **기능형 콘솔**로 바뀌어야 한다.

1. 과거 현장 로그를 PLC에 다시 흘려보낼 수 있는가
2. PLC 현재값을 읽어 DB 적재와 이벤트 생성이 동시에 되는가

---

## 4. 권장 최종 구조

### 4.1 메뉴/엔트리 포인트

`Program.cs`는 아래처럼 단순화하는 것이 좋다.

```text
1. Replay DB logs to PLC
2. Capture PLC values to DB and publish events
9. Tools
0. Exit
```

여기서 핵심 메뉴는 1, 2만 유지한다.  
`Tools`는 `DbVerifier`, `DllInspector` 같은 보조 도구를 넣는 별도 그룹이다.

`FullIntegrationTest`는 메인 메뉴에서 빼고, 필요하면 Capture 모드 위에 올리는 별도 검증 코드로만 남긴다.

### 4.2 폴더/파일 구조

권장 구조는 아래와 같다.

```text
DSPilot.TestConsole/
  Program.cs
  Modes/
    ReplayMode.cs
    CaptureMode.cs
  Replay/
    ReplayRunner.cs
    ReplayLogReader.cs
    ReplayScheduler.cs
    ReplayValueConverter.cs
  Capture/
    CaptureRunner.cs
    CaptureSubscriber.cs
    CaptureEventPublisher.cs
    CaptureEdgeDetector.cs
  Shared/
    PlcConnectionFactory.cs
    PlcTagSpecFactory.cs
    PlcTagDbReader.cs
    PlcValueFormatter.cs
  Tools/
    DbVerifier.cs
    DllInspector.cs
```

중요한 점은 `Replay`와 `Capture`가 서로 직접 의존하지 않게 하는 것이다.  
공통은 `Shared`로만 올린다.

---

## 5. 기능별 정리 방향

## 5.1 Replay 모드

### 목적

기존 `plcTagLog`를 읽어서 원래 시간 간격대로 PLC에 다시 쓰는 기능이다.

### 입력

- DB 경로
- PLC 연결 설정
- 리플레이 시간 범위
- 반복 여부
- 속도 배율

### 출력

- PLC write 수행
- 콘솔 진행률
- 성공/실패 통계

### 기준 구현

Replay 모드의 기반은 `SimplePlcWriteTest.cs`를 잡는 것이 맞다.

이유:

- DB에서 태그를 읽어 `TagSpec`를 동적으로 만드는 흐름이 이미 있음
- `PLCBackendService` 시작부터 리플레이까지 한 파일에서 닫혀 있음
- 실제 "DB -> PLC write" 관점에서 가장 직접적임

`PlcLogReplayTest.cs`는 별도 모드로 남기지 말고, 안에 있는 시간 간격 유지 로직만 `ReplayScheduler` 성격으로 흡수하면 된다.

### Replay 내부 책임 분리

`ReplayLogReader`
- `plcTag`, `plcTagLog`를 읽음
- 시간 범위 필터링
- 태그 메타데이터 정리

`PlcTagSpecFactory`
- DB 태그를 `TagSpec`으로 변환

`ReplayValueConverter`
- DB 문자열 값을 `PlcValue`로 변환

`ReplayScheduler`
- 로그 간 timestamp delta 계산
- 실시간/배속/무한반복 제어

`ReplayRunner`
- PLC 연결
- 리플레이 실행
- 통계 및 종료 처리

### Replay에서 하지 말아야 할 일

- DB 저장
- Rising Edge 이벤트 처리
- Call 상태 전이
- Cycle Analysis

Replay는 어디까지나 **쓰기 재생기**여야 한다.

### Replay 실행 흐름

```text
plcTag/plcTagLog 읽기
-> 고유 태그 추출
-> TagSpec 생성
-> PLCBackendService 시작
-> 로그 시간차 계산
-> PlcValue 변환
-> PLC write
-> 결과 집계
```

---

## 5.2 Capture 모드

### 목적

PLC 값을 읽고, DB에 적재하고, 그 값을 기준으로 이벤트를 발생시키는 기능이다.

### 입력

- PLC 연결 설정
- 감시 대상 태그 목록
- DB 경로
- 스캔 주기

### 출력

- `plcTagLog` 저장
- Rising Edge 등 이벤트 발생
- 콘솔 상태/통계 출력

### 기준 구현

Capture 모드는 `Ev2WalEventReceiveTest.cs`를 기준 구현으로 삼는 것이 맞다.

이유:

- EV2 WinForm TestApp 패턴과 맞음
- `ModuleInitializer.Initialize()`를 통해 `TagHistoricWAL`과 `PLCBackendService`를 함께 올림
- "PLC 읽기 -> WAL -> DB flush" 경로가 이미 정리되어 있음
- 수동 INSERT보다 실제 운영 구조에 더 가깝다

반대로 `EventReceiveTest.cs`의 직접 `INSERT INTO plcTagLog` 방식은 **보조 참고용 로직**으로만 쓰는 것이 좋다.

이유:

- DB 저장 경로를 EV2 WAL과 별도로 또 만들게 됨
- 저장 일관성이 깨질 수 있음
- 유지보수 시 두 구현을 같이 수정해야 함

따라서 Capture 모드는 아래처럼 가져가야 한다.

### Capture 내부 책임 분리

`CaptureRunner`
- EV2 초기화
- 구독 시작/종료
- 통계 집계

`CaptureSubscriber`
- `GlobalCommunication.SubjectC2S` 또는 EV2 스캔 결과 수신
- raw PLC 값을 내부 이벤트 객체로 변환

`CaptureEventPublisher`
- 내부 이벤트를 `DSPilot.Models.PlcCommunicationEvent` 유사 구조로 발행
- 필요 시 `PlcEventProcessorService`로 연결

`CaptureEdgeDetector`
- 이전값 대비 Rising/Falling Edge 판정

`PlcTagDbReader`
- 감시 대상 태그를 DB에서 읽어옴

### Capture에서 권장하는 저장 기준

DB 저장의 authoritative path는 한 가지여야 한다.

권장:

- **DB 저장은 EV2 WAL이 담당**
- **테스트 콘솔은 수신값을 이벤트/모니터링 용도로만 후처리**

즉 아래 순서가 좋다.

```text
PLC scan
-> EV2 WAL 적재
-> DB flush
-> 수신값 구독
-> Edge 판정
-> 내부 이벤트 발행
```

### Capture 실행 흐름

```text
태그 목록 준비
-> ModuleInitializer.Initialize()
-> PLCBackendService 시작
-> Subject 구독
-> 수신값 표준화
-> Edge 판정
-> 이벤트 발행
-> 통계/로그 출력
```

---

## 6. DSPilot 본체와 맞추는 방법

테스트 콘솔 정리 방향은 이미 DSPilot 본체에 있는 추상화와 맞춰야 한다.

### Capture는 본체 구조와 맞춘다

현재 본체에는 아래 구조가 이미 있다.

- `IPlcEventSource`
- `Ev2PlcEventSource.Real`
- `PlcEventProcessorService`
- `PlcToCallMapperService`

즉 Capture 모드는 단독 테스트에 그치지 말고, 최종적으로는 아래 구조를 흉내 내야 한다.

```text
PLC
-> IPlcEventSource 성격의 입력
-> Channel
-> Rising Edge 판정
-> Tag -> Call 매핑
-> 상태 전이 / IO 이벤트 기록
```

테스트 콘솔에서는 전체 DI까지 다 끌고 오지 않아도 되지만, 이벤트 payload 형식은 본체와 같게 가져가는 것이 좋다.

### Replay는 본체의 Simulation 개념과 맞춘다

현재 본체 `PlcDataReaderService`는 DB의 과거 로그를 1초 윈도우로 읽어 재생하는 구조를 이미 갖고 있다.  
다만 그 대상이 PLC가 아니라 DSP 내부 처리 파이프라인이다.

Replay 모드는 그 구조를 참고하되, sink를 아래처럼 바꾼 버전으로 보면 된다.

```text
기존: DB -> DSP 처리
목표: DB -> PLC write
```

즉, 시간창 개념과 증분 재생 개념은 참고하되 목적지는 PLC여야 한다.

---

## 7. 현재 파일을 어떻게 정리할지

### 유지

- `Program.cs`
- `SimplePlcWriteTest.cs` -> Replay 기반 코드로 흡수
- `Ev2WalEventReceiveTest.cs` -> Capture 기반 코드로 흡수
- `DbVerifier.cs`
- `DllInspector.cs`

### 흡수/분해

- `PlcLogReplayTest.cs`
  - 별도 모드로 남기지 말고 `ReplayScheduler`/`ReplayLogReader` 쪽으로 분해

- `EventReceiveTest.cs`
  - 수동 DB INSERT 전체를 남기지 말고
  - 채널 처리, Edge 감지, 후처리 아이디어만 `CaptureEdgeDetector` 또는 `CaptureEventPublisher` 쪽으로 흡수

### 메인 메뉴에서 제외 권장

- `FullIntegrationTest.cs`

이 파일은 지금 상태로는 "두 기능 정리" 목적과 맞지 않는다.  
Capture 모드가 안정화된 뒤, 그 위에 붙는 상위 검증 코드로만 두는 것이 맞다.

---

## 8. 단계별 정리 순서

### 1단계. 메뉴 축소

- `Program.cs`를 Replay/Capture 중심 메뉴로 변경
- 검사 도구는 `Tools`로 이동

### 2단계. 공통 코드 추출

공통으로 반복되는 아래 코드를 먼저 뺀다.

- DB에서 태그 읽기
- `TagSpec` 생성
- dataType -> `PlcDataType` 변환
- string -> `PlcValue` 변환
- PLC 연결 설정 생성

### 3단계. Replay 확정

- `SimplePlcWriteTest`를 기준으로 `ReplayRunner` 구성
- `PlcLogReplayTest` 중 시간간격 재생 부분만 흡수
- 무한 루프는 옵션으로만 남김

### 4단계. Capture 확정

- `Ev2WalEventReceiveTest`를 기준으로 `CaptureRunner` 구성
- DB 저장은 WAL 경로 하나만 사용
- `EventReceiveTest`의 Edge 감지/채널 로직만 후처리 모듈로 이동

### 5단계. DSPilot 이벤트 연결

- Capture 결과를 `PlcCommunicationEvent`와 유사한 내부 모델로 표준화
- 이후 `PlcEventProcessorService`와 연결 가능한 형태로 고정

---

## 9. 권장 CLI 형태

메뉴형보다 명령형이 더 낫다.

예시:

```bash
dotnet run -- replay --db sample/db/DsDB.sqlite3 --speed 1.0 --repeat false
dotnet run -- capture --db sample/db/DsDB.sqlite3 --scan-interval 500
dotnet run -- tool verify-db --db sample/db/DsDB.sqlite3
```

이렇게 하면 문서/자동화/운영 스크립트가 쉬워진다.

그래도 당장 메뉴를 유지해야 한다면, 내부적으로는 위 명령 객체를 호출하는 방식으로 구현하는 것이 좋다.

---

## 10. 최종 권장안 한 줄 정리

`DSPilot.TestConsole`은 아래처럼 정리하는 것이 가장 맞다.

- **Replay는 `SimplePlcWriteTest` 기반으로 단일화한다**
- **Capture는 `Ev2WalEventReceiveTest` 기반으로 단일화한다**
- **`PlcLogReplayTest`, `EventReceiveTest`, `FullIntegrationTest`는 메인 기능이 아니라 흡수 또는 보조 코드로 낮춘다**

---

## 11. 바로 실행 가능한 정리안

가장 현실적인 정리안은 아래다.

### A안. 최소 변경

- 메뉴는 유지
- 내부 구현만 Replay/Capture로 재배치
- 기존 클래스명은 당장 유지 가능

장점:

- 영향 범위가 작음
- 빨리 정리 가능

단점:

- 파일명이 역할과 안 맞을 수 있음

### B안. 권장안

- `ReplayMode`, `CaptureMode`로 클래스명도 변경
- 공통/개별 책임을 폴더 단위로 분리
- 메인 메뉴를 2기능 중심으로 재작성

장점:

- 문서와 코드가 일치함
- 이후 DSPilot 본체와 연결하기 쉬움

단점:

- 초반 수정량이 조금 더 큼

현재 코드 상태를 보면 **B안이 맞다**.

---

## 12. 결론

이 작업은 "테스트 항목 정리"가 아니라 "입력 방향 기준으로 구조를 재정의"하는 작업이다.

정리 기준은 아래 두 문장으로 끝난다.

1. 과거 DB 로그를 PLC로 보내는 것은 `Replay`
2. PLC 현재값을 DB와 이벤트로 보내는 것은 `Capture`

이 둘 외의 코드는 공통 지원이거나 보조 도구여야 한다.  
이 기준으로 정리하면 `DSPilot.TestConsole`은 이후 DSPilot 본체 서비스와도 자연스럽게 맞물린다.
