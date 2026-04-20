using System.Collections.Concurrent;
using DSPilot.Models.Plc;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// PLC IO 데이터 수집 및 제공 서비스
/// </summary>
public class PlcIoDataService
{
    private readonly ILogger<PlcIoDataService> _logger;
    private readonly IPlcRepository _plcRepository;
    private readonly ConcurrentDictionary<string, List<PlcTagLogEntity>> _tagHistory = new();
    private const int MaxHistoryCount = 1000;

    public PlcIoDataService(
        ILogger<PlcIoDataService> logger,
        IPlcRepository plcRepository)
    {
        _logger = logger;
        _plcRepository = plcRepository;
    }

    /// <summary>
    /// 특정 태그의 최근 이력 조회
    /// </summary>
    public async Task<List<PlcTagLogEntity>> GetTagHistoryAsync(string tagAddress, int count = 100)
    {
        try
        {
            var logs = await _plcRepository.GetTagLogsAsync(tagAddress, count);
            return logs.OrderBy(l => l.DateTime).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tag history for {TagAddress}", tagAddress);
            return new List<PlcTagLogEntity>();
        }
    }

    /// <summary>
    /// 시간 범위로 태그 이력 조회
    /// </summary>
    public async Task<List<PlcTagLogEntity>> GetTagHistoryByTimeRangeAsync(
        string tagAddress,
        DateTime startTime,
        DateTime endTime)
    {
        try
        {
            var logs = await _plcRepository.GetTagLogsByTimeRangeAsync(tagAddress, startTime, endTime);
            return logs.OrderBy(l => l.DateTime).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tag history for {TagAddress} in time range", tagAddress);
            return new List<PlcTagLogEntity>();
        }
    }

    /// <summary>
    /// 여러 태그의 최근 이력을 동시에 조회
    /// </summary>
    public async Task<Dictionary<string, List<PlcTagLogEntity>>> GetMultipleTagHistoryAsync(
        List<string> tagAddresses,
        int count = 100)
    {
        var result = new Dictionary<string, List<PlcTagLogEntity>>();

        var tasks = tagAddresses.Select(async tagAddress =>
        {
            var logs = await GetTagHistoryAsync(tagAddress, count);
            return (tagAddress, logs);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (tagAddress, logs) in results)
        {
            result[tagAddress] = logs;
        }

        return result;
    }

    /// <summary>
    /// Call의 InTag와 OutTag 이력을 동시에 조회
    /// </summary>
    public async Task<(List<PlcTagLogEntity> InTagLogs, List<PlcTagLogEntity> OutTagLogs)>
        GetCallTagHistoryAsync(string? inTagAddress, string? outTagAddress, int count = 100)
    {
        var inTagTask = !string.IsNullOrEmpty(inTagAddress)
            ? GetTagHistoryAsync(inTagAddress, count)
            : Task.FromResult(new List<PlcTagLogEntity>());

        var outTagTask = !string.IsNullOrEmpty(outTagAddress)
            ? GetTagHistoryAsync(outTagAddress, count)
            : Task.FromResult(new List<PlcTagLogEntity>());

        await Task.WhenAll(inTagTask, outTagTask);

        return (await inTagTask, await outTagTask);
    }
}
