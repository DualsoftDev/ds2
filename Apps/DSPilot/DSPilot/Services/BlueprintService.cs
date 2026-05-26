using System.Text.Json;
using DSPilot.Models;

namespace DSPilot.Services;

public class BlueprintService : IDisposable
{
    private readonly string _uploadsDir;
    private readonly string _layoutFilePath;
    private readonly ILogger<BlueprintService> _logger;
    private BlueprintLayout _layout = new();
    private Timer? _debounceTimer;

    public BlueprintLayout Layout => _layout;
    public long ImageVersion { get; private set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public BlueprintService(IWebHostEnvironment env, ILogger<BlueprintService> logger)
    {
        _logger = logger;
        _uploadsDir = Path.Combine(env.WebRootPath, "uploads");
        _layoutFilePath = Path.Combine(_uploadsDir, "layout-data.json");

        if (!Directory.Exists(_uploadsDir))
            Directory.CreateDirectory(_uploadsDir);

        Load();
    }

    public async Task<(int Width, int Height)> SaveBlueprintImageAsync(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var safeName = $"blueprint{ext}";
        var filePath = Path.Combine(_uploadsDir, safeName);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;

        await using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await ms.CopyToAsync(fs);
        }

        _layout.BlueprintImagePath = $"/uploads/{safeName}";
        ImageVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Detect image dimensions from file header
        var (w, h) = ReadImageDimensions(filePath);
        if (w > 0 && h > 0)
        {
            _layout.CanvasWidth = w;
            _layout.CanvasHeight = h;
        }

        Save();
        return (w, h);
    }

    private static (int Width, int Height) ReadImageDimensions(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var header = new byte[24];
        if (stream.Read(header, 0, 24) < 24) return (0, 0);

        // PNG: 89 50 4E 47 ... IHDR chunk has width/height at offset 16-23
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (width, height);
        }

        // JPEG: FF D8 ... find SOF0 (FFC0) or SOF2 (FFC2) marker
        if (header[0] == 0xFF && header[1] == 0xD8)
        {
            stream.Position = 2;
            while (stream.Position < stream.Length - 8)
            {
                var b = stream.ReadByte();
                if (b != 0xFF) continue;
                var marker = stream.ReadByte();
                if (marker == 0xC0 || marker == 0xC2)
                {
                    var buf = new byte[7];
                    if (stream.Read(buf, 0, 7) < 7) break;
                    var height2 = (buf[3] << 8) | buf[4];
                    var width2 = (buf[5] << 8) | buf[6];
                    return (width2, height2);
                }
                else if (marker == 0xD9 || marker == 0xDA) break; // EOI or SOS
                else
                {
                    var lenBuf = new byte[2];
                    if (stream.Read(lenBuf, 0, 2) < 2) break;
                    var len = (lenBuf[0] << 8) | lenBuf[1];
                    if (len < 2) break;
                    stream.Position += len - 2;
                }
            }
        }

