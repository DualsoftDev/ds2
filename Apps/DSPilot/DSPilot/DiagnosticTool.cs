using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using DSPilot.Engine;
using EngineFlowAnalysis = DSPilot.Engine.FlowAnalysis;
using DSPilot.Services;
using Microsoft.Data.Sqlite;

namespace DSPilot;

/// <summary>
/// PLC 데이터베이스 및 Flow DAG 진단 도구
/// </summary>
public static class DiagnosticTool
{
    /// <summary>
    /// Flow DAG 분석 결과를 상세히 출력
    /// </summary>
    public static void DiagnoseFlowDag(DsProjectService projectService, string? flowNameFilter = null)
    {
        Console.WriteLine("=== Flow DAG Analysis ===");
        Console.WriteLine();

        if (!projectService.IsLoaded)
        {
            Console.WriteLine("ERROR: Project not loaded");
            return;
        }

        var allFlows = projectService.GetAllFlows()
            .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
            .Where(f => flowNameFilter == null || f.Name.Contains(flowNameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"Analyzing {allFlows.Count} flows...");
        Console.WriteLine();

        var store = GetDsStore(projectService);

        foreach (var flow in allFlows)
        {
            Console.WriteLine($"Flow: {flow.Name}");
            Console.WriteLine("".PadLeft(80, '='));

            var works = Queries.worksOf(flow.Id, store);
            Console.WriteLine($"  Total Works: {works.Length}");

            foreach (var work in works)
            {
                var calls = Queries.callsOf(work.Id, store);
                var arrows = Queries.arrowCallsOf(work.Id, store);

                Console.WriteLine($"\n  Work: {work.Name}");
                Console.WriteLine($"    Calls: {calls.Length}");
                foreach (var call in calls)
                {
                    Console.WriteLine($"      - {call.Name}");
                }

                Console.WriteLine($"    Arrows: {arrows.Length}");
                foreach (var arrow in arrows)
                {
                    var sourceCall = calls.FirstOrDefault(c => c.Id == arrow.SourceId);
                    var targetCall = calls.FirstOrDefault(c => c.Id == arrow.TargetId);
                    var sourceName = sourceCall?.Name ?? $"[Unknown:{arrow.SourceId}]";
                    var targetName = targetCall?.Name ?? $"[Unknown:{arrow.TargetId}]";
                    Console.WriteLine($"      {sourceName} -> {targetName}");
                }
            }

            // 전체 Call/Arrow 수집
            var allCalls = works.SelectMany(w => Queries.callsOf(w.Id, store)).ToList();
            var allArrows = works.SelectMany(w => Queries.arrowCallsOf(w.Id, store)).ToList();

            Console.WriteLine($"\n  Total Calls (all Works): {allCalls.Count}");
            Console.WriteLine($"  Total Arrows (all Works): {allArrows.Count}");

            // FlowAnalysis 실행
            try
            {
                var result = EngineFlowAnalysis.analyzeFlow(flow, store);

                Console.WriteLine($"\n  DAG Analysis Result:");
                Console.WriteLine($"    Representative Work: {result.RepresentativeWorkName ?? "None"}");

                // Head Call 정보
                var headCallName = Microsoft.FSharp.Core.FSharpOption<Ds2.Core.Call>.get_IsSome(result.HeadCall)
                    ? result.HeadCall.Value.Name
                    : "None";
                Console.WriteLine($"    Head Call: {headCallName} (Total: {result.HeadCount})");

                // Tail Call 정보
                var tailCallName = Microsoft.FSharp.Core.FSharpOption<Ds2.Core.Call>.get_IsSome(result.TailCall)
                    ? result.TailCall.Value.Name
                    : "None";
                Console.WriteLine($"    Tail Call: {tailCallName} (Total: {result.TailCount})");

                // 복수 Head/Tail 경고
                if (result.HeadCount > 1)
                    Console.WriteLine($"    [WARNING] Multiple heads detected, using first only");
                if (result.TailCount > 1)
                    Console.WriteLine($"    [WARNING] Multiple tails detected, using first only");

                Console.WriteLine($"    MovingStartName: {result.MovingStartName ?? "None"}");
                Console.WriteLine($"    MovingEndName: {result.MovingEndName ?? "None"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  ERROR: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("=== Analysis Complete ===");
    }

    private static DsStore GetDsStore(DsProjectService projectService)
    {
        var storeField = typeof(DsProjectService).GetField("_store",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (storeField == null)
            throw new InvalidOperationException("Failed to access DsStore from DsProjectService");

        var store = storeField.GetValue(projectService) as DsStore;
        if (store == null)
            throw new InvalidOperationException("DsStore is null");

        return store;
    }
    public static void DiagnosePlcDatabase(string dbPath)
    {
        Console.WriteLine("=== PLC Database Diagnostic ===");
        Console.WriteLine($"DB Path: {dbPath}");
        Console.WriteLine();

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"ERROR: Database file not found at {dbPath}");
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            // 1. Check table existence
            Console.WriteLine("1. Checking tables...");
            var tables = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }
            Console.WriteLine($"   Found tables: {string.Join(", ", tables)}");
            Console.WriteLine();

            // 2. Check plcTagLog count
            Console.WriteLine("2. Checking plcTagLog...");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM plcTagLog;";
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                Console.WriteLine($"   Total rows: {count}");
            }

            // 3. Check dateTime format
            Console.WriteLine();
            Console.WriteLine("3. Checking dateTime column...");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(plcTagLog);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    var type = reader.GetString(2);
                    if (name == "dateTime")
                    {
                        Console.WriteLine($"   Column 'dateTime' type: {type}");
                    }
                }
            }

            // 4. Check MIN/MAX dateTime
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(dateTime), MAX(dateTime) FROM plcTagLog;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var minVal = reader.IsDBNull(0) ? "NULL" : reader.GetValue(0).ToString();
                    var maxVal = reader.IsDBNull(1) ? "NULL" : reader.GetValue(1).ToString();
                    Console.WriteLine($"   MIN(dateTime): {minVal}");
                    Console.WriteLine($"   MAX(dateTime): {maxVal}");

                    // Try to parse as DateTime
                    if (!reader.IsDBNull(0))
                    {
                        try
                        {
                            var minDateTime = reader.GetDateTime(0);
                            Console.WriteLine($"   MIN as DateTime: {minDateTime:yyyy-MM-dd HH:mm:ss.fff}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"   ERROR parsing MIN as DateTime: {ex.Message}");
                        }
                    }

                    if (!reader.IsDBNull(1))
                    {
                        try
                        {
                            reader.Read(); // Re-read to get MAX
                        }
                        catch { }
                    }
                }
            }

