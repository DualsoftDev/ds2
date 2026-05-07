# Template-Driven Submodel 리팩터 설계문서

## 1. 배경 / 문제

현재 `Export/Metadata.fs` (Nameplate / HandoverDocumentation), `Export/TechnicalData.fs`
(TechnicalData) 가 IDTA 표준 SM 의 구조를 **F# 코드로 일일이 직조** 한다:

```fsharp
let nameplateToSubmodel (np: Nameplate) (projectId: Guid) : Submodel =
    let elems = [
        mkProp "URIOfTheProduct" np.URIOfTheProduct
        mkMlp  "ManufacturerName" np.ManufacturerName
        ...
        addressToSmc np.AddressInformation
        ...
    ]
```

문제:
- IDTA / admin-shell-io 가 정의를 지속 갱신 (예: Digital Nameplate v2 → v3.0.1)
- 새 필드 추가 / 이름 변경 / semanticId 갱신마다 코드 수정 필요
- 한국 / 독일어 / 영어 라벨, 유닛, 단위 정의 등을 다중 위치 동기화 필요
- 유지비용 비현실적

## 2. 목표

코드 = **사용자 데이터를 주입하는 얇은 레이어** 로 한정.
구조 / CD / semanticId / 다국어 라벨은 **IDTA 가 publish 한 .aasx 템플릿** 이 단일 진실 원천.

```
[IDTA published .aasx] = 구조(Schema)
[ds2 Project 데이터]    = 값(Values)
        │
        └─ TemplateLoader + Scaffold → 최종 Submodel emit
```

## 3. 원칙

| 원칙 | 의미 |
|---|---|
| **Schema = Template** | SM 의 모든 SubmodelElement 트리, idShort, semanticId, CD 참조, MLP 언어 슬롯은 템플릿이 결정. 코드는 만들지 않는다. |
| **Values = ds2** | 코드는 idShort path → ds2 value 로의 매핑만 책임. |
| **무 구조 변경** | 코드는 절대 SubmodelElement 추가/삭제/이름 변경하지 않음 (CD ID 와 일관성 유지). |
| **양방향** | Import 도 동일 path 매핑 사용 → round-trip 안전. |
| **호환** | 옛 버전 AASX 도 best-effort idShort lookup 으로 읽기. |

## 4. 아키텍처

```
┌─────────────────────────────────────────────────────────┐
│ Concepts/Templates/                                      │
│  ├ Nameplate.aasx                  (IDTA 02006 v3.0.1)   │
│  ├ HandoverDocumentation.aasx      (IDTA 02004 v1.2)     │
│  ├ TechnicalData.aasx              (IDTA 02003 v1.2)     │  ← 추가
│  └ SequenceModel.aasx              (ds2 자체)            │
└─────────────────────────────────────────────────────────┘
            │
            │ embed as resource → fsproj
            ▼
┌─────────────────────────────────────────────────────────┐
│ Concepts/TemplateLoader.fs (신규)                        │
│   loadSubmodel : aasxResource × submodelIdShort          │
│                → ISubmodel (deep clone)                  │
│   loadAllConceptDescriptions : aasxResource              │
│                → IConceptDescription list                │
└─────────────────────────────────────────────────────────┘
            │
            ▼
┌─────────────────────────────────────────────────────────┐
│ Concepts/TemplateScaffold.fs (신규)                      │
│   applyValues : ISubmodel × Map<idShortPath, value>      │
│               → ISubmodel  (in-place mutation OK)        │
│   readValues  : ISubmodel → Map<idShortPath, value>      │
└─────────────────────────────────────────────────────────┘
            │
            ▼
┌─────────────────────────────────────────────────────────┐
│ Export/Metadata.fs · Export/TechnicalData.fs (리팩터)    │
│   nameplateToSubmodel:                                   │
│     1. TemplateLoader.loadSubmodel "Nameplate.aasx" ...  │
│     2. Scaffold.applyValues sm (mapNameplateToPaths np)  │
│     3. sm.Id <- $"urn:dualsoft:nameplate:{projectId}"    │
│                                                          │
│   같은 패턴: documentationToSubmodel, technicalDataToSubmodel │
└─────────────────────────────────────────────────────────┘
```

## 5. ds2 → idShort path 매핑 예시 (Nameplate)

```fsharp
let nameplateValueMap (np: Nameplate) : Map<string, NodeValue> =
    Map.ofList [
        "URIOfTheProduct",                      Prop np.URIOfTheProduct
        "ManufacturerName",                     Mlp  np.ManufacturerName
        "ManufacturerProductDesignation",       Mlp  np.ManufacturerProductDesignation
        "ContactInformation/AddressOfAdditionalLink", Prop np.AddressInformation.Street
        "ContactInformation/Phone/TelephoneNumber",   Mlp  np.AddressInformation.Phone.TelephoneNumber
        // ... etc.
    ]
```

`NodeValue` = `Prop of string | Mlp of string | List of <itemValueMap> seq`.

Scaffold 가 트리를 walk 하며 path 매치 시 Property/MLP/SML 값을 갱신.
매핑 없는 path 는 템플릿 default 값 유지 (또는 빈 값).

## 6. 단계별 PR

| PR | 내용 | 영향 범위 |
|---|---|---|
| **PR1 (Phase 1)** | 인프라: TemplateLoader + 3개 .aasx 임베딩 + README | 빌드 변화 없음 (사용처 미연결) |
| **PR2** | TemplateScaffold (apply/read) + 단위테스트 | 빌드 변화 없음 |
| **PR3** | Nameplate.aasx 를 v3.0.1 로 교체 + nameplateToSubmodel 리팩터 | export 결과 v3.0.1 구조 |
| **PR4** | HandoverDocumentation 동일 패턴 리팩터 | export 결과 v1.2 구조 |
| **PR5** | TechnicalData.aasx 추가 + technicalDataToSubmodel 리팩터 | export 결과 v1.2 구조 |
| **PR6** | Import 측 Scaffold.readValues 사용으로 round-trip 안정화 | import 안정성 ↑ |

## 7. 호환성 / 마이그레이션

- 임포트: 기존 ds2 가 만든 AASX 와 새 템플릿 기반 AASX 모두 idShort 일치하면 동작.
  v2 → v3 의 idShort 변경분만 alias map 으로 흡수.
- CD 충돌: 신/구 CD 가 같은 ID 면 신규 우선 (Builder.fs 의 `legacyCdPrefixes` 필터 패턴 적용).
- AasxProjectCache: 임포트 시 원본 SM 보존 정책 유지 — 사용자가 외부 툴로 추가한 element 보존.

## 8. 리스크

| 리스크 | 대응 |
|---|---|
| 템플릿 idShort 와 ds2 필드 의미 불일치 | 매핑 함수에서 명시적 처리 (override / skip / synthesize) |
| 템플릿 파일이 .aasx 형식이 아닌 .xml 만 제공 | AASX Package Explorer 로 1회 wrap 후 사용. README 에 절차 명시 |
| 새 IDTA 버전이 SubmodelElement 종류 변경 (Property → Range 등) | Scaffold 에서 호환성 어댑터 추가 |
| 다중 SM 임베디드 → DLL 크기 ↑ | 각 .aasx 가 ~10KB → 무시할 수준 |

## 9. 후속 작업 (본 문서 범위 외)

- 임베디드 sm 별 minimum/maximum cardinality validator
- 새 IDTA 버전 배포 모니터링 자동화 (CI 작업)
- AID (Asset Interface Description) 등 다른 표준 SM 으로 확장
