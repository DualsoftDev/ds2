namespace DSPilot.Models.UserTagAlerts;

/// <summary>
/// userTagAlertLog 한 행 — UI/Repository 공용.
/// </summary>
public sealed record UserTagAlertRecord(
    long Id,
    DateTime OccurredAt,           // UTC
    Guid SystemId,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue,
    string ActualValue,
    long? SourceLogId);

/// <summary>시계열 버킷 — 차트의 1포인트.</summary>
public sealed record UserTagAlertBucket(
    DateTime BucketStart,          // UTC, 버킷 시작 시각
    string LogLevel,
    int Count);

/// <summary>태그명 별 Top-N 행.</summary>
public sealed record UserTagAlertTopRow(
    string Name,
    string LogLevel,
    int Count);
