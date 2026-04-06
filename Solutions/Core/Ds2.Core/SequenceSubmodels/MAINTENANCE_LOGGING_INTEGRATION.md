# Maintenance-Logging 통합 최종 보고서
**작성일**: 2026-04-06
**프로젝트**: Ds2.Core SequenceSubmodels
**목적**: Maintenance.fs가 Logging.fs의 에러 태그 정보를 참조하여 예지 보전 수행

---

## 1. 최적화 개요

### 1.1 문제 인식
- Maintenance.fs에서 독자적으로 `MaintenanceErrorTagSpec` 정의 → **중복**
- Logging.fs의 `ErrorLogTagSpec`과 별도로 에러 코드/태그 관리 → **데이터 불일치 위험**
- 두 서브모델 간 에러 정보 동기화 필요

### 1.2 해결 방안
- **Logging.fs를 단일 진실 공급원(Single Source of Truth)으로 지정**
- Maintenance.fs는 `Logging.ErrorLogTagSpec.Name`을 참조하여 예지 보전 수행
- ErrorTrackingConfig로 예지 보전 추가 설정만 관리

---

## 2. 아키텍처 변경

### 2.1 변경 전 (중복 구조)
```
Logging.fs
├── ErrorLogTagSpec (Name, Address, ErrorCode, Severity, ...)
└── ErrorLogEvent (런타임 이벤트)

Maintenance.fs
├── MaintenanceErrorTagSpec (TagName, ErrorCode, ...) ← 중복!
└── ErrorBasedPrediction (분석 결과)
```

### 2.2 변경 후 (참조 구조)
```
Logging.fs (단일 진실 공급원)
└── ErrorLogTagSpec
    ├── Name: "Motor_Overload"
    ├── Address: "M901"
    ├── ErrorCode: "E002"
    ├── Severity: "Error"
    └── AutoLogToFile, LinkedLotTracking, EnableHashChain
                    ↑
                    │ (Name으로 참조)
                    │
Maintenance.fs (예지 보전 설정)
└── ErrorTrackingConfig
    ├── ErrorLogTagName: "Motor_Overload" ← Logging.ErrorLogTagSpec.Name 참조
    ├── ThresholdCount: 3
    ├── AnalysisPeriodDays: 7
    ├── EnablePrediction: true
    └── PredictionModel: "Linear"
                    ↓
            ErrorBasedPrediction (분석 결과)
            ├── ErrorLogTagName: "Motor_Overload"
            ├── ErrorTrend: "Increasing"
            ├── PredictedFailureDate: 2026-04-13
            └── ConfidenceLevel: 0.85
```

---

## 3. 구현 상세

### 3.1 Logging.fs (변경 없음)

**ErrorLogTagSpec** - 에러 태그 정의 (appsettings.json)
```fsharp
type ErrorLogTagSpec() =
    member val Name: string = ""                      // 태그 이름 (고유 식별자)
    member val Address: string = ""                   // PLC 주소 (예: "M901")
    member val ErrorCode: string = ""                 // 에러 코드 (예: "E002")
    member val Severity: string = "Error"             // "Info" | "Warning" | "Error" | "Critical"
    member val DataType: string = "Bool"
    member val AutoLogToFile: bool = true
    member val AutoLogToDatabase: bool = false
    member val LinkedLotTracking: bool = false
    member val EnableHashChain: bool = false
    member val Description: string = ""
```

### 3.2 Maintenance.fs (최적화)

#### 변경 1: MaintenanceErrorTagSpec → ErrorTrackingConfig

**변경 전**:
```fsharp
type MaintenanceErrorTagSpec() =
    member val TagName: string = ""           // ← 중복!
    member val ErrorCode: string = ""         // ← 중복!
    member val ThresholdCount = 5
    member val AnalysisPeriodDays = 30
    member val EnablePrediction = true
```

**변경 후**:
```fsharp
type ErrorTrackingConfig() =
    member val ErrorLogTagName: string = ""   // ← Logging.ErrorLogTagSpec.Name 참조
    member val ThresholdCount = 5
    member val AnalysisPeriodDays = 30
    member val EnablePrediction = true
    member val PredictionModel: string = "Linear"  // 예측 모델 추가
```

#### 변경 2: ErrorBasedPrediction 최적화

**변경 전**:
```fsharp
type ErrorBasedPrediction() =
    member val ErrorCode: string = ""         // ← 중복!
    member val ErrorFrequency = 0
    // ...
```

**변경 후**:
```fsharp
type ErrorBasedPrediction() =
    member val ErrorLogTagName: string = ""   // ← Logging.ErrorLogTagSpec.Name 참조
    member val ErrorCode: string = ""         // 런타임 조회용 (캐시)
    member val ErrorFrequency = 0
    member val ErrorTrend: string = "Stable"
    member val PredictedFailureDate: DateTime option = None
    member val ConfidenceLevel = 0.0
    member val ErrorHistory: DateTime array = [||]
```

