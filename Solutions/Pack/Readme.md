# DualSoft-DS2

DS2 라이브러리를 한 덩이로 묶은 NuGet 패키지 — `Ds2.Core`(도메인 모델·DsStore) + `Ds2.Mermaid`(다이어그램 import/export) + `Ds2.CSV`(CSV import/export) + `Ds2.Aasx`(AASX import/export) + `Ds2.Runtime`(시뮬레이션 엔진) + `Ds2.Runtime.Report`(리포트 생성).

```
dotnet add package DualSoft-DS2
```

설치하면 `Ds2.Core.dll` / `Ds2.Mermaid.dll` / `Ds2.CSV.dll` / `Ds2.Aasx.dll` / `Ds2.Runtime.dll` / `Ds2.Runtime.Report.dll` 이 함께 들어오고, 코드에서는 `open Ds2.Core` / `open Ds2.Mermaid` / `open Ds2.CSV` / `open Ds2.Aasx` / `open Ds2.Runtime.Engine` 그대로 사용한다. (패키지 이름과 네임스페이스는 별개 — 간판만 `DualSoft-DS2`.)

## C# Tutorial / Samples

NuGet 소비자 기준 C# 튜토리얼은 `Samples/CSharp`에 있다.

```bash
dotnet run --project Samples/CSharp
```

패키지가 아직 배포되기 전 로컬 submodule 소스로 검증할 때:

```bash
dotnet run --project Samples/CSharp -p:UseLocalDs2Projects=true
```

예제 흐름:

- Mermaid 파일을 `DsStore`로 import
- Core Store 조회, JSON save/load
- CSV import 및 PLC CSV → AASX facade 호출
- AASX export
- `SimIndexModule.build`로 Runtime index 구성
- `EventDrivenEngine`으로 시뮬레이션 실행
- `ReportService`로 HTML/CSV 리포트 생성

`Ds2.Runtime.Remote`와 `Ds2.Backend.Runtime`은 SignalR/서버 호스트 구성이 섞인 배포용 레이어라 이 기본 패키지에는 포함하지 않는다.

## 구조

- 소스는 직접 두지 않고 **git submodule `ds2`** (github.com/DualsoftDev/ds2) 를 빌드해 포함한다.
- 버전 SSOT = `ds2/Apps/Promaker/BuildVersion.txt` (Promaker 인스톨러와 동일 버전).
- License: Apache-2.0 (ds2 리포 따름).

## 빌드 / 배포

```bash
make init        # 클론 직후 1회 — submodule(ds2) 받아오기
make pack        # nupkgs/DualSoft-DS2.<버전>.nupkg 생성
make publish     # nuget.org 푸시 (NUGET_API_KEY 환경변수 필요)
```

push 키는 리포에 저장하지 않는다:
```bash
export NUGET_API_KEY=<nuget.org API 키>
make publish
```

특정 버전으로 강제: `make pack VER=0.1.13`

## dll 추가

`DualSoft-DS2.fsproj` 에 두 줄만 추가:
1. `<ProjectReference Include="ds2/Solutions/.../Ds2.Xxx.fsproj" PrivateAssets="all" />`
2. `<BundledDll Include="Ds2.Xxx" />`
