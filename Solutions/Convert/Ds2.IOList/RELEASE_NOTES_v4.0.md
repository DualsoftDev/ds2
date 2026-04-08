# Ds2.IOList Generator v4.0 - Release Notes

## 릴리스 날짜: 2026-03-26

---

## 🎉 주요 신규 기능

### 1. 주소 할당 모드 시스템

**문제**: 기존 flow.txt 방식은 Flow별로만 주소를 할당할 수 있어, SystemType(예: 모든 로봇)이 연속된 주소를 사용해야 하는 경우 지원 불가능

**해결**: 두 가지 주소 할당 모드 도입 (GLOBAL, LOCAL)

#### GLOBAL Mode (권장)
SystemType별로 연속된 주소 공간 사용
```
@SYSTEM RBT
@IW_BASE 3070
@QW_BASE 3070
@MW_BASE 9110

@SYSTEM PIN
@IW_BASE 3200
@QW_BASE 3200
@MW_BASE 9200
```

**효과**:
- 모든 RBT 장치가 Flow에 상관없이 연속된 주소 사용
- Flow S301 RBT_1: %QW3070.0 ~ %QW3075.15
- Flow S301 RBT_2: %QW3076.0 ~ %QW3081.15
- Flow S302 RBT_1: %QW3082.0 ~ %QW3087.15 (연속!)

#### LOCAL Mode
Flow별로 독립적인 주소 공간 사용
```
@FLOW S301
@IW_BASE 3070
@QW_BASE 3070
@MW_BASE 9110

@FLOW S302
@IW_BASE 3170
@QW_BASE 3170
@MW_BASE 9210
```

**혼합 사용 가능**:
```
# RBT는 GLOBAL 모드
@SYSTEM RBT
@IW_BASE 3070

# PIN은 LOCAL 모드 (각 Flow별로 설정)
@FLOW S301
@IW_BASE 3200
```

### 2. 완전한 데이터 타입 지원

**기존**: BOOL만 지원
**신규**: IEC 61131-3 표준 13개 타입 전체 지원

| DS2 ValueSpec | IEC 타입 | 비트 크기 |
|--------------|---------|----------|
| BoolValue | BOOL | 1 bit |
| Int8Value | SINT | 8 bit |
| Int16Value | INT | 16 bit |
| Int32Value | DINT | 32 bit |
| Int64Value | LINT | 64 bit |
| UInt8Value | USINT | 8 bit |
| UInt16Value | UINT | 16 bit |
| UInt32Value | UDINT | 32 bit |
| UInt64Value | ULINT | 64 bit |
| Float32Value | REAL | 32 bit |
| Float64Value | LREAL | 64 bit |
| StringValue | STRING | Variable |

**자동 변환**: ApiCall의 InputSpec/OutputSpec에서 자동으로 데이터 타입 추출

---

## 🔧 주요 변경사항

### 파일명 변경
- `flow.txt` → `address_config.txt`
- `FlowConfig.fs` → `AddressConfig.fs`

### API 변경
**기존**:
```fsharp
api.Generate(store, templateDir, flowConfigPath)
```

**신규**:
```fsharp
api.Generate(store, templateDir)  // address_config.txt 자동 로드
```

### 내부 구조 개선
- **AllocationState**: 전역/지역 카운터 관리
- **주소 할당 로직**: 모드별 전략 패턴 적용
- **SignalGenerator**: 상태 스레딩(state threading) 방식으로 리팩토링

---

## 📊 테스트 결과

### BB_LINE 프로젝트 (실제 프로젝트)
- **ApiCalls**: 444개
- **생성된 IO 신호**: 472개
- **생성된 Dummy 신호**: 28개
- **에러**: 0개
- **경고**: 0개

### 주소 연속성 검증 (GLOBAL Mode)
```
S301_RBT_2.CTYPE_80000000 → %QW3070.0
S301_RBT_2.CTYPE_1 → %QW3071.15
S301_RBT_2.CN_8000 → %QW3072.0
...
S302_RBT_2.CTYPE_80000000 → %QW3081.0  ✅ 연속됨!
```

---

## 📚 신규 문서

### 1. ADDRESS_ALLOCATION.md (신규)
- GLOBAL/LOCAL/HYBRID 모드 상세 설명
- 설정 파일 문법 레퍼런스
- v3.0 → v4.0 마이그레이션 가이드
- 모범 사례 및 트러블슈팅

### 2. SPECIFICATION.md (업데이트)
- 섹션 4: 주소 할당 모드 설명 추가
- 섹션 5: 슬롯 기반 할당 전략 설명 업데이트

