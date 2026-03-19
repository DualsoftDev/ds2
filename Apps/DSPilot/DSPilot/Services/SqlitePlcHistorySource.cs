using DSPilot.Abstractions;
using DSPilot.Models.Plc;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// SQLite 기반 PLC 이력 데이터 소스 (기존 PlcRepository 래핑)
/// </summary>
public class SqlitePlcHistorySource : IPlcHistorySource
{
    private readonly IPlcRepository _plcRepo;
    private readonly ILogger<SqlitePlcHistorySource> _logger;

    public SqlitePlcHistorySource(
        IPlcRepository plcRepo,
        ILogger<SqlitePlcHistorySource> logger)
    {
        _plcRepo = plcRepo;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<List<PlcTagLogEntity>> GetLogsInRangeAsync(DateTime startExclusive, DateTime endInclusive)
    {
        return _plcRepo.GetLogsInRangeAsync(startExclusive, endInclusive);
    }

    /// <inheritdoc />
    public Task<List<DateTime>> FindRisingEdgesAsync(string address, DateTime startTime, DateTime endTime)
    {
        return _plcRepo.FindRisingEdgesAsync(address, startTime, endTime);
    }

    /// <inheritdoc />
    public Task<List<PlcTagLogEntity>> GetMultipleTagRisingEdgesInRangeAsync(
        List<string> addresses,
        DateTime startTime,
        DateTime endTime)
    {
        return _plcRepo.GetMultipleTagRisingEdgesInRangeAsync(addresses, startTime, endTime);
    }

    /// <inheritdoc />
    public Task<List<PlcTagLogEntity>> GetTagLogsByAddressInRangeAsync(
        string address,
        DateTime startTime,
        DateTime endTime)
    {
        return _plcRepo.GetTagLogsByAddressInRangeAsync(address, startTime, endTime);
    }

    /// <inheritdoc />
    public Task<DateTime?> GetOldestLogDateTimeAsync()
    {
        return _plcRepo.GetOldestLogDateTimeAsync();
    }

    /// <inheritdoc />
    public Task<DateTime?> GetLatestLogDateTimeAsync()
    {
        return _plcRepo.GetLatestLogDateTimeAsync();
    }
}
