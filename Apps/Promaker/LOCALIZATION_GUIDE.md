# Promaker 다국어 지원 구현 가이드

## 📌 개요
Promaker 애플리케이션의 한국어/영어 다국어 지원 기능 구현 가이드입니다.
현재 MainToolbar가 완료되었으며, 나머지 UI 컴포넌트는 이 가이드를 참고하여 순차적으로 작업할 수 있습니다.

## ✅ 현재 완료 상태

### 완료된 컴포넌트
- **LanguageManager.cs**: 언어 전환 로직 완료
- **Strings.resx / Strings.en.resx**: 리소스 파일 구조 완료
- **Strings.Designer.cs**: Public 접근자로 설정 완료
- **MainToolbar.xaml**: 전체 리소스화 완료 (46개 리소스)

### 대기 중인 컴포넌트
- **Dialogs** (13개): ~1,710줄의 한글 텍스트
- **Controls** (8개): ~500줄의 한글 텍스트
- **ViewModels** (9개): ~1,121개 한글 문자열

## 🏗️ 아키텍처

### 1. LanguageManager
- **위치**: `/Apps/Promaker/Promaker/Presentation/LanguageManager.cs`
- **기능**: 언어 전환 및 CultureInfo 설정
- **언어 저장**: `%AppData%/Promaker/language.txt`

```csharp
// 언어 전환
LanguageManager.ToggleLanguage();

// 특정 언어 설정
LanguageManager.ApplyLanguage(AppLanguage.Korean);
LanguageManager.ApplyLanguage(AppLanguage.English);
```

### 2. 리소스 파일
- **Strings.resx**: 한국어 (기본)
- **Strings.en.resx**: 영어
- **Strings.Designer.cs**: 자동 생성 (수동 관리 필요)

### 3. UI 바인딩
- **XAML**: `{x:Static res:Strings.XXX}`
- **C#**: `Strings.XXX`

## 🛠️ 구현 방법

### XAML 파일 리소스화

#### 1단계: xmlns 추가
```xaml
<Window ...
        xmlns:res="clr-namespace:Promaker.Resources">
```

#### 2단계: 정적 바인딩 사용
```xaml
<!-- 기존 -->
<TextBlock Text="프로젝트"/>
<Button Content="저장"/>
<Window Title="설정"/>

<!-- 리소스화 후 -->
<TextBlock Text="{x:Static res:Strings.Project}"/>
<Button Content="{x:Static res:Strings.Save}"/>
<Window Title="{x:Static res:Strings.Settings}"/>
```

#### 3단계: ToolTip도 리소스화
```xaml
<!-- 기존 -->
<Button ToolTip="저장 (Ctrl+S)"/>

<!-- 리소스화 후 -->
<Button ToolTip="{x:Static res:Strings.SaveTooltip}"/>
```

### C# 코드 리소스화

#### 1단계: using 추가
```csharp
using Promaker.Resources;
```

#### 2단계: 문자열 리소스 사용
```csharp
// 기존
_dialogService.ShowWarning("파일 저장에 실패했습니다.");

// 리소스화 후
_dialogService.ShowWarning(Strings.FileSaveFailed);
```

### 리소스 파일에 추가

#### Strings.resx (한국어)
```xml
<data name="Project" xml:space="preserve">
  <value>프로젝트</value>
</data>
<data name="Save" xml:space="preserve">
  <value>저장</value>
</data>
```

#### Strings.en.resx (영어)
```xml
<data name="Project" xml:space="preserve">
  <value>Project</value>
</data>
<data name="Save" xml:space="preserve">
  <value>Save</value>
</data>
```

#### Strings.Designer.cs 업데이트
```csharp
public static string Project {
    get {
        return ResourceManager.GetString("Project", resourceCulture);
    }
}

public static string Save {
    get {
        return ResourceManager.GetString("Save", resourceCulture);
    }
}
```

⚠️ **중요**: Strings.Designer.cs는 자동 생성되지 않으므로 수동으로 추가해야 합니다!

## 📋 작업 체크리스트

### Phase 1: Dialog 리소스화 (우선순위: 높음)
- [ ] CallCreateDialog.xaml
- [ ] ApiCallCreateDialog.xaml
- [ ] ApiDefEditDialog.xaml
- [ ] ArrowTypeDialog.xaml
- [ ] ConditionDropDialog.xaml
- [ ] CsvExportDialog.xaml
- [ ] CsvImportDialog.xaml
- [ ] DurationBatchDialog.xaml
- [ ] IoBatchSettingsDialog.xaml
- [ ] MermaidImportDialog.xaml
- [ ] ProjectPropertiesDialog.xaml
- [ ] ValueSpecDialog.xaml
- [ ] ApiCallSpecDialog.xaml

### Phase 2: Control 리소스화 (우선순위: 중간)
- [ ] PropertyPanel.xaml
- [ ] ConditionSectionControl.xaml
- [ ] ValueSpecEditorControl.xaml
- [ ] ExplorerPane.xaml
- [ ] SimulationPanel.xaml
- [ ] GanttChartControl.xaml
- [ ] CanvasWorkspace.xaml
- [ ] EditorCanvas.xaml

