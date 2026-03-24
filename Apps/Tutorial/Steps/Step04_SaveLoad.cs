// ============================================================================
// Step 04: 저장 / 불러오기
// ----------------------------------------------------------------------------
// Step 01~02 에서 만든 프로젝트를 4개 포맷으로 저장하고 다시 불러온다.
//
// 포맷:
//   JSON    — DsStore.SaveToFile / LoadFromFile (기본 저장 포맷)
//   Mermaid — MermaidExporter / MermaidImporter (다이어그램)
//   AASX    — AasxExporter / AasxImporter (산업 표준)
//   CSV     — CsvExporter / CsvImporter (표 형식)
//
// 학습 내용:
//   - 각 Exporter / Importer 의 API 패턴
//   - Result<T,E>: F# Result → C# IsOk / ResultValue / ErrorValue
//   - MermaidImporter.parseFile + preview: 단계별 Import
//   - Roundtrip 검증 (원본 vs 변환 후 비교)
// ============================================================================

using Ds2.Aasx;
using Ds2.CSV;
using Ds2.Mermaid;
using Ds2.Store;

namespace Ds2.Tutorial.Steps;

static class Step04_SaveLoad
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 04: 저장 / 불러오기 ===\n");
        if (silent) return;

        var store = ctx.Store;
        var tempDir = Path.Combine(Path.GetTempPath(), $"ds2_step04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // ── JSON ─────────────────────────────────────────
            Console.WriteLine("  [JSON]");
            var jsonPath = Path.Combine(tempDir, "project.json");
            store.SaveToFile(jsonPath);
            Console.WriteLine($"    Save → {new FileInfo(jsonPath).Length:N0} bytes");

            var jsonStore = DsStore.empty();
            jsonStore.LoadFromFile(jsonPath);
            Console.WriteLine($"    Load → Works={jsonStore.Works.Count}, Arrows={jsonStore.ArrowWorks.Count}");
            PrintMatch(store, jsonStore);
            Console.WriteLine();

            // ── Mermaid ──────────────────────────────────────
            Console.WriteLine("  [Mermaid]");
            var mdPath = Path.Combine(tempDir, "project.md");
            MermaidExporter.saveProjectToFile(store, mdPath);
            Console.WriteLine($"    Export → {new FileInfo(mdPath).Length:N0} bytes");

            // 단계별: parseFile → preview (Import 전 확인)
            var parsed = MermaidImporter.parseFile(mdPath);
            if (parsed.IsOk)
            {
                var preview = MermaidImporter.preview(parsed.ResultValue, ImportLevel.SystemLevel);
                Console.WriteLine($"    Preview → Flows={preview.FlowNames.Length}, Works={preview.WorkNames.Length}, Arrows={preview.ArrowWorksCount}");
            }

            var mImport = MermaidImporter.loadProjectFromFile(mdPath);
            if (mImport.IsOk)
                Console.WriteLine($"    Import → Works={mImport.ResultValue.Works.Count}");
            Console.WriteLine();

            // ── AASX (산업 표준) ─────────────────────────────
            Console.WriteLine("  [AASX]");
            var aasxPath = Path.Combine(tempDir, "project.aasx");
            AasxExporter.exportFromStore(store, aasxPath);
            Console.WriteLine($"    Export → {new FileInfo(aasxPath).Length:N0} bytes");

            var aasxStore = DsStore.empty();
            AasxImporter.importIntoStore(aasxStore, aasxPath);
            Console.WriteLine($"    Import → Works={aasxStore.Works.Count}, Arrows={aasxStore.ArrowWorks.Count}");
            PrintMatch(store, aasxStore);
            Console.WriteLine();

            // ── CSV ──────────────────────────────────────────
            Console.WriteLine("  [CSV]");
            var csvPath = Path.Combine(tempDir, "project.csv");
            CsvExporter.saveProjectToFile(store, csvPath);
            Console.WriteLine($"    Export → {new FileInfo(csvPath).Length:N0} bytes");
            Console.WriteLine($"    내용: {CsvExporter.projectToCsv(store, ctx.ProjectId).Trim()}");
            Console.WriteLine();

            // ── 요약 ─────────────────────────────────────────
            Console.WriteLine("  [파일 목록]");
            foreach (var f in Directory.GetFiles(tempDir).OrderBy(f => f))
                Console.WriteLine($"    {Path.GetExtension(f).ToUpper().PadRight(6)} {new FileInfo(f).Length,8:N0} bytes  {Path.GetFileName(f)}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        Console.WriteLine();
        Console.WriteLine("  → 다음은 이 프로젝트로 시뮬레이션을 돌린다.");
    }

    private static void PrintMatch(DsStore orig, DsStore conv)
    {
        var a = orig.Works.Values.Select(w => w.Name).OrderBy(n => n);
        var b = conv.Works.Values.Select(w => w.Name).OrderBy(n => n);
        Console.WriteLine($"    Roundtrip: {(a.SequenceEqual(b) ? "PASS" : "FAIL")}");
    }
}
