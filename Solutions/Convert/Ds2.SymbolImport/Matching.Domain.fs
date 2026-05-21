namespace Ds2.SymbolImport.Matching

/// <summary>
/// PLC 변수의 I/O 방향 타입
/// </summary>
type IODirection =
    | Input
    | Output

/// <summary>
/// PLC I/O 타입 (표준 심볼 구조의 레벨 2)
/// PLC 심볼 표준화 사양 기반
/// </summary>
type IOType =
    // 출력 타입
    | O     // 범용 출력
    | Y     // 출력 (미쓰비시)
    | Q     // 출력 (지멘스/LS)
    | SOL   // 솔레노이드
    // 입력 타입
    | LS    // 리밋 스위치
    | PS    // 압력 스위치
    | RS    // 릴레이 스위치
    | I     // 범용 입력
    | X     // 입력 (미쓰비시)
    | M     // 메모리/마커
    // 기타 타입
    | D     // 데이터
    | B     // 비트
    | Unknown

/// <summary>
/// 파싱된 PLC 심볼 - 레벨 수에 제한 없이 동적으로 처리
/// </summary>
type ParsedSymbol =
    { OriginalName: string
      Levels: string list }

/// <summary>
/// 논리명, 물리 주소, 방향을 가진 PLC 변수
/// </summary>
type Variable =
    { LogicalName: string
      PhysicalAddress: string
      Direction: IODirection }

/// <summary>
/// 입력-출력 페어링에 사용되는 매칭 전략
/// </summary>
type MatchingStrategy =
    | MappingSet     // 패턴 기반 매핑 세트 (디바이스 키워드 → API → 입출력 키워드)
    | Transformer    // Transformer 기반 시맨틱 매칭 (ONNX 모델)
    | NotMatched     // 매칭 실패

/// <summary>
/// Flow, Work, Device, API 정보를 포함하고
/// 입출력 주소가 페어링된 디바이스 API 매핑
/// 다중 입력 지원: 하나의 출력에 여러 입력 가능
/// </summary>
type DeviceApiMapping =
    { Flow: string
      Work: string
      DeviceName: string
      Api: string
      // 출력 정보
      OutputVariableName: string
      OutputPhysicalAddress: string
      // 입력 정보 (매칭됨 - 다중 가능)
      InputVariableNames: string list
      InputPhysicalAddresses: string list
      // 매칭 메타데이터
      MatchingStrategy: MatchingStrategy
      MatchingConfidence: float }

/// <summary>
/// 그룹화된 API를 가진 디바이스 정보
/// </summary>
type DeviceInfo =
    { DeviceName: string
      Flow: string
      Work: string
      Apis: DeviceApiMapping list
      GroupName: string }

/// <summary>
/// 공통 특성을 공유하는 디바이스 그룹
/// </summary>
type DeviceGroup =
    { GroupName: string
      Devices: DeviceInfo list }

/// <summary>
/// API에 대한 출력/입력 키워드 매핑
/// </summary>
type ApiKeywordMapping =
    { OutputKeywords: string list
      InputKeywords: string list }

/// <summary>
/// 디바이스 키워드와 API별 입출력 키워드 매핑 세트
/// 주소 패턴으로 입출력 방향 판정
/// </summary>
type MappingSet =
    { Name: string
      DeviceKeywords: string list
      Apis: Map<string, ApiKeywordMapping>
      /// 출력 주소 패턴 (예: ["%QX"; "%Q"; "Q:"; "O:"; "Y"])
      OutputAddressPatterns: string list
      /// 입력 주소 패턴 (예: ["%IX"; "%I"; "I:"; "X"])
      InputAddressPatterns: string list }
