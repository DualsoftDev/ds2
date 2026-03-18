# Promaker 다국어 지원 작업 체크리스트

## 📊 전체 진행 상황

- **완료**: MainToolbar (46개 리소스)
- **대기**: Dialogs (13개), Controls (8개), ViewModels (9개)
- **예상 총 리소스**: ~300-350개

---

## Phase 1: Dialog 리소스화 (우선순위: 높음)

### 1. CallCreateDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/CallCreateDialog.xaml`
- **예상 한글 텍스트**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `CallCreateDialogTitle` - "Call 생성"
  - `CallNameLabel` - "Call 이름"
  - `ParentWorkLabel` - "상위 Work"
  - `OkButton` - "확인"
  - `CancelButton` - "취소"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Title, Label, Button 텍스트 리소스화
  - [ ] Strings.resx / Strings.en.resx에 추가
  - [ ] Strings.Designer.cs 업데이트
  - [ ] 빌드 테스트

### 2. ApiCallCreateDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ApiCallCreateDialog.xaml`
- **예상 한글 텍스트**: ~18개
- **예상 소요 시간**: 35분
- **리소스 예시**:
  - `ApiCallCreateDialogTitle` - "API Call 생성"
  - `ApiCallNameLabel` - "API Call 이름"
  - `ApiDefinitionLabel` - "API 정의"
  - `SelectApiDefButton` - "API 선택..."
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Dialog 모든 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 3. ApiDefEditDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ApiDefEditDialog.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `ApiDefEditDialogTitle` - "API 정의 편집"
  - `ApiNameLabel` - "API 이름"
  - `EndpointLabel` - "엔드포인트"
  - `MethodLabel` - "메서드"
  - `HeadersLabel` - "헤더"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 레이블 및 버튼 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 4. ArrowTypeDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ArrowTypeDialog.xaml`
- **예상 한글 텍스트**: ~12개
- **예상 소요 시간**: 25분
- **리소스 예시**:
  - `ArrowTypeDialogTitle` - "연결 타입 선택"
  - `ArrowTypeLabel` - "연결 타입"
  - `NormalArrowType` - "일반"
  - `ConditionalArrowType` - "조건부"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Dialog 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 5. ConditionDropDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ConditionDropDialog.xaml`
- **예상 한글 텍스트**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `ConditionDropDialogTitle` - "조건 삭제"
  - `ConfirmDeleteCondition` - "조건을 삭제하시겠습니까?"
  - `ConditionNameLabel` - "조건 이름"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 확인 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 6. CsvExportDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/CsvExportDialog.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `CsvExportDialogTitle` - "CSV 내보내기"
  - `ExportPathLabel` - "저장 경로"
  - `BrowseButton` - "찾아보기..."
  - `ExportOptionsLabel` - "내보내기 옵션"
  - `IncludeHeadersCheckbox` - "헤더 포함"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 옵션 및 레이블 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 7. CsvImportDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/CsvImportDialog.xaml`
- **예상 한글 텍스트**: ~22개
- **예상 소요 시간**: 45분
- **리소스 예시**:
  - `CsvImportDialogTitle` - "CSV 불러오기"
  - `SourceFileLabel` - "원본 파일"
  - `PreviewLabel` - "미리보기"
  - `ImportOptionsLabel` - "불러오기 옵션"
  - `DelimiterLabel` - "구분자"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 UI 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 8. DurationBatchDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/DurationBatchDialog.xaml`
- **예상 한글 텍스트**: ~18개
- **예상 소요 시간**: 35분
- **리소스 예시**:
  - `DurationBatchDialogTitle` - "Duration 일괄 편집"
  - `TargetSelectionLabel` - "대상 선택"
  - `DurationValueLabel` - "Duration 값"
  - `ApplyToAllCheckbox` - "전체 적용"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Dialog 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 9. IoBatchSettingsDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/IoBatchSettingsDialog.xaml`
- **예상 한글 텍스트**: ~25개
- **예상 소요 시간**: 50분
- **리소스 예시**:
  - `IoBatchDialogTitle` - "I/O 일괄 설정"
  - `InputConditionsLabel` - "입력 조건"
  - `OutputConditionsLabel` - "출력 조건"
  - `AddConditionButton` - "조건 추가"
  - `RemoveConditionButton` - "조건 제거"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 섹션 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 10. MermaidImportDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/MermaidImportDialog.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `MermaidImportDialogTitle` - "Mermaid 불러오기"
  - `MermaidCodeLabel` - "Mermaid 코드"
  - `PreviewLabel` - "미리보기"
  - `ImportButton` - "불러오기"
  - `ValidateButton` - "검증"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Dialog 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 11. ProjectPropertiesDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ProjectPropertiesDialog.xaml`
