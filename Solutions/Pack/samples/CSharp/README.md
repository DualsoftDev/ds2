# C# Tutorial

이 예제는 패키지 사용자가 내부 F# 생성자에 의존하지 않고 공개 API만으로 DS2 모델을 만들고 Runtime까지 연결하는 흐름을 보여줍니다. NuGet에 포함되는 DLL(`Ds2.Core`, `Ds2.Mermaid`, `Ds2.CSV`, `Ds2.Aasx`, `Ds2.Runtime`, `Ds2.Runtime.Report`)을 기준으로 구성했습니다.

```bash
dotnet run --project Samples/CSharp
```

로컬 submodule 소스로 먼저 검증하려면:

```bash
dotnet run --project Samples/CSharp -p:UseLocalDs2Projects=true
```

## 단계별 API

1. `Ds2.Mermaid.MermaidImporter.loadProjectFromFile`: Mermaid -> `DsStore`
2. `Ds2.Core.Store.DsStore`: Store 조회, JSON save/load
3. `Ds2.CSV.CsvImporter`: CSV -> `DsStore`
4. `Ds2.Aasx.AasxExporter`: `DsStore` -> AASX
5. `Ds2.Aasx.PlcAasxFacade`: PLC CSV 문자열 -> AASX bytes
6. `Ds2.Runtime.Engine.Core.SimIndexModule.build`: Runtime index 구성
7. `Ds2.Runtime.Engine.EventDrivenEngine`: 시뮬레이션 실행
8. `Ds2.Runtime.Report.ReportService`: 상태 변경 기록 -> HTML/CSV 리포트
