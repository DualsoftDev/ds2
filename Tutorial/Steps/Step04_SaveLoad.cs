// ============================================================================
// Step 04: 저장 / 불러오기
// ----------------------------------------------------------------------------
// Step 01~02 에서 만든 프로젝트를 JSON / AASX 포맷으로 저장하고,
// Mermaid / CSV 공개 import API 로도 DsStore 를 생성해 본다.
//
// 포맷:
//   JSON    — DsStore.SaveToFile / LoadFromFile (기본 저장 포맷)
//   Mermaid — MermaidImporter.loadProjectFromFile (문서/다이어그램 기반 import)
//   CSV     — CsvImporter.parseContent / loadProject (PLC 파이프라인 기반 import)
//   AASX    — AasxExporter / AasxImporter (산업 표준 AAS 패키지)
//
// 학습 내용:
//   - DsStore.SaveToFile / LoadFromFile: JSON 직렬화
//   - MermaidImporter.loadProjectFromFile: public API 로 Store 생성
//   - CsvImporter.parseContent / loadProject: public API 로 Store 생성
//   - AasxExporter.exportFromStore: AASX 내보내기
//   - AasxImporter.importIntoStore: AASX 가져오기
//   - Roundtrip 검증 (원본 vs 변환 후 Work 이름 비교)
// ============================================================================

using Ds2.Aasx;
using Ds2.CSV;
using Ds2.Core.Store;
using Ds2.Mermaid;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Ds2.Tutorial.Steps;

static class Step04_SaveLoad
{
    private const string MermaidSample =
        """
        graph TD
            subgraph Assembly["Assembly"]
                subgraph PickPart["PickPart"]
                    PickCall["Robot.Pick"]
                end
                subgraph WeldJoint["WeldJoint"]
                    WeldCall["Welder.Start"]
                end
                subgraph PlacePart["PlacePart"]
                    PlaceCall["Robot.Place"]
                end

                PickPart --> WeldJoint
                WeldJoint --> PlacePart
            end
        """;

    private const string CsvSample =
        """
        Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress
        Assembly,PickPart,Robot,Line,Pick,PickDone,%IX0.0,PickStart,%QX0.0
        Assembly,WeldJoint,Welder,Line,Start,WeldDone,%IX0.1,WeldStart,%QX0.1
        Assembly,PlacePart,Robot,Line,Place,PlaceDone,%IX0.2,PlaceStart,%QX0.2
        """;

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

            // ── Mermaid import (public API) ──────────────────
            Console.WriteLine("  [Mermaid]");
            var mermaidPath = Path.Combine(tempDir, "line.mmd");
            File.WriteAllText(mermaidPath, MermaidSample);
            var mermaidStore = RequireOk(MermaidImporter.loadProjectFromFile(mermaidPath), "Mermaid import");
            PrintStats("Import", mermaidStore);
            Console.WriteLine();

            // ── CSV import (public API) ──────────────────────
            Console.WriteLine("  [CSV]");
            var document = RequireOk(CsvImporter.parseContent(CsvSample), "CSV parse");
            var csvStore = RequireOk(CsvImporter.loadProject(document, "CsvAssembly", "Controller"), "CSV import");
            PrintStats("Import", csvStore);
            Console.WriteLine();

            // ── AASX (산업 표준) ─────────────────────────────
            Console.WriteLine("  [AASX]");
            var aasxPath = Path.Combine(tempDir, "project.aasx");
            AasxExporter.exportFromStore(store, aasxPath, "https://dualsoft.com/", false, false);
            Console.WriteLine($"    Export → {new FileInfo(aasxPath).Length:N0} bytes");

            var aasxStore = DsStore.empty();
            AasxImporter.importIntoStore(aasxStore, aasxPath);
            Console.WriteLine($"    Import → Works={aasxStore.Works.Count}, Arrows={aasxStore.ArrowWorks.Count}");
            PrintMatch(store, aasxStore);
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

    private static T RequireOk<T>(FSharpResult<T, FSharpList<string>> result, string label)
    {
        if (result.IsOk)
            return result.ResultValue;

        var errors = string.Join(Environment.NewLine, result.ErrorValue);
        throw new InvalidOperationException($"{label} failed:{Environment.NewLine}{errors}");
    }

    private static void PrintStats(string label, DsStore store) =>
        Console.WriteLine($"    {label} → Projects={store.Projects.Count}, Systems={store.Systems.Count}, " +
                          $"Flows={store.Flows.Count}, Works={store.Works.Count}, Calls={store.Calls.Count}");

    private static void PrintMatch(DsStore orig, DsStore conv)
    {
        var a = orig.Works.Values.Select(w => w.Name).OrderBy(n => n);
        var b = conv.Works.Values.Select(w => w.Name).OrderBy(n => n);
        Console.WriteLine($"    Roundtrip: {(a.SequenceEqual(b) ? "PASS" : "FAIL")}");
    }
}