        return (0, 0);
    }

    public void UpdatePlacement(FlowPlacement placement)
    {
        var existing = _layout.FlowPlacements.FirstOrDefault(p => p.FlowId == placement.FlowId);
        if (existing != null)
            _layout.FlowPlacements.Remove(existing);
        _layout.FlowPlacements.Add(placement);
        ScheduleSave();
    }

    // 저장 없이 메모리만 업데이트 (Editor 즉시적용 전 임시상태용)
    public void UpdatePlacementLocal(FlowPlacement placement)
    {
        var existing = _layout.FlowPlacements.FirstOrDefault(p => p.FlowId == placement.FlowId);
        if (existing != null)
            _layout.FlowPlacements.Remove(existing);
        _layout.FlowPlacements.Add(placement);
    }

    public void RemovePlacementLocal(Guid flowId)
    {
        _layout.FlowPlacements.RemoveAll(p => p.FlowId == flowId);
    }

    public void RemovePlacement(Guid flowId)
    {
        _layout.FlowPlacements.RemoveAll(p => p.FlowId == flowId);
        ScheduleSave();
    }

    public void SaveLayout()
    {
        ScheduleSave();
    }

    /// <summary>
    /// 주어진 Flow 목록을 캔버스 비율에 맞는 격자로 자동 배치한다.
    /// 기존 FlowPlacements 는 모두 제거하고, GridColumns/GridRows 도 자동 재계산하여 덮어쓴다.
    /// FlowProcessOrder 도 입력 순서로 갱신한다.
    /// </summary>
    public void AutoFillPlacements(IReadOnlyList<(Guid FlowId, string FlowName, string SystemName, Guid SystemId)> orderedFlows)
    {
        _layout.FlowPlacements.Clear();
        if (orderedFlows.Count == 0) return;

        var n = orderedFlows.Count;
        var usableW = _layout.CanvasWidth - _layout.OffsetX - _layout.OffsetRight;
        var usableH = _layout.CanvasHeight - _layout.OffsetY - _layout.OffsetBottom;
        if (usableW <= 0) usableW = Math.Max(1, _layout.CanvasWidth);
        if (usableH <= 0) usableH = Math.Max(1, _layout.CanvasHeight);
        var aspect = (double)usableW / usableH;

        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * aspect)));
        var rows = (int)Math.Ceiling((double)n / cols);
        // 한 줄로 떨어지지 않도록 보정 (cols * rows >= n)
        while (cols * rows < n) rows++;

        _layout.GridColumns = cols;
        _layout.GridRows = rows;

        for (int i = 0; i < n; i++)
        {
            var f = orderedFlows[i];
            _layout.FlowPlacements.Add(new FlowPlacement
            {
                FlowId = f.FlowId,
                SystemId = f.SystemId,
                FlowName = f.FlowName,
                SystemName = f.SystemName,
                Col = i % cols,
                Row = i / cols,
                ColSpan = 1,
                RowSpan = 1,
            });
        }

        _layout.FlowProcessOrder = orderedFlows
            .Select(f => new FlowOrderEntry { FlowId = f.FlowId, FlowName = f.FlowName })
            .ToList();
    }

    /// <summary>
    /// 현재 layout-data.json 의 FlowPlacement Guid 집합이 주어진 Flow Guid 집합과 다른지 판정.
    /// 추가/삭제/리네임으로 한 쪽이라도 다르면 true. layout 이 비어 있으면 (placement 0개) false —
    /// 첫 자동 채움은 AasxFileWatcherService.TryAutoFillBlueprint 가 담당하므로 여기는 stale 판정 전용.
    /// </summary>
    public bool IsFlowSetStale(IEnumerable<Guid> currentFlowIds)
    {
        var placed = _layout.FlowPlacements.Select(p => p.FlowId).ToHashSet();
        if (placed.Count == 0) return false;
        var current = currentFlowIds?.ToHashSet() ?? new HashSet<Guid>();
        return !current.SetEquals(placed);
    }

    /// <summary>
    /// 현재 layout-data.json 을 layout-data_yyyyMMdd_HHmmss[_suffix].json 으로 복사한다.
    /// AASX 자동 동기화로 placement 가 재배치되기 직전, 사용자가 누적한 배치를 백업 보관하기 위한 용도.
    /// 실패해도 best-effort — null 반환.
    /// </summary>
    public string? BackupCurrentLayoutFile(string? suffix = null)
    {
        try
        {
            if (!File.Exists(_layoutFilePath)) return null;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var name = string.IsNullOrWhiteSpace(suffix)
                ? $"layout-data_{stamp}.json"
                : $"layout-data_{stamp}_{suffix}.json";
            var dest = Path.Combine(_uploadsDir, name);
            File.Copy(_layoutFilePath, dest, overwrite: false);
            _logger.LogInformation("Layout backed up: {Src} → {Dst}", _layoutFilePath, dest);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Layout backup failed");
            return null;
        }
    }

    /// <summary>
    /// FlowPlacements / FlowProcessOrder / Grid 만 새 Flow 목록으로 다시 채운다.
    /// 도면 메타(이미지 / Canvas / Offset) 는 그대로 보존.
    /// 호출 측에서 백업이 필요하면 먼저 <see cref="BackupCurrentLayoutFile"/> 을 호출할 것.
    /// 즉시 파일로 flush 한다 (debounce 거치지 않음).
    /// </summary>
    public void ResetFlowPlacementsAndAutoFill(
        IReadOnlyList<(Guid FlowId, string FlowName, string SystemName, Guid SystemId)> orderedFlows)
    {
        AutoFillPlacements(orderedFlows);
        Save();
    }

    public string GetLayoutJson()
    {
        return JsonSerializer.Serialize(_layout, new JsonSerializerOptions { WriteIndented = true });
    }

    public void LoadLayoutJson(string json)
    {
        var imported = JsonSerializer.Deserialize<BlueprintLayout>(json)
            ?? throw new InvalidOperationException("Invalid layout JSON");
        _layout.GridColumns = imported.GridColumns;
        _layout.GridRows = imported.GridRows;
        _layout.OffsetX = imported.OffsetX;
        _layout.OffsetY = imported.OffsetY;
        _layout.OffsetRight = imported.OffsetRight;
        _layout.OffsetBottom = imported.OffsetBottom;
        _layout.FlowPlacements = imported.FlowPlacements;
        _layout.FlowProcessOrder = imported.FlowProcessOrder ?? [];
        Save();
    }

    private void ScheduleSave()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => Save(), null, 500, Timeout.Infinite);
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_layout, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_layoutFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save layout data");
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        Save(); // flush pending changes
    }

    private void Load()
    {
        if (!File.Exists(_layoutFilePath)) return;
        try
        {
            var json = File.ReadAllText(_layoutFilePath);
            _layout = JsonSerializer.Deserialize<BlueprintLayout>(json) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load layout data");
            _layout = new();
        }
    }
}
