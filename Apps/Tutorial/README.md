# Ds2 C# Tutorial

`Ds2.Tutorial`은 DS2 소스 레벨 C# 튜토리얼입니다. Core 모델 생성부터 변환, Runtime 시뮬레이션, 리포트까지 한 Store가 단계별로 확장되는 흐름을 보여줍니다.

```bash
dotnet run --project ds2/Apps/Tutorial/Ds2.Tutorial.csproj
```

전체 실행은 `0`, 특정 단계만 실행하려면 단계 번호를 입력합니다. stdin이 리다이렉트된 환경에서는 Simulation CLI가 자동 데모 모드로 실행됩니다.

## 단계

1. `Step01_AddEntities` — `Project`, `DsSystem`, `Flow`, `Work` 생성과 `ImportPlan` 적용
2. `Step02_AddArrows` — `ArrowBetweenWorks`로 Work 연결
3. `Step03_QueryExplore` — `Queries`, `StoreHierarchyQueries`로 Store 탐색
4. `Step04_SaveLoad` — JSON save/load, Mermaid/CSV import, AASX export/import
5. `Step05_Simulation` — `SimIndexModule.build`, `EventDrivenEngine`, token simulation
6. `Step06_Report` — `StateChangeRecord`, `ReportService`, HTML/CSV report
7. `Step07_SimCli` — Runtime API를 조합한 인터랙티브 콘솔
8. `Step08_ConvertCli` — JSON/AASX roundtrip 변환 파이프라인

## NuGet 소비자와의 차이

Step 01~02는 DS2 소스 레벨 튜토리얼이라 `Project`, `Work` 같은 internal 생성자를 직접 사용합니다. 이 프로젝트는 assembly name이 `Ds2.Tutorial`이고 `Ds2.Core`의 friend assembly로 등록되어 있어 컴파일됩니다.

NuGet 패키지 소비자는 internal 생성자에 접근할 수 없으므로 `Ds2.Mermaid.MermaidImporter`, `Ds2.CSV.CsvImporter`, `Ds2.Aasx.PlcAasxFacade` 같은 public import/facade API로 `DsStore`를 만든 뒤 Runtime으로 넘기는 방식을 사용합니다. 패키지 소비자 기준 예제는 wrapper repo의 `Samples/CSharp`를 참고합니다.

## 별도 튜토리얼

- [외부 OPC UA 클라이언트 연결·구독](OpcUaExternalClient/README.md) — 별도 Web 프로젝트로 실행하는 DSPilot PFX 발급, Application Certificate 승인, AID/XGT Variable 구독 튜토리얼
