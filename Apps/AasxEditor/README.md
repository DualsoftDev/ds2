# AASX JSON Editor

AASX(Asset Administration Shell) 파일을 웹 브라우저에서 열고, 탐색하고, 편집할 수 있는 Blazor Server 웹 애플리케이션입니다.

## 주요 기능

- **AASX 파일 열기/추가**: 파일 선택 또는 드래그앤드롭으로 AASX 파일 로드 (다중 파일 지원)
- **JSON 코드 편집**: Monaco Editor 기반 JSON 뷰어/에디터 (정렬, 검증)
- **AAS 트리 탐색**: 좌측 트리뷰에서 AAS 구조 계층 탐색
- **Explorer 뷰**: 카드 기반 시각적 탐색 (breadcrumb 네비게이션)
- **속성 편집**: 우측 패널에서 노드 속성 직접 편집 후 JSON 반영
- **검색 및 일괄 편집**: idShort, value, semanticId 검색 + 검색 결과 일괄 값 변경
- **저장/다른이름저장**: 편집된 환경을 AASX 파일로 다운로드
- **세션 복원**: 브라우저 새로고침 시 SQLite에서 마지막 상태 복원 (프로그램 재시작 시 초기화)

## 기술 스택

| 구분 | 기술 |
|------|------|
| 프레임워크 | .NET 9, Blazor Server (InteractiveServer) |
| 에디터 | Monaco Editor (CDN) |
| DB | SQLite (Microsoft.Data.Sqlite) |
| AAS 라이브러리 | AasCore.Aas3_0 |
| AASX 변환 | Ds2.Aasx (프로젝트 참조) |

## 프로젝트 구조

```
AasxEditor/
  Components/
    Pages/
      Home.razor          - 마크업 (툴바, 검색, 3패널 레이아웃, 모달)
      Home.razor.cs       - 상태 필드, DI, 공용 헬퍼, 라이프사이클
      Home.FileIO.cs      - 파일 열기/추가/저장/복원/툴바 액션
      Home.Explorer.cs    - 트리/익스플로러/검색/속성 편집
      Home.BatchEdit.cs   - 일괄 편집 (Environment 직접 수정)
      Home.DragDrop.cs    - 드래그앤드롭 (InputFile 주입 방식)
    Layout/
      MainLayout.razor    - 앱 레이아웃 셸
  Models/
    AasEntity.cs          - AasxFileRecord, AasEntityRecord, AasSearchQuery
    AasTreeNode.cs        - 트리 노드 모델
  Services/
    IAasMetadataStore.cs  - 메타데이터 저장소 인터페이스
    SqliteMetadataStore.cs - SQLite 구현
    AasxConverterService.cs - AASX <-> JSON 변환
    AasTreeBuilderService.cs - Environment -> 트리 노드 변환
    AasEntityExtractor.cs - Environment -> 검색용 엔티티 추출
  wwwroot/
    js/monaco-interop.js  - Monaco/DropZone/ResizeHandle JS 모듈
    app.css               - 전체 스타일 (Bootstrap과 공존)
  Program.cs              - DI 등록 및 앱 설정
samples/                  - 테스트용 AASX 샘플 파일
```

## 실행

```bash
cd Apps/AasxEditor
dotnet run --project AasxEditor
```

기본 URL: `https://localhost:5001` (launchSettings.json에 따라 다를 수 있음)

## 빌드

```bash
dotnet build AasxEditor/AasxEditor.csproj
```

## 아키텍처 노트

### Blazor Server + JS Interop 제약

Blazor Server는 SignalR을 통해 서버와 통신하므로:
- **모달이 열린 상태에서 `JS.InvokeAsync` 호출하면 데드락** 발생 가능. 모달을 먼저 닫은 후 JS interop 호출.
- **SignalR 메시지 크기 제한 (기본 32KB)**: 큰 바이너리를 `JS.InvokeAsync<byte[]>`로 전달하면 실패. 드래그앤드롭은 `DataTransfer` API로 `InputFile`에 주입하여 Blazor 내장 스트리밍 활용.
- **Bootstrap CSS 충돌 주의**: `.modal-dialog` 등 Bootstrap 클래스명을 사용할 경우 `pointer-events: none` 등이 적용될 수 있으므로 app.css에서 명시적으로 덮어씌움.

### 드래그앤드롭 구조

JS `drop` 이벤트에서 File 객체를 보관하고, 모달에서 "새로 열기/추가" 선택 시 `DataTransfer`를 이용하여 기존 `InputFile`의 `<input>` 요소에 파일을 주입합니다. 이를 통해 `OnFileOpen`/`OnFileAdd` 핸들러를 재사용하며, SignalR 메시지 크기 제한을 우회합니다.

### 세션 관리

- `static bool _sessionStarted`로 프로세스 재시작과 브라우저 새로고침을 구분
- 재시작: DB 초기화 (빈 상태)
- 새로고침: SQLite에서 마지막 파일 복원

### Partial Class 분리

`Home` 페이지는 책임별로 6개 파일로 분리:
- `.razor` - Razor 마크업 + `RenderTreeNode` (빌더 컨텍스트 필요)
- `.razor.cs` - 공유 상태/DI/헬퍼(`SyncJsonToEditorAsync`, `RebuildTree`, `ApplyEnvironmentAsync`)
- `.FileIO.cs` - 파일 I/O (InputFile, 저장, DB 복원)
- `.Explorer.cs` - 트리 탐색/검색/속성 편집
- `.BatchEdit.cs` - 일괄 편집 (Environment 직접 변경 -> JSON 재생성)
- `.DragDrop.cs` - 드래그앤드롭 (JSInvokable + InputFile 주입)