#### 변경 3: MaintenanceSystemProperties

**변경 전**:
```fsharp
member val ErrorTagSpecs = ResizeArray<MaintenanceErrorTagSpec>()
```

**변경 후**:
```fsharp
member val ErrorTrackingConfigs = ResizeArray<ErrorTrackingConfig>()
```

#### 변경 4: MaintenanceWorkProperties

**변경 전**:
```fsharp
member val TrackedErrorCodes: string array = [||]  // ← 중복!
```

**변경 후**:
```fsharp
member val TrackedErrorLogTagNames: string array = [||]  // ← Logging.ErrorLogTagSpec.Name 참조
```

---

## 4. appsettings.json 통합

### 4.1 Logging 설정 (에러 태그 정의)
```json
{
  "LoggingSystemProperties": {
    "ErrorLogTagSpecs": [
      {
        "Name": "Motor_Overload",
        "Address": "M901",
        "ErrorCode": "E002",
        "Severity": "Error",
        "AutoLogToFile": true,
        "LinkedLotTracking": true,
        "EnableHashChain": false
      },
      {
        "Name": "Vacuum_Pressure_Low",
        "Address": "M903",
        "ErrorCode": "E003",
        "Severity": "Error",
        "AutoLogToFile": true,
        "LinkedLotTracking": true,
        "EnableHashChain": true
      }
    ]
  }
}
```

### 4.2 Maintenance 설정 (예지 보전 설정)
```json
{
  "MaintenanceSystemProperties": {
    "EnableErrorBasedPrediction": true,
    "ErrorTrackingConfigs": [
      {
        "ErrorLogTagName": "Motor_Overload",
        "ThresholdCount": 3,
        "AnalysisPeriodDays": 7,
        "EnablePrediction": true,
        "PredictionModel": "Linear"
      },
      {
        "ErrorLogTagName": "Vacuum_Pressure_Low",
        "ThresholdCount": 5,
        "AnalysisPeriodDays": 14,
        "EnablePrediction": true,
        "PredictionModel": "Exponential"
      }
    ]
  }
}
```

### 4.3 Work 설정 (Work별 에러 추적)
```json
{
  "MaintenanceWorkProperties": {
    "EnableErrorTracking": true,
    "TrackedErrorLogTagNames": ["Motor_Overload", "Vacuum_Pressure_Low"]
  }
}
```

---

## 5. 데이터 흐름

### 5.1 전체 흐름
```
1. appsettings.json 로드
   ├── Logging.ErrorLogTagSpec (에러 태그 정의)
   └── Maintenance.ErrorTrackingConfig (예지 보전 설정)
        ↓
2. PLC 스캔 → 에러 감지
   ├── Logging: M901 (Motor_Overload) = true
   └── ErrorLogTagSpec.Name = "Motor_Overload"로 식별
        ↓
3. Logging 처리
   ├── AutoLogToFile → 파일 기록
   ├── LinkedLotTracking → LOT 번호 연결
   └── EnableHashChain → 해시 체인 연결
        ↓
4. Maintenance 분석
   ├── ErrorTrackingConfig 조회 (ErrorLogTagName = "Motor_Overload")
   ├── ThresholdCount = 3, AnalysisPeriodDays = 7
   ├── ErrorFrequency 업데이트 (3회 발생)
   ├── ErrorTrend 분석 → "Increasing"
   └── PredictedFailureDate 계산 → 2026-04-13
        ↓
5. ErrorBasedPrediction 생성
   ├── ErrorLogTagName = "Motor_Overload"
   ├── ErrorTrend = "Increasing"
   ├── PredictedFailureDate = 2026-04-13
   └── ConfidenceLevel = 0.85
```

### 5.2 참조 관계
```
Logging.ErrorLogTagSpec
    └── Name: "Motor_Overload"
            ↑
            │ (참조)
            │
Maintenance.ErrorTrackingConfig
    └── ErrorLogTagName: "Motor_Overload"
            ↓
Maintenance.ErrorBasedPrediction
    └── ErrorLogTagName: "Motor_Overload"
            ↓
MaintenanceWorkProperties
    └── TrackedErrorLogTagNames: ["Motor_Overload", ...]
```

---

## 6. 사용 예시

