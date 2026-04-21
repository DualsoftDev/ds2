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

코어(RCL)와 두 개의 호스트(Web / Desktop)로 분리된 구성입니다. 코어에 실질 코드의 대부분이 있고, 호스트는 각각 얇은 래퍼입니다.

```
Apps/AasxEditor/
  AasxEditor.Core/          ← [RCL] 공유 코어 (90%+ 코드)
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
      Routes.razor          - 라우터 (AppAssembly = Core)
      _Imports.razor
    Models/                 - AasEntity, AasTreeNode
    Services/               - IAasMetadataStore, SqliteMetadataStore, AasxConverterService,
                              AasTreeBuilderService, AasEntityExtractor, CircuitTracker, ...
    wwwroot/                - js/monaco-interop.js, app.css, favicon, lib/bootstrap
    CoreAssemblyMarker.cs   - Router AppAssembly 해석용 마커

  AasxEditor.Web/           ← Blazor Server 호스트 (IIS 배포 / SaaS)
    Program.cs              - DI 등록, Interactive Server 렌더 모드
    Components/App.razor    - 호스트 HTML (blazor.web.js 로드, @rendermode="InteractiveServer")
    appsettings.json, Properties/launchSettings.json

  AasxEditor.Desktop/       ← WPF + BlazorWebView 호스트 (데스크톱 배포)
    App.xaml / .xaml.cs     - WPF 진입점, DI 등록 (DB는 LocalAppData에)
    MainWindow.xaml / .xaml.cs - BlazorWebView 창
    wwwroot/index.html      - 호스트 HTML (blazor.webview.js 로드)

samples/                    - 테스트용 AASX 샘플 파일
```

## 실행

### Web (Blazor Server)
```bash
cd Apps/AasxEditor
dotnet run --project AasxEditor.Web
```
기본 URL: `http://localhost:5236` (launchSettings.json 참조)

### Desktop (WPF BlazorWebView, Windows 전용)
```bash
cd Apps/AasxEditor
dotnet run --project AasxEditor.Desktop
```
SQLite DB는 `%LocalAppData%\AasxEditor\aas_metadata.db`에 생성됩니다.

## 빌드

```bash
dotnet build AasxEditor.sln
```

## 아키텍처 노트

### 코어/호스트 분리 (RCL 패턴)

Blazor 컴포넌트·서비스·모델·정적 자산은 모두 `AasxEditor.Core` RCL에 있고, 두 호스트가 이를 참조합니다. 같은 기능이 **Web(SignalR 기반)** 과 **Desktop(InProc WebView)** 양쪽에서 동일하게 동작합니다.

- 렌더 모드는 호스트에서 결정: Web의 `App.razor`가 `<Routes @rendermode="InteractiveServer"/>`를 걸고, Desktop은 `BlazorWebView`가 InProc로 처리 (rendermode 불필요).
- 코어의 정적 자산은 `_content/AasxEditor.Core/...` 경로로 서빙됩니다.
- `CircuitTracker`/`ClientCount` 계열은 Web에서만 의미 있는 다중 클라이언트 기능이지만, Desktop에서도 단일 "세션" 하나로 동작하며 부작용은 없습니다.
- SQLite 위치는 Web=ContentRoot, Desktop=`%LocalAppData%\AasxEditor`.

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
