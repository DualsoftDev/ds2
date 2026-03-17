using System.Text.Json;
using DSPilot.Models;

namespace DSPilot.Services;

public class BlueprintService
{
    private readonly string _uploadsDir;
    private readonly string _layoutFilePath;
    private readonly ILogger<BlueprintService> _logger;
    private BlueprintLayout _layout = new();

    public BlueprintLayout Layout => _layout;

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

        // Save to temp memory to read dimensions, then write to file
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;

        await using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await ms.CopyToAsync(fs);
        }

        _layout.BlueprintImagePath = $"uploads/{safeName}";
        Save();

        // Return (0,0) - actual dimensions will be detected via JS in the browser
        return (0, 0);
    }

    public void UpdatePlacement(FlowPlacement placement)
    {
        var existing = _layout.FlowPlacements.FirstOrDefault(p => p.FlowId == placement.FlowId);
        if (existing != null)
            _layout.FlowPlacements.Remove(existing);
        _layout.FlowPlacements.Add(placement);
        Save();
    }

    public void RemovePlacement(Guid flowId)
    {
        _layout.FlowPlacements.RemoveAll(p => p.FlowId == flowId);
        Save();
    }

    public void SaveLayout()
    {
        Save();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_layout, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_layoutFilePath, json);
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