### Phase 3: ViewModel 메시지 리소스화 (우선순위: 중간)
- [ ] FileCommands.cs (8개 Dialog 호출)
- [ ] NodeCreationViewModel.cs (6개)
- [ ] NodeCommands.cs (3개)
- [ ] MermaidImportCommands.cs (2개)
- [ ] MainViewModel.cs (2개)
- [ ] IoBatchCommands.cs
- [ ] DurationBatchCommands.cs
- [ ] EditorGuards.cs

### Phase 4: 동적 바인딩 지원 (우선순위: 낮음)
- [ ] Dialog Title 동적 변경
- [ ] 언어 전환 시 열린 창 갱신
- [ ] ViewModel 속성 변경 알림

### Phase 5: 최종 검증
- [ ] 모든 UI 한글 제거 확인
- [ ] 빌드 에러 0개 확인
- [ ] 언어 전환 테스트
- [ ] 에러 메시지 테스트

## 📝 리소스 네이밍 컨벤션

### 일반 텍스트
- **패턴**: `{ComponentName}` (명사형)
- **예시**: `Project`, `Save`, `Delete`, `Settings`

### 긴 레이블/설명
- **패턴**: `{ComponentName}Label` 또는 `{Feature}Description`
- **예시**: `DevicesAliasLabel`, `CallFormatDescription`

### 버튼 텍스트
- **패턴**: `{Action}` (동사형)
- **예시**: `Save`, `Cancel`, `Apply`, `Browse`

### 툴팁
- **패턴**: `{ComponentName}Tooltip`
- **예시**: `SaveTooltip`, `DeleteTooltip`, `AutoLayoutTooltip`

### 에러/경고 메시지
- **패턴**: `{Action}{Result}` 또는 `{Feature}Error`
- **예시**: `FileSaveFailed`, `FileOpenFailed`, `InvalidInputError`

### 확인 메시지
- **패턴**: `Confirm{Action}`
- **예시**: `ConfirmSaveChanges`, `ConfirmDelete`

### Dialog Title
- **패턴**: `{DialogName}Title`
- **예시**: `CallCreateDialogTitle`, `ProjectPropertiesTitle`

## 🔍 작업 팁

### 1. 한글 텍스트 검색
```bash
# XAML 파일에서 한글 검색
grep -n "[\u3131-\uD79D]" *.xaml

# C# 파일에서 한글 검색 (주석 제외)
grep -n "[\u3131-\uD79D]" *.cs | grep -v "///"
```

### 2. 중복 리소스 확인
- 비슷한 텍스트는 하나의 리소스로 통합
- 예: "저장", "파일 저장" → `Save`, `SaveFile`

### 3. 컨텍스트 유지
- 같은 단어라도 컨텍스트가 다르면 별도 리소스
- 예: "실행" (버튼) vs "실행 옵션" (그룹 레이블)

### 4. 단계별 빌드
- 파일 1-2개 작업 후 빌드 테스트
- Designer.cs 업데이트 확인

## 🚀 재개 방법

### 언어 전환 버튼 활성화
`MainToolbar.xaml`에서 해당 버튼의 `Visibility="Collapsed"` 제거

```xaml
<!-- 현재 (숨김) -->
<Button ... Visibility="Collapsed">

<!-- 활성화 후 -->
<Button ...>
```

### 작업 재개 순서
1. LOCALIZATION_TODO.md에서 다음 작업 확인
2. 해당 파일의 한글 텍스트 추출
3. Strings.resx / Strings.en.resx에 추가
4. Strings.Designer.cs 업데이트
5. XAML/C# 코드 수정
6. 빌드 & 테스트
7. 다음 파일로 이동

## 📚 참고 자료

### 완료된 예제
- **MainToolbar.xaml**: 완벽한 리소스화 예제
- **LanguageManager.cs**: 언어 전환 로직
- **MainViewModel.cs**: ToggleLanguage 구현

### .NET 리소스 파일 문서
- [Microsoft Docs - 리소스 파일](https://docs.microsoft.com/ko-kr/dotnet/framework/resources/)
- [WPF StaticExtension](https://docs.microsoft.com/ko-kr/dotnet/api/system.windows.markup.staticextension)

## 🐛 트러블슈팅

### Q: "정적 멤버를 찾을 수 없습니다" 에러
**A**: Strings.Designer.cs에 해당 속성이 `public static`으로 추가되었는지 확인

### Q: 언어가 전환되지 않음
**A**: `Strings.Culture` 설정 확인 및 `{x:Static}` 바인딩 사용 확인

### Q: 빌드는 되는데 런타임 에러
**A**: xmlns:res가 올바른지, 리소스 이름 오타 확인

### Q: Designer.cs가 자동 생성 안됨
**A**: Visual Studio에서는 자동 생성되지만, dotnet CLI에서는 수동 추가 필요

---

## 📞 문의
작업 중 문제가 발생하면 이 가이드를 참고하거나 완료된 MainToolbar.xaml 예제를 확인하세요.
