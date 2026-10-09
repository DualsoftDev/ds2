# DualSoft-DS2

DS2 라이브러리를 한 덩이로 묶은 NuGet 패키지 — `Ds2.Core`(도메인 모델·DsStore) + `Ds2.Mermaid`(다이어그램 import/export) + `Ds2.CSV`(CSV import/export) + `Ds2.Aasx`(AASX import/export) + `Ds2.Text`(DS2 Text v4 writer) + `Ds2.Runtime`(시뮬레이션 엔진) + `Ds2.Runtime.Report`(리포트 생성).

```
dotnet add package DualSoft-DS2
```

설치하면 `Ds2.Core.dll` / `Ds2.Mermaid.dll` / `Ds2.CSV.dll` / `Ds2.Aasx.dll` / `Ds2.Text.dll` / `Ds2.Runtime.dll` / `Ds2.Runtime.Report.dll` 이 함께 들어오고, 코드에서는 `open Ds2.Core` / `open Ds2.Mermaid` / `open Ds2.CSV` / `open Ds2.Aasx` / `open Ds2.Runtime.Engine` 그대로 사용한다. (패키지 이름과 네임스페이스는 별개 — 간판만 `DualSoft-DS2`.)

## C# Tutorial / Samples

NuGet 소비자 기준 C# 튜토리얼은 `samples/CSharp`에 있다. 저장소 루트에서:

```bash
dotnet run --project Solutions/Pack/samples/CSharp
```

기본은 nuget.org 의 `DualSoft-DS2` 패키지를 받아 쓴다(`DualSoftDs2Version` 속성으로 버전 지정). 패키지를 올리기 전에 이 저장소 소스로 검증하려면 `Solutions/Pack/ds2` 자리에 ds2 체크아웃(또는 링크)을 두고 `-p:UseLocalDs2Projects=true` 를 붙인다.

예제 흐름:

- Mermaid 파일을 `DsStore`로 import
- Core Store 조회, JSON save/load
- CSV import 및 PLC CSV → AASX facade 호출
- AASX export
- `SimIndexModule.build`로 Runtime index 구성
- `EventDrivenEngine`으로 시뮬레이션 실행
- `ReportService`로 HTML/CSV 리포트 생성

서버 호스트(SignalR Hub · PLC 게이트웨이 · OPC UA)는 배포용 레이어라 이 패키지에 포함하지 않는다.

## 구조

- 패키지 프로젝트는 [`DualSoft-DS2.csproj`](DualSoft-DS2.csproj) 한 파일이다. 코드 없이 같은 저장소의 라이브러리 프로젝트를 `PrivateAssets="all"` 로 참조해 산출 DLL 만 `lib/net9.0/` 에 모은다.
- 받는 쪽이 필요로 하는 외부 의존은 넷 — `FSharp.Core` · `AasCore.Aas3_1` · `ClosedXML` · `log4net`. 프로젝트 참조를 숨겼으므로 이쪽은 csproj 에 직접 적는다.
- 버전은 csproj 의 `<Version>` 이 다음 후보 번호이고, 올릴 때 `-p:Version` 으로 덮어쓴다.
- License: Apache-2.0 (ds2 저장소 따름).

## 빌드 / 배포

```bash
dotnet pack Solutions/Pack/DualSoft-DS2.csproj -c Release -p:Version=0.1.24
# 산출물: Solutions/Pack/bin/Release/DualSoft-DS2.0.1.24.nupkg

dotnet nuget push Solutions/Pack/bin/Release/DualSoft-DS2.0.1.24.nupkg \
  --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
```

push 키는 저장소에 저장하지 않는다. 만든 패키지가 맞는지는 `lib/net9.0/` 의 파일 목록을 이전 게시본과 맞춰 보는 것으로 확인한다.

## dll 추가

`DualSoft-DS2.csproj` 의 `ProjectReference` 묶음에 한 줄 추가:

```xml
<ProjectReference Include="..\Convert\Ds2.Xxx\Ds2.Xxx.fsproj" PrivateAssets="all" />
```

`Ds2.` 로 시작하는 산출 DLL 은 `IncludeReferencedAssemblies` 타깃이 자동으로 `lib/` 에 담는다.
