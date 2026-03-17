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

        _layout.BlueprintImagePath = $"uploads/{safeName}";
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

    public void RemovePlacement(Guid flowId)
    {
        _layout.FlowPlacements.RemoveAll(p => p.FlowId == flowId);
        ScheduleSave();
    }

    public void SaveLayout()
    {
        ScheduleSave();
    }

    public string GetLayoutJson()
    {
        return JsonSerializer.Serialize(_layout, new JsonSerializerOptions { WriteIndented = true });
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