- **예상 한글 텍스트**: ~30개
- **예상 소요 시간**: 60분
- **리소스 예시**:
  - `ProjectPropertiesDialogTitle` - "프로젝트 속성"
  - `GeneralTabHeader` - "일반"
  - `ProjectNameLabel` - "프로젝트 이름"
  - `DescriptionLabel` - "설명"
  - `AuthorLabel` - "작성자"
  - `VersionLabel` - "버전"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 탭 및 속성 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 12. ValueSpecDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ValueSpecDialog.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `ValueSpecDialogTitle` - "값 설정"
  - `ValueTypeLabel` - "값 타입"
  - `LiteralValueLabel` - "리터럴 값"
  - `PropertyPathLabel` - "속성 경로"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Dialog 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 13. ApiCallSpecDialog.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Dialogs/ApiCallSpecDialog.xaml`
- **예상 한글 텍스트**: ~25개
- **예상 소요 시간**: 50분
- **리소스 예시**:
  - `ApiCallSpecDialogTitle` - "API Call 설정"
  - `RequestHeadersLabel` - "요청 헤더"
  - `RequestBodyLabel` - "요청 본문"
  - `ResponseMappingLabel` - "응답 매핑"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 섹션 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

**Phase 1 예상 총 소요 시간**: 약 8시간

---

## Phase 2: Control 리소스화 (우선순위: 중간)

### 1. PropertyPanel.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/PropertyPanel/PropertyPanel.xaml`
- **예상 한글 텍스트**: ~35개
- **예상 소요 시간**: 70분
- **리소스 예시**:
  - `PropertiesPanelTitle` - "속성"
  - `BasicInfoSection` - "기본 정보"
  - `NameLabel` - "이름"
  - `TypeLabel` - "타입"
  - `DescriptionLabel` - "설명"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 섹션 헤더 및 레이블 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 2. ConditionSectionControl.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/PropertyPanel/ConditionSectionControl.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `ConditionsSectionTitle` - "조건"
  - `InputConditionsLabel` - "입력 조건"
  - `OutputConditionsLabel` - "출력 조건"
  - `AddConditionButton` - "추가"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Control 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 3. ValueSpecEditorControl.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/PropertyPanel/ValueSpecEditorControl.xaml`
- **예상 한글 텍스트**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `ValueTypeLabel` - "값 타입"
  - `ValueLabel` - "값"
  - `EditButton` - "편집..."
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Control 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 4. ExplorerPane.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Shell/ExplorerPane.xaml`
- **예상 한글 텍스트**: ~25개
- **예상 소요 시간**: 50분
- **리소스 예시**:
  - `ExplorerPaneTitle` - "탐색기"
  - `SearchPlaceholder` - "검색..."
  - `SystemsNode` - "Systems"
  - `FlowsNode` - "Flows"
  - `WorksNode` - "Works"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 트리 노드 및 컨텍스트 메뉴 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 5. SimulationPanel.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Simulation/SimulationPanel.xaml`
- **예상 한글 텍스트**: ~30개
- **예상 소요 시간**: 60분
- **리소스 예시**:
  - `SimulationPanelTitle` - "시뮬레이션"
  - `CurrentTimeLabel` - "현재 시간"
  - `EventLogLabel` - "이벤트 로그"
  - `StateLabel` - "상태"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 모든 레이블 및 컨트롤 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 6. GanttChartControl.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Simulation/GanttChartControl.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `GanttChartTitle` - "간트 차트"
  - `TimelineLabel` - "타임라인"
  - `TasksLabel` - "작업"
  - `ZoomInTooltip` - "확대"
  - `ZoomOutTooltip` - "축소"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Chart 관련 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 7. CanvasWorkspace.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Canvas/CanvasWorkspace.xaml`
- **예상 한글 텍스트**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `CanvasTabTitle` - "캔버스"
  - `ZoomLabel` - "배율"
  - `GridLabel` - "그리드"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] Control 텍스트 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 8. EditorCanvas.xaml
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Canvas/EditorCanvas.xaml`
- **예상 한글 텍스트**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `SelectAllTooltip` - "전체 선택"
  - `CopyTooltip` - "복사"
  - `PasteTooltip` - "붙여넣기"
  - `CutTooltip` - "잘라내기"
- **작업 내용**:
  - [ ] xmlns:res 추가
  - [ ] 컨텍스트 메뉴 및 툴팁 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

**Phase 2 예상 총 소요 시간**: 약 6시간

---

## Phase 3: ViewModel 메시지 리소스화 (우선순위: 중간)

### 1. FileCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/FileCommands.cs`
- **예상 한글 문자열**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `FileFilterDsProj` - "Ds2 프로젝트 파일 (*.dsproj)|*.dsproj"
  - `FileOpenSuccess` - "파일을 성공적으로 열었습니다."
  - `FileSaveSuccess` - "파일을 성공적으로 저장했습니다."
  - `InvalidFileFormat` - "잘못된 파일 형식입니다."
