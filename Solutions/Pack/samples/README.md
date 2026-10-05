# DualSoft-DS2 C# Tutorial Samples

이 폴더는 NuGet 패키지 소비자 기준의 C# 튜토리얼 예제입니다. DS2 소스 내부 생성자에 의존하지 않고 public import/facade API로 `DsStore`를 만든 뒤 Runtime까지 연결합니다.

```bash
dotnet run --project Samples/CSharp
```

패키지가 아직 배포되기 전 로컬 `ds2` submodule 소스로 검증할 때는 다음처럼 실행합니다.

```bash
dotnet run --project Samples/CSharp -p:UseLocalDs2Projects=true
```

## Tutorial 흐름

1. Mermaid 파일을 `DsStore`로 import
2. Core Store 조회, JSON save/load
3. CSV import 및 PLC CSV -> AASX facade 호출
4. AASX export
5. Runtime `SimIndex` 빌드, `EventDrivenEngine` 실행
6. Runtime Report HTML/CSV export

실행 결과 파일은 샘플 앱 출력 폴더의 `out` 디렉터리에 생성됩니다.

DS2 소스 레벨 튜토리얼은 `ds2/Apps/Tutorial`에 있습니다. 그 프로젝트는 friend assembly라 internal 생성자를 직접 다루는 Step도 포함합니다.