            // 5. Sample data
            Console.WriteLine();
            Console.WriteLine("4. Sample data (first 5 rows)...");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, plcTagId, dateTime, value FROM plcTagLog ORDER BY id LIMIT 5;";
                using var reader = cmd.ExecuteReader();
                int row = 0;
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var tagId = reader.GetInt32(1);
                    var dateTimeVal = reader.GetValue(2);
                    var value = reader.IsDBNull(3) ? "NULL" : reader.GetString(3);

                    Console.WriteLine($"   Row {++row}: ID={id}, TagId={tagId}, DateTime={dateTimeVal} (type: {dateTimeVal?.GetType().Name ?? "null"}), Value={value}");
                }
            }

            // 6. Check plcTag
            Console.WriteLine();
            Console.WriteLine("5. Checking plcTag...");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM plcTag;";
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                Console.WriteLine($"   Total tags: {count}");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name, address FROM plcTag LIMIT 5;";
                using var reader = cmd.ExecuteReader();
                Console.WriteLine("   Sample tags:");
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var name = reader.IsDBNull(1) ? "NULL" : reader.GetString(1);
                    var address = reader.IsDBNull(2) ? "NULL" : reader.GetString(2);
                    Console.WriteLine($"     ID={id}, Name={name}, Address={address}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Diagnostic Complete ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