- **작업 내용**:
  - [ ] using Promaker.Resources 추가
  - [ ] Dialog 메시지를 Strings.XXX로 변경
  - [ ] 리소스 파일 업데이트
  - [ ] Strings.Designer.cs 업데이트
  - [ ] 빌드 테스트

### 2. NodeCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/NodeCommands.cs`
- **예상 한글 문자열**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `NodeDeleteConfirm` - "선택한 노드를 삭제하시겠습니까?"
  - `NodeDeleteSuccess` - "노드가 삭제되었습니다."
  - `NodeCopySuccess` - "노드가 복사되었습니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 3. MermaidImportCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/MermaidImportCommands.cs`
- **예상 한글 문자열**: ~12개
- **예상 소요 시간**: 25분
- **리소스 예시**:
  - `MermaidParseError` - "Mermaid 구문 분석 실패"
  - `MermaidImportSuccess` - "Mermaid 다이어그램을 성공적으로 불러왔습니다."
  - `InvalidMermaidSyntax` - "잘못된 Mermaid 구문입니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 에러 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 4. MainViewModel.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs`
- **예상 한글 문자열**: ~10개
- **예상 소요 시간**: 20분
- **리소스 예시**:
  - `ApplicationError` - "애플리케이션 오류"
  - `UnsavedChangesWarning` - "저장되지 않은 변경 사항이 있습니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 5. IoBatchCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/IoBatchCommands.cs`
- **예상 한글 문자열**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `IoBatchApplySuccess` - "I/O 일괄 설정이 적용되었습니다."
  - `NoTargetsSelected` - "대상이 선택되지 않았습니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 6. DurationBatchCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/DurationBatchCommands.cs`
- **예상 한글 문자열**: ~12개
- **예상 소요 시간**: 25분
- **리소스 예시**:
  - `DurationBatchApplySuccess` - "Duration 일괄 편집이 적용되었습니다."
  - `InvalidDurationValue` - "잘못된 Duration 값입니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 7. EditorGuards.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/EditorGuards.cs`
- **예상 한글 문자열**: ~10개
- **예상 소요 시간**: 20분
- **리소스 예시**:
  - `InvalidOperationError` - "잘못된 작업입니다."
  - `CannotModifyLockedEntity` - "잠긴 엔티티는 수정할 수 없습니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 에러 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 8. CsvCommands.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/Shell/CsvCommands.cs`
- **예상 한글 문자열**: ~15개
- **예상 소요 시간**: 30분
- **리소스 예시**:
  - `CsvExportSuccess` - "CSV 파일을 성공적으로 내보냈습니다."
  - `CsvImportSuccess` - "CSV 파일을 성공적으로 불러왔습니다."
  - `CsvParseError` - "CSV 파일 분석 실패"
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

