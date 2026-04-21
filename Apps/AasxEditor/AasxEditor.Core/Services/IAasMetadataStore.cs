using AasxEditor.Models;

namespace AasxEditor.Services;

/// <summary>
/// AASX 메타데이터 저장소 추상화 — SQLite, Redis 등 교체 가능
/// </summary>
public interface IAasMetadataStore : IAsyncDisposable
{
    /// <summary>DB 초기화 (테이블 생성 등)</summary>
    Task InitializeAsync();

    // === 파일 관리 ===
    Task<AasxFileRecord> AddFileAsync(string fileName, string filePath, int shellCount, int submodelCount, string? jsonContent = null);
    Task UpdateJsonContentAsync(long fileId, string jsonContent);
    Task<string?> GetJsonContentAsync(long fileId);
    Task RemoveFileAsync(long fileId);
    Task<List<AasxFileRecord>> GetFilesAsync();

    // === 엔티티 관리 ===
    Task AddEntitiesAsync(long fileId, IEnumerable<AasEntityRecord> entities);
    Task RemoveEntitiesByFileAsync(long fileId);
    Task<List<AasEntityRecord>> GetEntitiesByFileAsync(long fileId);

    // === 검색 ===
    Task<List<AasEntityRecord>> SearchAsync(AasSearchQuery query);

    // === 일괄 편집 ===
    Task<int> BatchUpdateValueAsync(AasSearchQuery query, string newValue);
    Task<int> BatchUpdateFieldByIdsAsync(IEnumerable<long> entityIds, string field, string newValue);
}