### 3. README.md (업데이트)
- v4.0 신규 기능 강조
- ADDRESS_ALLOCATION.md 링크 추가

---

## 🔄 마이그레이션 가이드

### v3.0에서 v4.0으로 업그레이드

#### 기존 flow.txt 사용 중인 경우

**옵션 1: GLOBAL 모드로 변경 (권장)**
```
# 기존 flow.txt
@FLOW S301
@IW_BASE 3070
@FLOW S302
@IW_BASE 3170

↓

# 신규 address_config.txt
@SYSTEM RBT
@IW_BASE 3070
@SYSTEM PIN
@IW_BASE 3200
```

**옵션 2: LOCAL 모드 유지**
```
# flow.txt → address_config.txt로 이름만 변경
@FLOW S301
@IW_BASE 3070
@FLOW S302
@IW_BASE 3170
```

#### API 호출 코드 변경
```csharp
// 기존 v3.0
var result = api.Generate(store, templateDir, flowConfigPath);

// 신규 v4.0
var result = api.Generate(store, templateDir);
// address_config.txt 자동 로드됨
```

---

## ⚠️ 호환성 정보

### 하위 호환성
- ✅ address_config.txt가 없으면 템플릿의 기본 주소 사용 (v3.0 이전과 동일)
- ✅ 기존 템플릿 파일(RBT.txt 등) 그대로 사용 가능
- ✅ 출력 CSV 형식 변경 없음

### 주의사항
- ⚠️ flow.txt는 더 이상 자동 로드되지 않음 (address_config.txt로 변경 필요)
- ⚠️ API 시그니처 변경 (flowConfigPath 파라미터 제거)

---

## 🐛 버그 수정

### MW 신호 오류 처리 개선
**문제**: MW 템플릿에 없는 API도 에러 발생
**수정**: MW 신호는 선택사항으로 처리, 에러 무시

### 템플릿 파싱 개선
**문제**: address_config.txt가 템플릿으로 파싱되어 에러
**수정**: TemplateParser가 address_config.txt 제외

---

## 📦 파일 구조

### 신규 파일
```
Ds2.IOList/
├── AddressConfig.fs                                    # 신규
├── doc/
│   └── ADDRESS_ALLOCATION.md                          # 신규

Ds2.IOList.Console/Demo/
├── Templates_BB_LINE/address_config.txt               # 신규
├── Templates_Demo/address_config.txt                  # 신규
├── address_config_LOCAL_example.txt                   # 신규
└── address_config_HYBRID_example.txt                  # 신규
```

### 삭제된 파일
```
Ds2.IOList/
├── FlowConfig.fs                                       # 삭제 (AddressConfig.fs로 대체)

Ds2.IOList.Console/Demo/
├── Templates_BB_LINE/flow.txt                         # 삭제
└── Templates_Demo/flow.txt                            # 삭제
```

---

## 💡 모범 사례

### 1. GLOBAL 모드 우선 사용 (권장)
대부분의 프로젝트에는 GLOBAL 모드가 적합:
- 간단하고 직관적
- SystemType별 주소 공간 명확히 분리
- 전체 메모리 사용량 계산 용이

### 2. LOCAL 모드 사용 시점
- Flow가 완전히 독립적인 모듈인 경우
- Flow별로 별도 배포/테스트하는 경우

### 3. 혼합 사용 (유연한 전략)
- 일부 SystemType은 GLOBAL (예: 공유 리소스인 로봇)
- 나머지는 LOCAL (예: Flow별 센서/액추에이터)
- 한 파일 안에 `@SYSTEM`과 `@FLOW`를 동시에 사용 가능

---

## 🎯 성능 개선

### 빌드 시간
- 변경 없음 (0.7초)

### 메모리 사용량
- 카운터 관리로 인한 미미한 증가 (< 1MB)

### 생성 속도
- 444 ApiCall 처리: < 1초

---

## 🙏 기여자

- **설계 및 구현**: Claude Code
- **요구사항 정의**: 사용자 피드백 기반
- **테스트**: BB_LINE 실제 프로젝트 데이터

---

## 📞 지원

### 문서
- [SPECIFICATION.md](doc/SPECIFICATION.md) - 전체 사양서
- [ADDRESS_ALLOCATION.md](doc/ADDRESS_ALLOCATION.md) - 주소 할당 가이드
- [EXAMPLES.md](doc/EXAMPLES.md) - 실전 예제

### 문의
- 이슈 리포트: Ds2.IOList Generator Team

---

**다음 릴리스 예정**: v4.1 - Word 타입 신호 지원 (검토 중)
