namespace DSPilot.Models.Plc;

/// <summary>
/// plcTagLog 의 엣지 1건 — 엣지가 기록된 로그 행의 id 와 시각.
/// <see cref="DSPilot.Repositories.IPlcRepository.FindRisingEdgesWithLogIdAsync"/> 가 반환한다.
/// LogId(plcTagLog.id)는 OEE 정지 onset 의 멱등 키(oeeDowntimeEvent.sourceLogId)로 사용한다 —
/// (detectSource,sourceLogId) 부분 유니크 인덱스가 동일 엣지의 중복 INSERT 를 막는다.
/// At 은 로컬시각(SqliteDateTimeHelpers.FromSqliteUtcString 변환 규칙과 동일).
/// </summary>
public sealed record PlcEdge(long LogId, DateTime At);
