namespace AasxEditor.Services;

/// <summary>
/// DS2 서브모델 속성(idShort)에 대한 한글 설명 매핑.
/// F# PropertiesBase 타입 정의 기준 (01_Simulation ~ 08_CostAnalysis).
/// </summary>
public static class PropertyDescriptions
{
    /// <summary>idShort 또는 Label → 설명 텍스트. 없으면 null 반환.</summary>
    public static string? Get(string label)
    {
        if (Descriptions.TryGetValue(label, out var desc))
            return desc;

        // "Submodels (11)", "ConceptDescriptions (41)" 등 카운트 포함 Label 처리
        var parenIdx = label.IndexOf(" (", StringComparison.Ordinal);
        if (parenIdx > 0)
            return Descriptions.GetValueOrDefault(label[..parenIdx]);

        return null;
    }

    public enum EditorHint { Auto, ForceJson, ForceText }

    /// <summary>idShort 기반 편집기 힌트. 기본은 Auto(자동 감지).</summary>
    public static EditorHint GetEditorHint(string idShort)
        => EditorHints.GetValueOrDefault(idShort, EditorHint.Auto);

    private static readonly Dictionary<string, EditorHint> EditorHints = new(StringComparer.Ordinal)
    {
        // 항상 JSON 구조로 저장되는 Property들 — 처음부터 JSON 편집기로 표시
        ["FBTagMapPresets"] = EditorHint.ForceJson,
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        // =====================================================================
        // AAS 구조 요소 (Shell, Submodel, SMC, Group 등)
        // =====================================================================

        // Root 레벨
        ["ProjectShell"] = "AAS 식별 정보와 자산(Asset) 참조를 포함하는 최상위 컨테이너",
        ["AssetInformation"] = "자산의 종류, 식별자, 썸네일 등 기본 정보",

        // Submodel 그룹
        ["Submodels"] = "AAS가 참조하는 서브모델 목록",
        ["ConceptDescriptions"] = "속성의 의미를 정의하는 개념 설명 목록 (semanticId 참조 대상)",

        // 서브모델 idShort
        ["SequenceModel"] = "시퀀스 구조 정의 — Flow/Work/Call 그래프",
        ["SequenceSimulation"] = "시뮬레이션, 용량분석, OEE, 병목감지, 시나리오 비교",
        ["SequenceControl"] = "PLC 통신, IO 태그 매핑, 안전 인터록",
        ["SequenceMonitoring"] = "실시간 상태 모니터링, 태그 값 추적",
        ["SequenceLogging"] = "실행 이력 로깅, Welford 통계, 에러 정의",
        ["SequenceMaintenance"] = "에러 추적, 설비 관리, 알람",
        ["SequenceHmi"] = "웹 HMI 화면 구성, 권한 관리, 실시간 UI",
        ["SequenceQuality"] = "SPC 관리도, 공정 능력(Cpk), Western Electric 규칙",
        ["SequenceCostAnalysis"] = "원가 분석, OEE 추적, BOM, 생산 능력 시뮬레이션",

        // 공통 SMC 계층
        ["SystemProperties"] = "시스템 수준 설정 (전역 구성)",
        ["FlowProperties"] = "Flow 수준 설정 (공정 단위)",
        ["WorkProperties"] = "Work 수준 설정 (작업 단위)",
        ["CallProperties"] = "Call 수준 설정 (호출 단위)",

        // 기타 구조
        ["ErrorDefinitions"] = "에러 정의 목록 (이름|태그주소|값타입)",

        // =====================================================================
        // 공통 메타데이터 (System-level 공유)
        // =====================================================================
        ["EngineVersion"] = "DS 엔진 버전",
        ["LangVersion"] = "DS 언어 버전",
        ["Author"] = "작성자",
        ["DateTime"] = "생성 일시",
        ["IRI"] = "IRI 식별자",
        ["SystemType"] = "시스템 타입",

        // =====================================================================
        // 공통 Work 속성 (대부분의 서브모델에서 공유)
        // =====================================================================
        ["Motion"] = "모션 정의",
        ["Script"] = "스크립트 참조",
        ["ExternalStart"] = "외부 시작 트리거 여부",
        ["IsFinished"] = "작업 완료 플래그",
        ["NumRepeat"] = "반복 횟수",
        ["Duration"] = "예상 소요 시간",
        ["SequenceOrder"] = "실행 순서",
        ["OperationCode"] = "공정 코드",

        // =====================================================================
        // 공통 Call 속성
        // =====================================================================
        ["ObjectName"] = "호출 대상 객체 이름",
        ["ActionName"] = "호출 액션 이름",
        ["RobotExecutable"] = "로봇 실행 프로그램 참조",
        ["Timeout"] = "타임아웃 시간",
        ["CallDirection"] = "데이터 방향 (InOut/InOnly/OutOnly)",
        ["Name"] = "이름",

        // =====================================================================
        // 01. Simulation (시뮬레이션)
        // =====================================================================

        // -- System --
        ["SimulationMode"] = "시뮬레이션 모드 (EventDriven/TimeStep/Hybrid)",
        ["EnablePhysicsSimulation"] = "물리 기반 시뮬레이션 활성화",
        ["TimeStepMs"] = "Time-Step 모드 시간 간격 (ms)",
        ["EnableBreakpoints"] = "시뮬레이션 브레이크포인트 활성화",
        ["RandomSeed"] = "난수 시드 (재현성 확보)",
        ["UseRandomVariation"] = "랜덤 변동 적용 (현실적 시뮬레이션)",
        ["VariationPercentage"] = "변동률 ±%",
        ["SimulationRepetitions"] = "몬테카를로 반복 횟수 (권장 100+)",
        ["ConfidenceLevel"] = "신뢰수준 (0.99 = 99%)",
        ["EnableCapacityAnalysis"] = "생산 능력 분석 활성화",
        ["CapacityHorizon"] = "능력 분석 기간 (ShortTerm/MediumTerm/LongTerm)",
        ["CapacityStrategy"] = "능력 계획 전략",
        ["DesignCapacityPerHour"] = "이론 최대 생산량 (units/hour)",
        ["DesignHoursPerDay"] = "설계 기준 일일 가동 시간",
        ["EffectiveCapacityPerHour"] = "현실 최대 생산량 (units/hour)",
        ["OperatingHoursPerDay"] = "실제 가동 시간 (예: 16h = 2교대)",
        ["PlannedCapacityPerHour"] = "계획 생산량 (units/hour)",
        ["OperatingDaysPerWeek"] = "주간 가동일 수",
        ["OperatingWeeksPerMonth"] = "월간 가동 주수",
        ["EnableThroughputTracking"] = "처리량 추적 활성화",
        ["ThroughputCalculationInterval"] = "처리량 계산 주기",
        ["TargetThroughputPerHour"] = "목표 시간당 처리량",
        ["CustomerDemandPerDay"] = "일일 고객 수요 (units)",
        ["TaktTime"] = "택트 타임 — 수요 기반 (초/대)",
        ["EnableCycleTimeAnalysis"] = "사이클 타임 분석 활성화",
        ["CycleTimeWarningThreshold"] = "경고 임계값: 설계 대비 +% 초과 시",
        ["EnableNormalDistributionTest"] = "정규분포 검증 활성화",
        ["EnableTocAnalysis"] = "TOC(제약이론) 분석 활성화",
        ["EnableBottleneckDetection"] = "병목 감지 활성화",
        ["BottleneckThreshold"] = "병목 임계값 (가동률 %, 0.90 = 90%)",
        ["CriticalThreshold"] = "긴급 임계값 (가동률 %, 0.95 = 95%)",
        ["EnableDbrSystem"] = "DBR(Drum-Buffer-Rope) 시스템 활성화",
        ["BufferType"] = "버퍼 유형 (Time/Quantity)",
        ["TimeBufferHours"] = "시간 버퍼 (시간)",
        ["BufferGreenZone"] = "Green 구간: 이 시간 초과 시 양호",
        ["BufferYellowZone"] = "Yellow 구간: 주의 필요",
        ["BufferRedZone"] = "Red 구간: 이 시간 미만 시 위험",
        ["EnableResourceUtilization"] = "자원 활용도 추적 활성화",
        ["IndustryType"] = "산업 유형 (벤치마크 비교용)",
        ["TargetUtilizationRate"] = "목표 자원 활용률 (%)",
        ["EnableLineBalancing"] = "라인 균형화 분석 활성화",
        ["EnableOeeTracking"] = "OEE 추적 활성화",
        ["OeeCalculationInterval"] = "OEE 계산 주기",
        ["TargetOEE"] = "목표 OEE (%, World Class = 85)",
        ["TargetAvailability"] = "목표 가동률 (%)",
        ["TargetPerformance"] = "목표 성능률 (%)",
        ["TargetQuality"] = "목표 양품률 (%)",
        ["EnableScenarioComparison"] = "What-If 시나리오 비교 활성화",
        ["ScenarioCount"] = "비교할 시나리오 수",
        ["EnableRoiCalculation"] = "ROI 자동 계산 활성화",

        // -- Flow --
        ["FlowSimulationEnabled"] = "Flow 시뮬레이션 활성화",
        ["FlowSimulationMode"] = "Flow 시뮬레이션 모드",

        // -- Work --
        ["DesignCycleTime"] = "설계 사이클 타임 (초)",
        ["ActualCycleTime"] = "실제 평균 사이클 타임 (초)",
        ["MinCycleTime"] = "최소 사이클 타임 (초)",
        ["MaxCycleTime"] = "최대 사이클 타임 (초)",
        ["CycleTimeSum"] = "사이클 타임 합계 (Welford 알고리즘)",
        ["CycleTimeSumOfSquares"] = "사이클 타임 제곱합 (분산 계산용)",
        ["CycleTimeStdDev"] = "사이클 타임 표준편차",
        ["CycleCount"] = "총 사이클 수",
        ["IsBottleneck"] = "병목 공정 여부",
        ["BottleneckSeverity"] = "병목 심각도 (NoBottleneck/Moderate/Critical)",
        ["UtilizationRate"] = "가동률 (%)",
        ["ProcessingTimePerUnit"] = "단위당 처리 시간 (초)",
        ["SetupTime"] = "준비 시간 (초)",
        ["TeardownTime"] = "정리 시간 (초)",
        ["BatchSize"] = "배치 크기",
        ["MaxParallelExecutions"] = "최대 병렬 실행 수",
        ["TotalUnitsProcessed"] = "총 처리 수량",
        ["ThroughputPerHour"] = "시간당 처리량",
        ["ThroughputPerDay"] = "일일 처리량",
        ["AvailableTime"] = "가용 시간",
        ["UsedTime"] = "사용 시간",
        ["ProductionTime"] = "생산 시간",
        ["ChangeoverTime"] = "전환 시간",
        ["IdleTime"] = "유휴 시간",
        ["DownTime"] = "다운타임",
        ["PlannedOperatingTime"] = "계획 가동 시간",
        ["ActualOperatingTime"] = "실제 가동 시간",
        ["PlannedProductionQty"] = "계획 생산량",
        ["ActualProductionQty"] = "실제 생산량",
        ["GoodProductQty"] = "양품 수량",
        ["DefectQty"] = "불량 수량",
        ["Availability"] = "가동률 (OEE 구성요소)",
        ["Performance"] = "성능률 (OEE 구성요소)",
        ["Quality"] = "양품률 (OEE 구성요소)",
        ["OEE"] = "종합 설비 효율 = 가동률 × 성능률 × 양품률",
        ["ConstraintType"] = "TOC 제약 유형",
        ["IsConstraining"] = "현재 제약 공정 여부",
        ["CurrentTocStep"] = "TOC 5단계 중 현재 단계",
        ["IsDrum"] = "Drum(제약 공정) 여부",
        ["HasBuffer"] = "버퍼 보유 여부",
        ["BufferSize"] = "버퍼 크기",
        ["CurrentBufferLevel"] = "현재 버퍼 수준",

        // -- Call --
        ["CallType"] = "Call 유형 (WaitForCompletion 등)",
        ["SensorDelay"] = "센서 지연 (ms)",
        ["StandardExecutionTime"] = "표준 실행 시간 (초)",
        ["ActualExecutionTime"] = "실제 실행 시간 (초)",
        ["MinExecutionTime"] = "최소 실행 시간 (초)",
        ["MaxExecutionTime"] = "최대 실행 시간 (초)",
        ["SimulateApiCall"] = "API 호출 시뮬레이션 활성화",
        ["MockApiResponse"] = "Mock API 응답 데이터",
        ["ApiResponseCode"] = "API 응답 코드",

        // =====================================================================
        // 02. Control (제어)
        // =====================================================================

        // -- System --
        ["EnableAutoTagGeneration"] = "IO 태그 자동 생성 활성화",
        ["TagPrefix"] = "태그 이름 접두사",
        ["TagNamingFormat"] = "태그 이름 생성 포맷 ({SystemId}_{WorkId}_{Signal})",
        ["NameTransform"] = "이름 변환 규칙 (UpperCase/LowerCase/CamelCase 등)",
        ["PlcVendor"] = "PLC 제조사",
        ["PlcIpAddress"] = "PLC IP 주소",
        ["PlcPort"] = "PLC 통신 포트",
        ["CommunicationTimeout"] = "PLC 통신 타임아웃",
        ["RetryAttempts"] = "재시도 횟수",
        ["TagMatchMode"] = "태그 매칭 모드 (ByAddress/ByName)",
        ["EnableAddressValidation"] = "태그 주소 유효성 검증",
        ["CaseSensitiveMatching"] = "대소문자 구분 매칭",
        ["EnableSafetyInterlock"] = "안전 인터록 시스템 활성화",
        ["EmergencyStopEnabled"] = "비상 정지 활성화",
        ["SafetyDoorCheck"] = "안전문 확인 필수 여부",
        ["LightCurtainCheck"] = "라이트 커튼 확인 필수 여부",
        ["TwoHandControl"] = "양수 조작 필수 여부",
        ["SafetyTimeoutSeconds"] = "안전 타임아웃 (초)",
        ["EnableHealthCheck"] = "PLC 헬스체크 활성화",
        ["HealthCheckInterval"] = "헬스체크 주기",
        ["EnableHeartbeat"] = "하트비트 신호 활성화",
        ["HeartbeatInterval"] = "하트비트 주기",

        // -- Flow --
        ["FlowControlEnabled"] = "Flow 제어 활성화",
        ["FlowPriority"] = "Flow 실행 우선순위",

        // -- Work --
        ["EnableHardwareControl"] = "하드웨어 제어 활성화",
        ["ControlMode"] = "제어 모드 (Sequential/Parallel/Conditional)",
        ["InTagName"] = "입력 태그 이름",
        ["InTagAddress"] = "입력 태그 PLC 주소",
        ["OutTagName"] = "출력 태그 이름",
        ["OutTagAddress"] = "출력 태그 PLC 주소",
        ["WorkTimeout"] = "작업 실행 타임아웃",
        ["EnableTimeout"] = "타임아웃 적용 여부",
        ["TimeoutAction"] = "타임아웃 시 동작 (Abort/Retry/Continue)",
        ["RequiresSafetyCheck"] = "실행 전 안전 점검 필수",
        ["InterlockConditions"] = "안전 인터록 조건 목록",
        ["EnableMotionControl"] = "모션 제어 활성화",
        ["MotionControlMode"] = "모션 모드 (Position/Velocity/Torque/Pulse)",
        ["TargetPosition"] = "목표 위치",
        ["TargetVelocity"] = "목표 속도",
        ["Acceleration"] = "가속도",
        ["Deceleration"] = "감속도",
        ["CurrentState"] = "현재 실행 상태",
        ["LastExecutionTime"] = "마지막 실행 시각",
        ["ExecutionCount"] = "총 실행 횟수",
        ["ErrorCount"] = "에러 발생 횟수",

        // -- Call --
        ["EnableRetry"] = "실패 시 자동 재시도 활성화",
        ["MaxRetryCount"] = "최대 재시도 횟수",
        ["RetryDelayMs"] = "재시도 간격 (ms)",
        ["CallTimeout"] = "Call 실행 타임아웃",
        ["WaitForCompletion"] = "완료 대기 여부",
        ["EnableConditional"] = "조건부 실행 활성화",
        ["ConditionExpression"] = "실행 조건 표현식",

        // =====================================================================
        // 03. Monitoring (모니터링)
        // =====================================================================

        // -- System --
        ["EnableRealTimeMonitoring"] = "실시간 모니터링 활성화",
        ["MonitoringIntervalMs"] = "모니터링 업데이트 주기 (ms)",
        ["EnableTagMonitoring"] = "PLC 태그 모니터링 활성화",
        ["TagRefreshIntervalMs"] = "태그 값 갱신 주기 (ms)",

        // -- Flow --
        ["MonitoringTags"] = "모니터링 대상 태그 목록",
        ["EnableAutoRefresh"] = "자동 갱신 활성화",

        // -- Work --
        ["CurrentProgress"] = "현재 진행률 (0.0~1.0)",

        // -- Call --
        ["LastStartedAt"] = "마지막 시작 시각",

        // =====================================================================
        // 04. Logging (로깅)
        // =====================================================================

        // -- System --
        ["EnableLogging"] = "실행 이력 로깅 활성화",
        ["LogToFile"] = "파일 로깅 활성화",
        ["LogToDatabase"] = "데이터베이스 로깅 활성화",
        ["LogFilePath"] = "로그 파일 경로",
        ["RetentionDays"] = "로그 보존 기간 (일)",
        ["ErrorDefinitions"] = "에러 정의 목록 (이름|태그주소|값타입)",

        // -- Flow --
        ["BottleneckThresholdMultiplier"] = "병목 감지 배수 (평균 × N배 초과 시 병목)",
        ["MinSampleSize"] = "통계 분석 최소 샘플 수",

        // -- Work (Welford 통계) --
        ["GoingCount"] = "실행 횟수 (통계용)",
        ["AverageDuration"] = "평균 실행 시간 (Welford 알고리즘)",
        ["M2"] = "Welford M2 중간값 (분산 계산용)",
        ["StdDevDuration"] = "실행 시간 표준편차",

        // =====================================================================
        // 05. Maintenance (유지보수)
        // =====================================================================

        // -- System --
        ["EnableErrorLogging"] = "에러 로깅 활성화",
        ["ErrorLogPath"] = "에러 로그 디렉토리",
        ["ErrorRetentionDays"] = "에러 기록 보존 기간 (일)",
        ["AutoAcknowledgeErrors"] = "에러 자동 확인 처리",
        ["EnableErrorAlarm"] = "에러 알람 활성화",
        ["CriticalErrorAlarmSound"] = "심각 에러 시 알람 소리",
        ["ErrorAlarmThreshold"] = "알람 트리거 임계 횟수",

        // -- Flow --
        ["EnableDeviceTracking"] = "설비 추적 활성화",
        ["DeviceNames"] = "연결 설비 이름 목록",

        // -- Work/Call --
        ["DeviceName"] = "연결 설비 이름",
        ["ErrorText"] = "에러 메시지",
        ["EnableErrorRetry"] = "에러 시 재시도 활성화",

        // =====================================================================
        // 06. HMI (화면 구성)
        // =====================================================================

        // -- System --
        ["EnableHMI"] = "HMI 시스템 활성화",
        ["DefaultLayout"] = "기본 화면 레이아웃",
        ["DefaultTheme"] = "기본 UI 테마",
        ["WebServerPort"] = "웹 서버 포트",
        ["EnableHttps"] = "HTTPS/SSL 활성화",
        ["EnableSignalR"] = "SignalR 실시간 통신 활성화",
        ["SignalRHub"] = "SignalR 허브 URL",
        ["EnableRealtimeUpdate"] = "실시간 상태 업데이트 활성화",
        ["UpdateIntervalMs"] = "상태 업데이트 주기 (ms)",
        ["ShowSystemOverview"] = "시스템 개요 패널 표시",
        ["ShowFlowList"] = "Flow 목록 표시",
        ["ShowAlarmPanel"] = "알람 패널 표시",
        ["ShowPerformanceMetrics"] = "성능 지표 표시",
        ["GridColumns"] = "그리드 레이아웃 열 수",
        ["EnablePermissionCheck"] = "권한 검사 활성화",
        ["RequireLoginForOperation"] = "조작 시 로그인 필수",
        ["DefaultPermission"] = "기본 사용자 권한 레벨",
        ["EnableOperationLog"] = "조작 로그 기록 활성화",
        ["EnableConfirmation"] = "중요 동작 2단계 확인",
        ["ConfirmCriticalOperations"] = "위험 조작 확인 필수",
        ["ButtonCooldownSeconds"] = "버튼 연속 클릭 방지 간격 (초)",
        ["OperationLogRetentionDays"] = "조작 로그 보존 기간 (일)",
        ["LogAllOperations"] = "모든 조작 로그 기록",
        ["CustomCSS"] = "커스텀 CSS",
        ["CustomLogo"] = "커스텀 로고 URL/경로",
        ["ApplicationTitle"] = "애플리케이션 타이틀",

        // -- Flow --
        ["EnableFlowHMI"] = "Flow HMI 컨트롤 활성화",
        ["ShowFlowControl"] = "Flow 제어 버튼 표시",
        ["AllowManualStart"] = "수동 시작 허용",
        ["AllowManualStop"] = "수동 정지 허용",
        ["FlowIcon"] = "Flow 아이콘 (Font Awesome)",
        ["FlowColor"] = "Flow UI 색상 클래스",

        // -- Work --
        ["EnableWorkHMI"] = "Work HMI 컨트롤 활성화",
        ["ShowWorkControl"] = "Work 제어 버튼 표시",
        ["AllowManualTrigger"] = "Work 수동 트리거 허용",
        ["AllowSkip"] = "Work 건너뛰기 허용",
        ["WorkIcon"] = "Work 아이콘 (Font Awesome)",
        ["WorkColor"] = "Work UI 색상 클래스",
        ["ShowDuration"] = "소요 시간 표시",
        ["ShowProgress"] = "진행률 표시",
        ["ShowStatus"] = "상태 표시",

        // -- Call --
        ["EnableCallHMI"] = "Call HMI 컨트롤 활성화",
        ["ShowCallControl"] = "Call 제어 버튼 표시",
        ["AllowManualExecution"] = "Call 수동 실행 허용 (테스트용)",
        ["ShowIOValues"] = "I/O 값 표시",

        // -- Device --
        ["DeviceType"] = "설비 유형 (Motor/Cylinder/Valve 등)",
        ["EnableDeviceControl"] = "설비 직접 제어 활성화",
        ["AllowManualMode"] = "수동 조작 모드 허용",

        // =====================================================================
        // 07. Quality (품질)
        // =====================================================================

        // -- System --
        ["EnableSPC"] = "SPC(통계적 공정 관리) 활성화",
        ["DefaultChartType"] = "기본 관리도 유형",
        ["SamplingPlanType"] = "샘플링 계획 유형 (FixedInterval 등)",
        ["SamplingInterval"] = "샘플링 간격 (초)",
        ["SamplingCount"] = "개수 기준 샘플링 (N개당 1회)",
        ["SubgroupSize"] = "서브그룹 크기",
        ["MinSubgroupsForAnalysis"] = "분석 시작 최소 서브그룹 수",
        ["ControlChartType"] = "관리도 유형 (XbarR 등)",
        ["AutoCalculateLimits"] = "관리 한계 자동 계산",
        ["SigmaMultiplier"] = "시그마 배수 (기본 3σ)",
        ["USL"] = "상한 규격 한계 (Upper Spec Limit)",
        ["LSL"] = "하한 규격 한계 (Lower Spec Limit)",
        ["TargetValue"] = "목표값",
        ["EnableWesternElectricRules"] = "Western Electric 이상 감지 규칙 활성화",
        ["EnabledRules"] = "활성화된 이상 감지 규칙 목록",
        ["EnableProcessCapability"] = "공정 능력 분석 (Cp/Cpk) 활성화",
        ["TargetCpk"] = "목표 Cpk (1.33 = 4σ 수준)",
        ["WarningCpk"] = "경고 Cpk (1.0 = 3σ 수준)",
        ["EnableQualityAlarms"] = "품질 알람 활성화",
        ["AlarmRetentionDays"] = "알람 기록 보존 기간 (일)",
        ["AutoAcknowledgeInfo"] = "Info 수준 알람 자동 확인",
        ["DataRetentionDays"] = "품질 데이터 보존 기간 (일)",
        ["ArchiveOldData"] = "오래된 데이터 아카이브",

        // -- Flow --
        ["EnableFlowQuality"] = "Flow 품질 관리 활성화",

        // -- Work --
        ["EnableQuality"] = "품질 모니터링 활성화",
        ["CharacteristicType"] = "품질 특성 유형 (계량형/계수형)",
        ["CharacteristicName"] = "품질 특성 이름 (예: 두께, 불량률)",
        ["Unit"] = "단위 (mm, % 등)",
        ["CurrentCp"] = "현재 Cp 지수",
        ["CurrentCpk"] = "현재 Cpk 지수",
        ["CurrentMean"] = "현재 공정 평균",
        ["CurrentStdDev"] = "현재 표준편차",
        ["TotalSubgroups"] = "수집된 서브그룹 수",
        ["IsOutOfControl"] = "공정 이탈 여부",
        ["LastViolationTime"] = "마지막 규칙 위반 시각",
        ["ViolationCount"] = "규칙 위반 누적 횟수",

        // -- Call --
        ["EnableQualityCheck"] = "Call 품질 검사 활성화",
        ["InspectionType"] = "검사 유형 (Visual/Dimensional/Functional)",

        // =====================================================================
        // 08. CostAnalysis (원가 분석)
        // =====================================================================

        // -- System --
        ["EnableCostAnalysis"] = "원가 분석 활성화",
        ["EnableCostSimulation"] = "원가 시뮬레이션 활성화",
        ["DefaultCurrency"] = "기본 통화 (KRW 등)",
        ["EnableOEETracking"] = "OEE 추적 활성화",
        ["OEECalculationInterval"] = "OEE 계산 주기",
        ["EnableCapacitySimulation"] = "생산 능력 시뮬레이션 활성화",
        ["ProductionLineCount"] = "생산 라인 수",
        ["ShiftPattern"] = "교대 패턴 (OneShift/TwoShift/ThreeShift/Continuous)",
        ["ShiftDuration"] = "교대 시간",
        ["EnableBOMTracking"] = "BOM(자재 명세서) 추적 활성화",
        ["EnableInventorySimulation"] = "재고 시뮬레이션 활성화",
        ["EnableCollisionDetection"] = "충돌 감지 활성화",
        ["EnableQualityTracking"] = "품질 추적 활성화",
        ["TargetYieldRate"] = "목표 수율 (1.0 = 100%)",
        ["TargetDefectRate"] = "목표 불량률 (0.0 = 0%)",

        // -- Work --
        ["EstimatedDuration"] = "예상 작업 소요 시간",
        ["StandardCycleTime"] = "표준 사이클 타임 (초)",
        ["RecordStateChanges"] = "상태 변경 기록 여부",
        ["EnableResourceContention"] = "자원 경합 감지 활성화",
        ["ResourceLockDuration"] = "자원 잠금 시간",
        ["WorkerCount"] = "필요 작업자 수",
        ["SkillLevel"] = "요구 숙련도 (Novice/Intermediate/Advanced/Expert)",
        ["ReworkQty"] = "재작업 수량",
        ["ReworkRate"] = "재작업률 (%)",
        ["LaborCostPerHour"] = "시간당 인건비",
        ["EquipmentCostPerHour"] = "시간당 설비비",
        ["OverheadCostPerHour"] = "시간당 간접비",
        ["UtilityCostPerHour"] = "시간당 유틸리티 비용",
        ["YieldRate"] = "수율 (1.0 = 100%)",
        ["DefectRate"] = "불량률 (0.0 = 0%)",
        ["TotalMaterialCost"] = "총 자재비",
        ["TotalLaborCost"] = "총 인건비",
        ["TotalEquipmentCost"] = "총 설비비",
        ["TotalOverheadCost"] = "총 간접비",
        ["TotalCost"] = "총 작업 원가",
        ["UnitCost"] = "단위당 원가",
    };
}
