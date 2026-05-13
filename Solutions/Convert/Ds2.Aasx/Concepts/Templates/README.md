# IDTA Submodel Templates — 외부 표준 추적

## 목적

ds2 가 emit 하는 표준 Submodel (Nameplate / HandoverDocumentation / TechnicalData)
을 **코드로 직조 → 템플릿 파일 기반 로드** 방식으로 전환.

이유: IDTA / admin-shell-io 가 정의를 지속 갱신함 → 코드로 매번 따라가는 것은 비현실적.
템플릿 파일을 교체하면 구조/CD/언어태그가 자동 동기화.

> 출처: <https://github.com/admin-shell-io/submodel-templates>

## 현재 상태 (2026-05-07 갱신)

| 파일 | IDTA 번호 | 버전 | 출처 |
|---|---|---|---|
| `Nameplate.aasx` | 02006 Digital Nameplate | **v3.0.1** ✅ | [published/Digital nameplate/3/0/1/](https://github.com/admin-shell-io/submodel-templates/tree/main/published/Digital%20nameplate/3/0/1) |
| `HandoverDocumentation.aasx` | 02004 Handover Documentation | **v2.0** ✅ | [published/Handover Documentation/2/0/](https://github.com/admin-shell-io/submodel-templates/tree/main/published/Handover%20Documentation/2/0) |
| `TechnicalData.aasx` | 02003 Technical Data | **v2.0** ✅ | [published/Technical_Data/2/0/](https://github.com/admin-shell-io/submodel-templates/tree/main/published/Technical_Data/2/0) |
| `SequenceModel.aasx` | (ds2 자체) | 1.0 | ds2 도메인 SM, IDTA 외 |

## 다운로드 (재실행 시)

```bash
cd Concepts/Templates

# Digital Nameplate v3.0.1
curl -L -o Nameplate.aasx \
  "https://raw.githubusercontent.com/admin-shell-io/submodel-templates/main/published/Digital%20nameplate/3/0/1/IDTA%2002006-3-0-1_Template_Digital%20Nameplate.aasx"

# Handover Documentation v2.0
curl -L -o HandoverDocumentation.aasx \
  "https://raw.githubusercontent.com/admin-shell-io/submodel-templates/main/published/Handover%20Documentation/2/0/IDTA%2002004-2-0_Template_HandoverDocumentation.aasx"

# Technical Data v2.0
curl -L -o TechnicalData.aasx \
  "https://raw.githubusercontent.com/admin-shell-io/submodel-templates/main/published/Technical_Data/2/0/IDTA%2002003_Template_TechnicalData.aasx"
```

## 동작 방식 (Phase 2 이후)

```
ds2 Project
   │
   │  (1) 사용자 입력 데이터 (제조사명, 시리얼 등)
   ▼
TemplateLoader.loadSubmodel(asmName, submodelIdShort)
   │
   │  (2) 템플릿 SM 의 deep clone — 표준 구조/CD/semanticId 그대로
   ▼
TemplateScaffold.applyValues
   │
   │  (3) ds2 데이터 → idShort path 별 Property value override
   │      (구조는 절대 수정하지 않음)
   ▼
완성된 Submodel (IDTA 표준 구조 + ds2 사용자 값)
```

## 업데이트 워크플로우

IDTA 가 새 버전 publish 시:
1. 본 폴더에서 해당 .aasx 교체
2. 빌드/테스트 — `idShort` 변경된 필드는 `TemplateScaffold` 의 매핑만 손보면 됨
3. `Catalog.fs` 의 자체 발급 CD 와 충돌 없는지만 확인 (필터 prefix 로 자동 처리)

## 보존 정책

- 템플릿 파일은 **무수정 원본** 유지 — 사용자 데이터 주입은 export 시 메모리에서만.
- 임포트는 양방향 호환 — 옛 버전 AASX 도 idShort lookup 으로 읽기 시도.
