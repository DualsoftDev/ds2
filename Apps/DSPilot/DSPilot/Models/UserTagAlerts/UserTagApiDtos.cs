namespace DSPilot.Models.UserTagAlerts;

// 격리형 호스팅 UserTag(이상발생 관리) API DTO. 전역 camelCase 정책으로 직렬화.
// 시각은 서버 로컬(=DB/표시 tz)로 미리 변환해 내려보내 클라이언트 이중변환을 피한다.

public record UserTagSnapshotDto(
    string PeriodPreset,
    string PeriodStartLocal,
    string PeriodEndLocal,
    string Granularity,
    string BucketLabel,
    int TotalCount,
    int Page,
    int MaxPage,
    int PageSize,
    List<UtAlertDto> Alerts,
    List<UtBucketDto> Buckets,
    List<UtTopDto> TopRows,
    Dictionary<string, int> LevelCounts,   // 키(Info/Warning/Error)는 그대로 — DictionaryKeyPolicy 미설정
    int ActiveErrorCount,
    int TodayErrorCount,
    string? LastAlertAtLocal,
    List<UtDefinitionDto> Definitions,
    List<string> SystemOptions);

public record UtAlertDto(
    string OccurredAtLocal,   // "yyyy-MM-dd HH:mm:ss.fff" (테이블은 앞 19자, CSV 는 전체)
    string LogLevel,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue,
    string ActualValue);

public record UtBucketDto(string BucketStartIso, string Level, int Count);

public record UtTopDto(string Name, string Level, int Count);

public record UtDefinitionDto(
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue);
