# ds2 Repository Guidelines

## 솔루션 동기화 규칙

이 저장소에는 전체 솔루션 1개와 앱별 서브 솔루션 3개가 존재함:

- 전체: [solutions/Ds2.sln](solutions/Ds2.sln)
- 서브:
  - [Apps/AasxEditor/AasxEditor.sln](Apps/AasxEditor/AasxEditor.sln)
  - [Apps/DSPilot/DSPilot.sln](Apps/DSPilot/DSPilot.sln)
  - [Apps/Promaker/Promaker.sln](Apps/Promaker/Promaker.sln)

**규칙**: 어느 한쪽 .sln 에 프로젝트를 추가/삭제했다면, 반드시 다른 쪽에도 동일하게 반영할 것.

- 서브 .sln (AasxEditor / DSPilot / Promaker) 변경 → `Ds2.sln` 에도 같은 추가/삭제 반영
- `Ds2.sln` 변경 → 해당 앱의 서브 .sln 에도 같은 추가/삭제 반영

**이유**: `Ds2.sln` 은 전체 빌드/CI 진입점이고 서브 .sln 은 앱별 개발 진입점임. 한쪽만 업데이트하면 빌드 누락, 참조 끊김, 형상관리 불일치가 발생함.

**적용 시점**: `dotnet sln add/remove`, Visual Studio 의 "솔루션에 추가/제거", 신규 `.csproj`/`.fsproj` 생성 직후 등 .sln 파일이 한 번이라도 변경되는 모든 작업.