### 9. CallPanel.ApiCalls.cs & CallPanel.Conditions.cs
- **경로**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/ViewModels/PropertyPanel/`
- **예상 한글 문자열**: ~20개
- **예상 소요 시간**: 40분
- **리소스 예시**:
  - `ApiCallAddSuccess` - "API Call이 추가되었습니다."
  - `ConditionAddSuccess` - "조건이 추가되었습니다."
  - `InvalidConditionError` - "잘못된 조건입니다."
- **작업 내용**:
  - [ ] using 추가
  - [ ] 메시지 리소스화
  - [ ] 리소스 파일 업데이트
  - [ ] 빌드 테스트

**Phase 3 예상 총 소요 시간**: 약 4시간

---

## Phase 4: 동적 바인딩 지원 (우선순위: 낮음)

### 1. Dialog Title 동적 변경
- **예상 소요 시간**: 2시간
- **작업 내용**:
  - [ ] INotifyPropertyChanged 패턴 적용
  - [ ] Dialog ViewModel에 Title 속성 추가
  - [ ] 언어 변경 시 PropertyChanged 이벤트 발생
  - [ ] 테스트

### 2. 언어 전환 시 열린 창 갱신
- **예상 소요 시간**: 2시간
- **작업 내용**:
  - [ ] LanguageManager에 언어 변경 이벤트 추가
  - [ ] 모든 ViewModel이 이벤트 구독
  - [ ] 언어 변경 시 UI 업데이트 로직 구현
  - [ ] 테스트

### 3. ViewModel 속성 변경 알림
- **예상 소요 시간**: 2시간
- **작업 내용**:
  - [ ] 기존 ViewModel에 동적 바인딩 추가
  - [ ] ObservableCollection 사용하여 동적 업데이트
  - [ ] 테스트

**Phase 4 예상 총 소요 시간**: 약 6시간

---

## Phase 5: 최종 검증

### 1. 모든 UI 한글 제거 확인
- **예상 소요 시간**: 1시간
- **작업 내용**:
  - [ ] grep으로 모든 XAML 파일 검색
  - [ ] grep으로 모든 C# 파일 검색 (주석 제외)
  - [ ] 누락된 텍스트 리소스화

### 2. 빌드 에러 0개 확인
- **예상 소요 시간**: 30분
- **작업 내용**:
  - [ ] dotnet build 실행
  - [ ] 모든 에러 수정
  - [ ] 경고 검토

### 3. 언어 전환 테스트
- **예상 소요 시간**: 1시간
- **작업 내용**:
  - [ ] 한글 → 영어 전환 테스트
  - [ ] 영어 → 한글 전환 테스트
  - [ ] 모든 Dialog 열어서 확인
  - [ ] 모든 메뉴 및 버튼 확인

### 4. 에러 메시지 테스트
- **예상 소요 시간**: 1시간
- **작업 내용**:
  - [ ] 의도적으로 에러 발생시키기
  - [ ] 에러 메시지 언어 확인
  - [ ] 확인/경고/정보 다이얼로그 언어 확인

**Phase 5 예상 총 소요 시간**: 약 3.5시간

---

## 📝 리소스 네이밍 컨벤션 요약

### 패턴별 예시

| 패턴 | 예시 | 설명 |
|------|------|------|
| `{ComponentName}` | `Project`, `Save`, `Delete` | 단순 명사형 텍스트 |
| `{ComponentName}Label` | `ProjectNameLabel`, `DurationLabel` | 긴 레이블 |
| `{Action}` | `Save`, `Cancel`, `Apply` | 버튼 텍스트 (동사형) |
| `{ComponentName}Tooltip` | `SaveTooltip`, `DeleteTooltip` | 툴팁 |
| `{Action}{Result}` | `FileSaveFailed`, `FileOpenFailed` | 에러/경고 메시지 |
| `Confirm{Action}` | `ConfirmSaveChanges`, `ConfirmDelete` | 확인 메시지 |
| `{DialogName}Title` | `CallCreateDialogTitle` | Dialog Title |

---

## 🚀 작업 재개 방법

1. **현재 상태 확인**
   - LOCALIZATION_GUIDE.md 읽기
   - MainToolbar.xaml 예제 확인

2. **Phase 선택**
   - Phase 1부터 시작 권장 (Dialog가 가장 많이 사용됨)

3. **파일별 작업 순서**
   - 한글 텍스트 추출 및 리스트업
   - Strings.resx / Strings.en.resx에 추가
   - Strings.Designer.cs에 public static 속성 추가
   - XAML/C# 코드에서 리소스 참조로 변경
   - 빌드 & 테스트

4. **체크리스트 업데이트**
   - 각 작업 완료 시 이 문서에서 [ ] → [x]로 변경

5. **언어 전환 버튼 활성화**
   - 모든 작업 완료 후 MainToolbar.xaml에서 `Visibility="Collapsed"` 제거

---

## 📊 예상 총 작업 시간

| Phase | 소요 시간 | 비고 |
|-------|----------|------|
| Phase 1 (Dialogs) | 약 8시간 | 13개 Dialog |
| Phase 2 (Controls) | 약 6시간 | 8개 Control |
| Phase 3 (ViewModels) | 약 4시간 | 9개 ViewModel |
| Phase 4 (동적 바인딩) | 약 6시간 | 선택사항 |
| Phase 5 (검증) | 약 3.5시간 | 최종 테스트 |
| **총 예상 시간** | **약 27.5시간** | Phase 4 포함 시 |

---

## 📞 참고 문서

- **구현 가이드**: `/mnt/c/ds/ds2/Apps/Promaker/LOCALIZATION_GUIDE.md`
- **완료 예제**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Controls/Shell/MainToolbar.xaml`
- **LanguageManager**: `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Presentation/LanguageManager.cs`
- **리소스 파일**:
  - `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Resources/Strings.resx`
  - `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Resources/Strings.en.resx`
  - `/mnt/c/ds/ds2/Apps/Promaker/Promaker/Resources/Strings.Designer.cs`

---

**작업 시작 시 이 체크리스트를 참고하여 순차적으로 진행하세요!**
