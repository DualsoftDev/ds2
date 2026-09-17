// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Kpi;

/// <summary>
/// 구 DB(plc.db · oee.db) 정리. doc/30 §9.
/// <para>
/// 새 DSPilot 은 설치 시 처음부터 수집한다 — 마이그레이션은 없다. 구 파일이 남아 있으면
/// 디스크만 차지하고(현장 실측 590MB 중 527MB 가 빈 페이지였다) 혼선을 준다.
/// </para>
/// <para>
/// ★ 이 정리는 <b>구 파이프라인이 제거된 뒤</b>에만 켠다(doc/30 §13 7단계). 전환 기간에는
/// 구 엔진이 여전히 plc.db 를 SSOT 로 쓰므로 기본값은 꺼짐이다. 설정 키는 <c>Kpi:PurgeLegacyDatabases</c>.
/// </para>
/// </summary>
public sealed class LegacyDbPurge
{
    /// <summary>삭제 대상 파일명(공유 폴더 기준).</summary>
    private static readonly string[] LegacyFiles = ["plc.db", "oee.db"];

    /// <summary>SQLite 사이드카 확장자 — 본체와 함께 지워야 재생성 시 옛 WAL 이 되살아나지 않는다.</summary>
    private static readonly string[] Sidecars = ["", "-wal", "-shm"];

    private readonly ILogger<LegacyDbPurge> _logger;

    public LegacyDbPurge(ILogger<LegacyDbPurge> logger) => _logger = logger;

    /// <summary>
    /// 구 DB 파일을 지운다. 이미 없으면 무해한 no-op.
    /// 삭제 전 커넥션 풀을 비워 열린 핸들이 파일 잠금을 유지하지 않게 한다.
    /// </summary>
    /// <returns>삭제한 파일 수.</returns>
    public int Purge()
    {
        var dirs = new List<string> { SharedPaths.SharedDirectory };

        // 구 버전이 쓰던 %ProgramData%\Dualsoft\DSPilot\plc.db 도 함께 정리.
        if (OperatingSystem.IsWindows())
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DualSoft", "DSPilot");
            dirs.Add(legacyDir);
        }

        SqliteConnection.ClearAllPools();

        int deleted = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in LegacyFiles)
            {
                foreach (var sfx in Sidecars)
                {
                    var path = Path.Combine(dir, name + sfx);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        File.Delete(path);
                        deleted++;
                        _logger.LogInformation("[LegacyDbPurge] deleted {Path}", path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[LegacyDbPurge] delete failed — {Path}", path);
                    }
                }
            }
        }

        if (deleted > 0)
            _logger.LogInformation("[LegacyDbPurge] {Count} legacy database file(s) removed", deleted);
        return deleted;
    }

    /// <summary>구 DB 가 아직 남아 있는지 — 설정 화면·진단이 사용자에게 알릴 때 쓴다.</summary>
    public bool AnyLegacyPresent()
    {
        foreach (var name in LegacyFiles)
        {
            if (File.Exists(Path.Combine(SharedPaths.SharedDirectory, name))) return true;
        }
        return false;
    }
}
