using System.Text.Json;
using AasxEditor.Models;
using Microsoft.Data.Sqlite;

namespace AasxEditor.Services;

public class SqliteMetadataStore : IAasMetadataStore
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteMetadataStore(string dbPath = "aas_metadata.db")
    {
        _connectionString = $"Data Source={dbPath}";
    }

    private async Task<SqliteConnection> GetConnectionAsync()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync();
        }
        return _connection;
    }

    public async Task InitializeAsync()
    {
        var conn = await GetConnectionAsync();

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS AasxFiles (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                FileName    TEXT NOT NULL,
                FilePath    TEXT NOT NULL,
                ImportedAt  TEXT NOT NULL,
                ShellCount  INTEGER NOT NULL DEFAULT 0,
                SubmodelCount INTEGER NOT NULL DEFAULT 0,
                JsonContent TEXT
            )
            """);

        // 기존 테이블에 JsonContent 컬럼이 없으면 추가 (마이그레이션)
        try { await ExecuteNonQueryAsync(conn, "ALTER TABLE AasxFiles ADD COLUMN JsonContent TEXT"); }
        catch { /* 이미 존재 */ }

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS AasEntities (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId          INTEGER NOT NULL,
                EntityType      TEXT NOT NULL,
                IdShort         TEXT NOT NULL,
                AasId           TEXT,
                JsonPath        TEXT NOT NULL,
                SemanticId      TEXT,
                Value           TEXT,
                ValueType       TEXT,
                ParentJsonPath  TEXT,
                PropertiesJson  TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (FileId) REFERENCES AasxFiles(Id) ON DELETE CASCADE
            )
            """);

        // 검색 성능용 인덱스
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_entities_file ON AasEntities(FileId)");
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_entities_type ON AasEntities(EntityType)");
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_entities_semantic ON AasEntities(SemanticId)");
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_entities_idshort ON AasEntities(IdShort)");
    }

    // === 파일 관리 ===

    public async Task<AasxFileRecord> AddFileAsync(string fileName, string filePath, int shellCount, int submodelCount, string? jsonContent = null)
    {
        var conn = await GetConnectionAsync();
        var now = DateTime.UtcNow.ToString("o");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AasxFiles (FileName, FilePath, ImportedAt, ShellCount, SubmodelCount, JsonContent)
            VALUES (@fn, @fp, @at, @sc, @smc, @jc);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@fn", fileName);
        cmd.Parameters.AddWithValue("@fp", filePath);
        cmd.Parameters.AddWithValue("@at", now);
        cmd.Parameters.AddWithValue("@sc", shellCount);
        cmd.Parameters.AddWithValue("@smc", submodelCount);
        cmd.Parameters.AddWithValue("@jc", (object?)jsonContent ?? DBNull.Value);

        var id = (long)(await cmd.ExecuteScalarAsync())!;
        return new AasxFileRecord
        {
            Id = id,
            FileName = fileName,
            FilePath = filePath,
            ImportedAt = DateTime.UtcNow,
            ShellCount = shellCount,
            SubmodelCount = submodelCount
        };
    }

    public async Task UpdateJsonContentAsync(long fileId, string jsonContent)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AasxFiles SET JsonContent = @jc WHERE Id = @id";
        cmd.Parameters.AddWithValue("@jc", jsonContent);
        cmd.Parameters.AddWithValue("@id", fileId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetJsonContentAsync(long fileId)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT JsonContent FROM AasxFiles WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", fileId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    public async Task RemoveFileAsync(long fileId)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AasxFiles WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", fileId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<AasxFileRecord>> GetFilesAsync()
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, FileName, FilePath, ImportedAt, ShellCount, SubmodelCount FROM AasxFiles ORDER BY ImportedAt DESC";

        var result = new List<AasxFileRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AasxFileRecord
            {
                Id = reader.GetInt64(0),
                FileName = reader.GetString(1),
                FilePath = reader.GetString(2),
                ImportedAt = DateTime.Parse(reader.GetString(3)),
                ShellCount = reader.GetInt32(4),
                SubmodelCount = reader.GetInt32(5)
            });
        }
        return result;
    }

    // === 엔티티 관리 ===

    public async Task AddEntitiesAsync(long fileId, IEnumerable<AasEntityRecord> entities)
    {
        var conn = await GetConnectionAsync();
        using var transaction = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AasEntities (FileId, EntityType, IdShort, AasId, JsonPath, SemanticId, Value, ValueType, ParentJsonPath, PropertiesJson)
            VALUES (@fid, @et, @ids, @aid, @jp, @sid, @val, @vt, @pjp, @pj)
            """;

        var pFileId = cmd.Parameters.Add("@fid", SqliteType.Integer);
        var pEntityType = cmd.Parameters.Add("@et", SqliteType.Text);
        var pIdShort = cmd.Parameters.Add("@ids", SqliteType.Text);
        var pAasId = cmd.Parameters.Add("@aid", SqliteType.Text);
        var pJsonPath = cmd.Parameters.Add("@jp", SqliteType.Text);
        var pSemanticId = cmd.Parameters.Add("@sid", SqliteType.Text);
        var pValue = cmd.Parameters.Add("@val", SqliteType.Text);
        var pValueType = cmd.Parameters.Add("@vt", SqliteType.Text);
        var pParentJsonPath = cmd.Parameters.Add("@pjp", SqliteType.Text);
        var pPropertiesJson = cmd.Parameters.Add("@pj", SqliteType.Text);

        foreach (var e in entities)
        {
            pFileId.Value = fileId;
            pEntityType.Value = e.EntityType;
            pIdShort.Value = e.IdShort;
            pAasId.Value = (object?)e.AasId ?? DBNull.Value;
            pJsonPath.Value = e.JsonPath;
            pSemanticId.Value = (object?)e.SemanticId ?? DBNull.Value;
            pValue.Value = (object?)e.Value ?? DBNull.Value;
            pValueType.Value = (object?)e.ValueType ?? DBNull.Value;
            pParentJsonPath.Value = (object?)e.ParentJsonPath ?? DBNull.Value;
            pPropertiesJson.Value = e.PropertiesJson;
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task RemoveEntitiesByFileAsync(long fileId)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AasEntities WHERE FileId = @fid";
        cmd.Parameters.AddWithValue("@fid", fileId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<AasEntityRecord>> GetEntitiesByFileAsync(long fileId)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AasEntities WHERE FileId = @fid ORDER BY JsonPath";
        cmd.Parameters.AddWithValue("@fid", fileId);
        return await ReadEntitiesAsync(cmd);
    }

    // === 검색 ===

    public async Task<List<AasEntityRecord>> SearchAsync(AasSearchQuery query)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (query.FileId.HasValue)
        {
            where.Add("FileId = @fid");
            cmd.Parameters.AddWithValue("@fid", query.FileId.Value);
        }
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            where.Add("EntityType = @et");
            cmd.Parameters.AddWithValue("@et", query.EntityType);
        }
        if (!string.IsNullOrEmpty(query.SemanticId))
        {
            where.Add("SemanticId LIKE @sid");
            cmd.Parameters.AddWithValue("@sid", $"%{query.SemanticId}%");
        }
        if (!string.IsNullOrEmpty(query.Text))
        {
            where.Add("(IdShort LIKE @txt OR Value LIKE @txt OR AasId LIKE @txt)");
            cmd.Parameters.AddWithValue("@txt", $"%{query.Text}%");
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT * FROM AasEntities {whereClause} ORDER BY FileId, JsonPath LIMIT 500";

        return await ReadEntitiesAsync(cmd);
    }

    // === 일괄 편집 ===

    public async Task<int> BatchUpdateValueAsync(AasSearchQuery query, string newValue)
    {
        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (query.FileId.HasValue)
        {
            where.Add("FileId = @fid");
            cmd.Parameters.AddWithValue("@fid", query.FileId.Value);
        }
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            where.Add("EntityType = @et");
            cmd.Parameters.AddWithValue("@et", query.EntityType);
        }
        if (!string.IsNullOrEmpty(query.SemanticId))
        {
            where.Add("SemanticId LIKE @sid");
            cmd.Parameters.AddWithValue("@sid", $"%{query.SemanticId}%");
        }
        if (!string.IsNullOrEmpty(query.Text))
        {
            where.Add("(IdShort LIKE @txt OR Value LIKE @txt OR AasId LIKE @txt)");
            cmd.Parameters.AddWithValue("@txt", $"%{query.Text}%");
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"UPDATE AasEntities SET Value = @newVal {whereClause}";
        cmd.Parameters.AddWithValue("@newVal", newValue);

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> BatchUpdateFieldByIdsAsync(IEnumerable<long> entityIds, string field, string newValue)
    {
        // 허용된 컬럼만 업데이트 가능 (SQL 인젝션 방지)
        var allowedFields = new HashSet<string> { "Value", "SemanticId", "ValueType", "Category" };
        if (!allowedFields.Contains(field))
            throw new ArgumentException($"일괄 편집 대상이 아닌 필드: {field}");

        var ids = entityIds.ToList();
        if (ids.Count == 0) return 0;

        var conn = await GetConnectionAsync();
        using var cmd = conn.CreateCommand();

        // 파라미터 바인딩으로 ID 목록 전달
        var idParams = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var pName = $"@id{i}";
            idParams.Add(pName);
            cmd.Parameters.AddWithValue(pName, ids[i]);
        }

        cmd.CommandText = $"UPDATE AasEntities SET {field} = @newVal WHERE Id IN ({string.Join(",", idParams)})";
        cmd.Parameters.AddWithValue("@newVal", newValue);

        return await cmd.ExecuteNonQueryAsync();
    }

    // === Helpers ===

    private static async Task<List<AasEntityRecord>> ReadEntitiesAsync(SqliteCommand cmd)
    {
        var result = new List<AasEntityRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AasEntityRecord
            {
                Id = reader.GetInt64(0),
                FileId = reader.GetInt64(1),
                EntityType = reader.GetString(2),
                IdShort = reader.GetString(3),
                AasId = reader.IsDBNull(4) ? null : reader.GetString(4),
                JsonPath = reader.GetString(5),
                SemanticId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Value = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValueType = reader.IsDBNull(8) ? null : reader.GetString(8),
                ParentJsonPath = reader.IsDBNull(9) ? null : reader.GetString(9),
                PropertiesJson = reader.GetString(10)
            });
        }
        return result;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