### 6.1 런타임 조회
```fsharp
// 1. Logging에서 ErrorLogTagSpec 조회
let errorLogTagSpec =
    loggingProps.ErrorLogTagSpecs
    |> Seq.find (fun t -> t.Name = "Motor_Overload")

// 2. Maintenance에서 ErrorTrackingConfig 조회
let trackingConfig =
    maintProps.ErrorTrackingConfigs
    |> Seq.find (fun c -> c.ErrorLogTagName = "Motor_Overload")

// 3. 에러 발생 시 Logging 처리
if plcReader.ReadBool(errorLogTagSpec.Address) then
    if errorLogTagSpec.AutoLogToFile then
        logToFile errorLogTagSpec
    if errorLogTagSpec.LinkedLotTracking then
        linkToLot errorLogTagSpec currentLotNumber

// 4. Maintenance 예지 보전 분석
if trackingConfig.EnablePrediction then
    let prediction = ErrorBasedPrediction()
    prediction.ErrorLogTagName <- trackingConfig.ErrorLogTagName
    prediction.ErrorCode <- errorLogTagSpec.ErrorCode
    prediction.ErrorFrequency <- getErrorCount errorLogTagSpec.Name 7
    prediction.ErrorTrend <- analyzeErrorTrend prediction.ErrorFrequency
    prediction.PredictedFailureDate <-
        predictFailureByErrorRate
            prediction.ErrorFrequency
            trackingConfig.ThresholdCount
            trackingConfig.AnalysisPeriodDays
```

### 6.2 Work별 에러 추적
```fsharp
let workProps = MaintenanceWorkProperties()
workProps.EnableErrorTracking <- true
workProps.TrackedErrorLogTagNames <- [| "Motor_Overload"; "Vacuum_Pressure_Low" |]

// 에러 발생 시 Work별 통계 업데이트
for tagName in workProps.TrackedErrorLogTagNames do
    let errorLogTagSpec =
        loggingProps.ErrorLogTagSpecs
        |> Seq.find (fun t -> t.Name = tagName)

    if plcReader.ReadBool(errorLogTagSpec.Address) then
        workProps.ErrorFrequency <- workProps.ErrorFrequency + 1
        workProps.LastErrorTime <- Some DateTime.UtcNow
```

---

## 7. 핵심 개선사항

### 7.1 중복 제거
| 항목 | 변경 전 | 변경 후 |
|------|---------|---------|
| 에러 코드 정의 | Logging + Maintenance (중복) | **Logging만** (단일) |
| 태그 이름 | 각자 관리 | **Logging.Name 참조** |
| 데이터 동기화 | 수동 (불일치 위험) | **자동 (Name 참조)** |

### 7.2 설계 개선
- ✅ **단일 진실 공급원(SSOT)**: Logging.ErrorLogTagSpec
- ✅ **느슨한 결합**: Maintenance는 Name만 참조 (상호 의존 없음)
- ✅ **역할 분리**: Logging(기록) vs Maintenance(분석)

### 7.3 코드 간결성
```
변경 전:
  MaintenanceErrorTagSpec: 5개 필드 (TagName, ErrorCode, ...) ← 중복

변경 후:
  ErrorTrackingConfig: 5개 필드 (ErrorLogTagName, ...) ← 참조만

감소율: 중복 제거 (ErrorCode, TagName 등)
```

---

## 8. 빌드 결과

```
dotnet build Ds2.Core/Ds2.Core.fsproj
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:00.55
```

---

## 9. 다음 단계

### 9.1 런타임 구현
1. **Logging → Maintenance 브리지**
   - ErrorLogTagSpec.Name 기반 조회 함수
   - 에러 발생 시 Maintenance 자동 업데이트

2. **예지 보전 엔진**
   - ErrorTrackingConfig 기반 분석
   - PredictionModel에 따라 Linear/Exponential 예측
   - ConfidenceLevel 자동 계산

3. **Work별 통계**
   - TrackedErrorLogTagNames 기반 집계
   - Work별 ErrorTrend, PredictedFailureDate 계산

### 9.2 테스트
1. Logging → Maintenance 참조 무결성 테스트
2. ErrorLogTagSpec.Name 변경 시 동기화 테스트
3. 예지 보전 정확도 검증

---

## 10. 결론

### 10.1 최적화 요약
- **Logging.fs를 단일 진실 공급원으로 지정**
- **Maintenance.fs는 Name 참조만으로 예지 보전 수행**
- **중복 제거, 데이터 일관성 보장, 느슨한 결합**

### 10.2 핵심 성과
- ✅ 빌드 성공 (0 warnings, 0 errors)
- ✅ 중복 코드 제거 (ErrorCode, TagName 등)
- ✅ 데이터 불일치 위험 제거
- ✅ 명확한 데이터 흐름 (Logging → Maintenance)

### 10.3 설계 철학
> **"Logging이 정의하고, Maintenance가 분석한다"**
>
> - Logging.ErrorLogTagSpec: 에러 태그 정의 (Name, Address, ErrorCode, Severity)
> - Maintenance.ErrorTrackingConfig: 예지 보전 설정 (ErrorLogTagName 참조)
> - ErrorBasedPrediction: 런타임 분석 결과 (ErrorTrend, PredictedFailureDate)
>
> 두 서브모델은 독립적이지만, Name을 통해 느슨하게 결합된다.

---

**End of Report**
