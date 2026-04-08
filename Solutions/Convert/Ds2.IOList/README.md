# Ds2.IOList Generator

> DS2 모델에서 PLC IO/Dummy 신호 목록을 자동 생성하는 F# 라이브러리

**버전**: 4.0
**표준**: IEC 61131-3
**대상**: ProMaker UI 통합

## 빠른 시작 (C#)

```csharp
using Ds2.IOList;

var api = new IoListGeneratorApi();
var result = api.Generate(store, @"C:\Templates");

if (api.IsSuccess(result))
{
    api.ExportIoList(result, "io_list.csv");
    api.ExportDummyList(result, "dummy_list.csv");
}
```

## 주요 기능

- ✅ **IEC 61131-3 데이터 타입**: BOOL, INT, DINT, REAL 등 13종 지원
- ✅ **GLOBAL/LOCAL 주소 할당**: SystemType별 또는 Flow별 선택 가능
- ✅ **address_config.txt**: 모든 베이스 주소를 한 곳에서 관리
- ✅ **템플릿 기반 슬롯 할당**: 주소 충돌 없는 안정적인 할당
- ✅ **CSV 자동 내보내기**: io_list.csv, dummy_list.csv
- ✅ **ProMaker 통합**: 간단한 API

## 핵심 정책 (v4.0)

- ✅ **베이스 주소**: address_config.txt에서만 설정
- ✅ **템플릿 파일**: 매크로 정보만 (주소 설정 금지)
- ✅ **GLOBAL vs LOCAL**: @SYSTEM 유무로 자동 결정

## 문서

📖 **[완전한 가이드](doc/README.md)** - 사용법, 예제, 트러블슈팅 모두 포함

## 예제

실전 예제는 `Ds2.IOList.Console/Demo/Templates_BB_LINE` 참조

## 라이선스

Proprietary - DualSoft
