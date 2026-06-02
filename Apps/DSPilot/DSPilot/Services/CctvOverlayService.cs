using System.Text.Json;
using DSPilot.Models.Cctv;

namespace DSPilot.Services;

/// <summary>
/// CCTV 설비 오버레이(영상 위 라벨) 영속 서비스. 싱글톤.
///
/// 영속은 plc.db 가 아님 — <c>%WebRoot%/uploads/cctv-overlays.json</c> (BlueprintService 선례 복제).
/// 이유: DatabaseLifecycleService.RebuildDatabaseAsync 가 plc.db 파일을 통째로 삭제하므로
/// 사용자가 그린 오버레이 좌표(사용자 자산)는 plc.db 에 두면 안 됨 (doc/20 §1, BlueprintLayout 과 동일 판단).
///
/// mutation(Upsert/Delete/Rename) 은 BlueprintController 즉시영속 CRUD 선례에 따라 동기 즉시 저장한다.
/// </summary>
public class CctvOverlayService
{
    private readonly string _uploadsDir;
    private readonly string _filePath;
    private readonly ILogger<CctvOverlayService> _logger;
    private readonly object _gate = new();
    private CctvOverlayFile _file = new();

    public CctvOverlayService(IWebHostEnvironment env, ILogger<CctvOverlayService> logger)
    {
        _logger = logger;
        // BlueprintService 와 동일 디렉터리/IWebHostEnvironment 방식: WebRoot/uploads.
        _uploadsDir = Path.Combine(env.WebRootPath, "uploads");
        _filePath = Path.Combine(_uploadsDir, "cctv-overlays.json");

        if (!Directory.Exists(_uploadsDir))
            Directory.CreateDirectory(_uploadsDir);

        Load();
    }

    /// <summary>전체 오버레이 목록 (복사본).</summary>
    public IReadOnlyList<CctvOverlay> GetAll()
    {
        lock (_gate)
            return _file.Overlays.ToList();
    }

    /// <summary>특정 카메라(Name)의 오버레이 목록. 비교는 대소문자 무시(카메라 Name 은 URL-safe).</summary>
    public IReadOnlyList<CctvOverlay> GetByCamera(string cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            return [];
        lock (_gate)
            return _file.Overlays
                .Where(o => string.Equals(o.CameraName, cameraName, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>
    /// Upsert (id 기준). 동일 id 존재 시 교체, 없으면 추가. 즉시 동기 저장.
    /// </summary>
    public CctvOverlay Upsert(CctvOverlay overlay)
    {
        if (string.IsNullOrWhiteSpace(overlay.Id))
            throw new ArgumentException("overlay.Id 가 비어 있습니다.", nameof(overlay));

        lock (_gate)
        {
            _file.Overlays.RemoveAll(o => o.Id == overlay.Id);
            _file.Overlays.Add(overlay);
            Save();
        }
        return overlay;
    }

    /// <summary>id 로 삭제. 삭제된 개수 반환. 즉시 동기 저장.</summary>
    public int Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return 0;
        lock (_gate)
        {
            var removed = _file.Overlays.RemoveAll(o => o.Id == id);
            if (removed > 0) Save();
            return removed;
        }
    }

    /// <summary>
    /// 카메라 개명(rename) 대비: oldName 의 오버레이들의 CameraName 을 newName 으로 갱신(보존).
    /// 삭제(Delete)와 명확히 구분 — 좌표/바인딩은 유지하고 FK 만 옮긴다 (doc/20 §3 좌표 유실 방지).
    /// 변경된 개수 반환. 즉시 동기 저장.
    /// </summary>
    public int RenameCamera(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)
            || string.Equals(oldName, newName, StringComparison.Ordinal))
            return 0;

        lock (_gate)
        {
            var affected = 0;
            for (var i = 0; i < _file.Overlays.Count; i++)
            {
                if (string.Equals(_file.Overlays[i].CameraName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    _file.Overlays[i] = _file.Overlays[i] with { CameraName = newName };
                    affected++;
                }
            }
            if (affected > 0) Save();
            return affected;
        }
    }

    /// <summary>
    /// 더 이상 존재하지 않는 카메라에 묶인 오버레이를 정리(prune). 카메라 개명과 구분 — 호출 측에서
    /// 개명은 <see cref="RenameCamera"/>, 영구 삭제만 prune 으로 처리해야 좌표 유실을 막을 수 있다.
    /// 유효 카메라 이름 집합을 받아 그 외 카메라의 오버레이를 제거하고, 제거된 개수를 반환.
    /// </summary>
    public int PruneOrphans(IEnumerable<string> validCameraNames)
    {
        var valid = new HashSet<string>(validCameraNames ?? [], StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var removed = _file.Overlays.RemoveAll(o => !valid.Contains(o.CameraName));
            if (removed > 0) Save();
            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _file = JsonSerializer.Deserialize<CctvOverlayFile>(json) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cctv-overlays.json");
            _file = new();
        }
    }

    // 호출 측에서 _gate 를 잡은 상태로만 호출한다.
    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cctv-overlays.json");
        }
    }
}
