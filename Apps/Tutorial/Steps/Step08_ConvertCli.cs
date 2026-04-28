// ============================================================================
// Step 08: Convert CLI — AASX 변환 파이프라인
// ----------------------------------------------------------------------------
// Step 04 에서 배운 변환을 파이프라인으로 조합한 실제 도구.
// Ev2.Import.PlcToAASX.CLI 스타일.
//
// 파이프라인:
//   1) JSON → AASX         (내보내기)
//   2) JSON → AASX → JSON  (Roundtrip 검증)
// ============================================================================

using Ds2.Aasx;
using Ds2.Core.Store;

namespace Ds2.Tutorial.Steps;

static class Step08_ConvertCli
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 08: Convert CLI ===\n");
        if (silent) return;

        var store = ctx.Store;
        var tempDir = Path.Combine(Path.GetTempPath(), $"ds2_convert_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Section("SOURCE");
            Stats("Input", store);
            Console.WriteLine();

            // ── Pipeline 1: JSON → AASX ──────────────────────
            Section("PIPELINE 1: JSON → AASX");

            var jsonPath = Path.Combine(tempDir, "project.json");
            store.SaveToFile(jsonPath);
            File("JSON Export", jsonPath);

            var aasxPath = Path.Combine(tempDir, "project.aasx");
            AasxExporter.exportFromStore(store, aasxPath, "https://dualsoft.com/", false, false);
            File("AASX Export", aasxPath);

            var aasxStore = DsStore.empty();
            AasxImporter.importIntoStore(aasxStore, aasxPath);
            Stats("AASX Import", aasxStore);
            Console.WriteLine();

            // ── Pipeline 2: Roundtrip ─────────────────────────
            Section("PIPELINE 2: JSON → AASX → JSON (Roundtrip)");

            var aasxRt = Path.Combine(tempDir, "roundtrip.aasx");
            AasxExporter.exportFromStore(store, aasxRt, "https://dualsoft.com/", false, false);
            File("AASX Export", aasxRt);

            var rtStore = DsStore.empty();
            AasxImporter.importIntoStore(rtStore, aasxRt);
            Stats("AASX Import", rtStore);

            var jsonRt = Path.Combine(tempDir, "roundtrip.json");
            rtStore.SaveToFile(jsonRt);
            File("JSON Export", jsonRt);

            var orig = store.Works.Values.Select(w => w.Name).OrderBy(n => n);
            var rt = rtStore.Works.Values.Select(w => w.Name).OrderBy(n => n);
            Console.ForegroundColor = orig.SequenceEqual(rt) ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"    Roundtrip: {(orig.SequenceEqual(rt) ? "PASS" : "FAIL")}");
            Console.ResetColor();
            Console.WriteLine();

            // ── Summary ──────────────────────────────────────
            Section("SUMMARY");
            var files = Directory.GetFiles(tempDir).OrderBy(f => f).ToArray();
            Console.WriteLine($"    생성: {files.Length}개 파일");
            foreach (var f in files)
                Console.WriteLine($"    {Path.GetExtension(f).ToUpper().PadRight(6)} {Path.GetFileName(f),-28} {new FileInfo(f).Length,8:N0} bytes");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        Console.WriteLine();
        Console.WriteLine("  전체 튜토리얼 완료!");
    }

    private static void Section(string t)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($">> {t}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('-', 50));
        Console.ResetColor();
    }

    private static void Stats(string label, DsStore s) =>
        Console.WriteLine($"    [{label}] Projects={s.Projects.Count}, Systems={s.Systems.Count}, Works={s.Works.Count}, Arrows={s.ArrowWorks.Count}");

    private static void File(string label, string path)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"    [{label}] ");
        Console.ResetColor();
        Console.WriteLine($"OK ({new FileInfo(path).Length:N0} bytes) → {Path.GetFileName(path)}");
    }
}
