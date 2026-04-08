# CostSim 최종 개발 문서

작성일: 2026-04-02

## 1. 목적

`CostSim`은 `ds2` 코어를 재사용해 공법 트리 기반으로 공정 모델을 편집하고, `SequenceOrder` 순서대로 인과 체인을 구성한 뒤 원가/시뮬레이션/리포트를 수행하는 WPF 제품이다.

핵심 목표는 다음과 같다.

- 캔버스 편집 대신 트리와 속성 입력 중심 UX 제공
- `System > Flow > Work` 구조를 기반으로 공법을 빠르게 구성
- 라이브러리 경로에서 기존 모델을 불러와 append/rebuild 방식으로 공법 재구성
- 시뮬레이션 결과를 텍스트/HTML/CSV/Excel 리포트로 출력

## 2. 현재 제품 범위

현재 `CostSim`은 아래 기능을 제공한다.

- 프로젝트/시스템/플로우/워크 추가, 삭제, 이름 변경
- 우클릭 컨텍스트 메뉴 기반 편집
- 워크 속성 입력
  - `OperationCode`
  - `Duration`
  - 인원수
  - 인건비/설비비/간접비/유틸리티
  - `Yield` / `Defect`
  - `Source`
- `SequenceOrder` 기준 정렬, 정규화, 상하 이동
- 트리 순서를 기반으로 인과 Arrow 재구성
- JSON / AASX 저장 및 로드
- 라이브러리 폴더 스캔 후 모델 append / rebuild
- 데모 라이브러리 자동 생성
- 시뮬레이션 실행 및 Report/Excel export
- Output 창 기반 상태/로그 확인

## 3. 아키텍처

### 3.1 제품 계층

- UI Shell
  - [`MainWindow.xaml`](/mnt/c/ds/ds2/Apps/CostSim/MainWindow.xaml)
  - [`MainWindow.xaml.cs`](/mnt/c/ds/ds2/Apps/CostSim/MainWindow.xaml.cs)
- UI 보조
  - [`TextPromptDialog.xaml`](/mnt/c/ds/ds2/Apps/CostSim/TextPromptDialog.xaml)
  - [`TextPromptDialog.xaml.cs`](/mnt/c/ds/ds2/Apps/CostSim/TextPromptDialog.xaml.cs)
- Presentation/Service
  - [`OutputPanelBuffer.cs`](/mnt/c/ds/ds2/Apps/CostSim/Presentation/OutputPanelBuffer.cs)
  - [`ThemeManager.cs`](/mnt/c/ds/ds2/Apps/CostSim/Presentation/ThemeManager.cs)
  - [`AppSettingStore.cs`](/mnt/c/ds/ds2/Apps/CostSim/Presentation/AppSettingStore.cs)
  - [`CostSimPathService.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/CostSimPathService.cs)
  - [`CostSimStoreHelper.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/CostSimStoreHelper.cs)
  - [`DemoLibraryBuilder.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/DemoLibraryBuilder.cs)
- View Model 성격의 단순 데이터 객체
  - [`TreeNodeItem.cs`](/mnt/c/ds/ds2/Apps/CostSim/TreeNodeItem.cs)
  - [`LibraryCatalogNode.cs`](/mnt/c/ds/ds2/Apps/CostSim/LibraryCatalogNode.cs)
  - [`WorkCostRow.cs`](/mnt/c/ds/ds2/Apps/CostSim/WorkCostRow.cs)

### 3.2 ds2 재사용 범위

- 모델/직렬화: `Ds2.Core`, `Ds2.Store`
- 편집/히스토리/인과 연결: `Ds2.Editor`
- 시뮬레이션: `Ds2.Runtime.Sim`
- 리포트/Export: `Ds2.Runtime.Sim.Report`
- AASX 변환: `Ds2.Aasx`

## 4. 이번 리팩토링 결과

확장 가능성을 높이기 위해 UI 파일에 몰려 있던 공통 책임을 분리했다.

### 4.1 경로 책임 분리

[`CostSimPathService.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/CostSimPathService.cs)

- 라이브러리 설정 파일 경로 관리
- 기본 라이브러리 루트 경로 관리
- 데모 라이브러리 루트 경로 관리
- `Documents` 대신 `LocalAppData` 기준 경로 정규화
- 접근 거부 폴더를 건너뛰는 파일 열거

### 4.2 출력/상태 책임 분리

[`OutputPanelBuffer.cs`](/mnt/c/ds/ds2/Apps/CostSim/Presentation/OutputPanelBuffer.cs)

- 상태 메시지 보관
- 최근 활동 로그 누적
- Output 창 렌더링 텍스트 생성

### 4.3 DS2 Work 조작 책임 분리

[`CostSimStoreHelper.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/CostSimStoreHelper.cs)

- Work 속성 업데이트
- SequenceOrder 읽기/설정
- Work SimulationProperties 생성/조회
- 공정 원가 합계 계산 반영
- DS2 transaction/mutation 래핑

### 4.4 데모 라이브러리 책임 분리

[`DemoLibraryBuilder.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/DemoLibraryBuilder.cs)

- 데모 시드 데이터 관리
- 데모 JSON 문서 생성
- EV Battery / Door Module / Seat Frame 예제 구성
- 시뮬레이션 의미가 있는 수준의 공정/원가/OEE 입력값 초기화

## 5. 확장 포인트

앞으로 신규 기능은 아래 기준으로 확장한다.

### 5.1 라이브러리 확장

- 라이브러리 경로 정책 변경: `CostSimPathService`
- 신규 import 전략 추가: `MainWindow.xaml.cs` 의 `AppendLibrarySelectionCore`, `RebuildFromLibraryCore`
- 라이브러리 메타데이터 확장: `LibraryCatalogNode`

### 5.2 공정 속성 확장

- 코어 속성 추가: [`Simulation.fs`](/mnt/c/ds/ds2/Solutions/Core/Ds2.Core/SequenceSubmodels/Simulation.fs)
- UI 입력 패널 연결: [`MainWindow.xaml`](/mnt/c/ds/ds2/Apps/CostSim/MainWindow.xaml)
- 저장/계산 반영: [`MainWindow.xaml.cs`](/mnt/c/ds/ds2/Apps/CostSim/MainWindow.xaml.cs), [`CostSimStoreHelper.cs`](/mnt/c/ds/ds2/Apps/CostSim/Services/CostSimStoreHelper.cs)

### 5.3 리포트 확장

- 시뮬레이션 결과 텍스트 형식 변경: `BuildReportText`
- Export 포맷 확장: `Ds2.Runtime.Sim.Report` exporter 사용부
- Excel 컬럼 구조 변경: `ReportService.exportAuto` 기준 exporter 확장

### 5.4 UI 확장

- 리본 그룹 재배치: [`MainWindow.xaml`](/mnt/c/ds/ds2/Apps/CostSim/MainWindow.xaml)
- 우측 패널 탭 확장: `Library / Details`
- Output 창 메시지 포맷 변경: `OutputPanelBuffer`

## 6. 현재 설계 원칙

- 공정 순서는 `Flow` 내 `Work.SequenceOrder`가 단일 진실 원천이다.
- Arrow 연결은 직접 편집보다 트리 순서에서 재생성하는 방식이 우선이다.
- 라이브러리 import는 `append` 와 `rebuild` 를 분리한다.
- 사용자 경로/데모 데이터/출력 포맷은 UI 코드 밖의 별도 클래스에서 관리한다.
- `MainWindow`는 orchestration 역할만 수행하고, 공통 로직은 서비스 클래스로 옮긴다.

## 7. 남은 권장 과제

- `MainWindow.xaml.cs` 를 `Library`, `Simulation`, `Editing` partial 또는 MVVM 단위로 추가 분해
- 라이브러리 스캔/빌드/preview 생성에 대한 단위 테스트 추가
- Excel export 결과 검증용 샘플 테스트 추가
- 데모 라이브러리 생성 기능을 메뉴 명령/초기 샘플 선택과 통합
- `CostSimStoreHelper` 를 더 확장해 flow/system 수준 계산도 서비스로 분리

## 8. 이번 정리에서 삭제한 문서

기존 `CostSim` 제품과 직접 맞지 않는 이전 제안/분석 문서는 제거했다.

- `공법검토App_개발사양서.md`
- `공법검토App_엑셀분석.md`
- `한국단자공업_공법검토시스템_제안서_공식.html`

이 문서는 현재 `CostSim` 코드 구조와 제품 범위를 기준으로 유지한다.
