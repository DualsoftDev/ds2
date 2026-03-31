// ============================================================================
// Step 06: 보고서 생성
// ----------------------------------------------------------------------------
// Step 05 의 시뮬레이션 결과를 수집하여 보고서를 만들고 내보낸다.
//
// 학습 내용:
//   - StateChangeRecord: F# record → C# 생성자로만 생성
//   - ReportService.fromStateChanges: 상태 기록 → SimulationReport
//   - SimulationReportModule.getWorks: Work 항목 추출
//   - ReportEntryModule: 표시명, Going 시간, 상태 변경 횟수
//   - ReportService.exportAuto: 확장자 자동 감지
//   - ExportOptionsModule.defaults + ReportService.export: 명시적 포맷
//
// F# record → C# 규칙:
//   new StateChangeRecord(nodeId, nodeName, nodeType, systemId, state, timestamp)
//   ※ object initializer { NodeId = ... } 불가 (읽기 전용)
// ============================================================================

using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Exporters;
using Ds2.Runtime.Sim.Report.Model;

namespace Ds2.Tutorial.Steps;

static class Step06_Report
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 06: 보고서 생성 ===\n");
        if (silent) return;

        var store = ctx.Store;

        // Duration + TokenRole 설정 (독립 실행 대비)
        store.Works[ctx.W1Id].Properties.Duration = TimeSpan.FromMilliseconds(300);
        store.Works[ctx.W2Id].Properties.Duration = TimeSpan.FromMilliseconds(500);
        store.Works[ctx.W3Id].Properties.Duration = TimeSpan.FromMilliseconds(300);
        store.Works[ctx.W1Id].TokenRole = TokenRole.Source;

        var index = SimIndexModule.build(store, 50);
        using var engine = new EventDrivenEngine(index);
        var sim = (ISimulationEngine)engine;

        // ── 1. 상태 변경 수집 ────────────────────────────────
        var records = new List<StateChangeRecord>();
        var startTime = DateTime.Now;

        sim.WorkStateChanged += (s, e) =>
            records.Add(new StateChangeRecord(
                nodeId:    e.WorkGuid.ToString(),
                nodeName:  e.WorkName,
                nodeType:  "Work",
                systemId:  "",
                state:     e.NewState.ToString(),
                timestamp: startTime + e.Clock
            ));

        Console.WriteLine("  [수집] 20배속, 1.5초간 실행...");
        sim.SpeedMultiplier = 20.0;
        sim.Start();

        // Source Work 에 토큰 투입 → 자동 순환
        Thread.Sleep(200);
        var token = sim.NextToken();
        sim.SeedToken(ctx.W1Id, token);

        Thread.Sleep(1500);
        sim.Stop();
        var endTime = DateTime.Now;
        Console.WriteLine($"    수집된 상태 변경: {records.Count}건");
        Console.WriteLine();

        // ── 2. 보고서 생성 ───────────────────────────────────
        var report = ReportService.fromStateChanges(startTime, endTime, records);
        Console.WriteLine("  [보고서]");
        Console.WriteLine($"    Entries:  {report.Entries.Length}");
        Console.WriteLine($"    Duration: {report.Metadata.TotalDuration}");
        Console.WriteLine();

        // ── 3. Work 요약 ─────────────────────────────────────
        Console.WriteLine("  [Work 요약]");
        foreach (var entry in SimulationReportModule.getWorks(report))
        {
            var name = ReportEntryModule.getDisplayName(entry);
            var goingTime = ReportEntryModule.getTotalGoingTime(entry);
            var changes = ReportEntryModule.getStateChangeCount(entry);
            Console.WriteLine($"    {name}: Going {goingTime:F2}s, 변경 {changes}회");
        }
        Console.WriteLine();

        // ── 4. HTML / CSV 내보내기 ───────────────────────────
        var htmlPath = Path.Combine(Path.GetTempPath(), "ds2_report.html");
        var csvPath  = Path.Combine(Path.GetTempPath(), "ds2_report.csv");

        var htmlResult = ReportService.exportAuto(report, htmlPath);
        var csvOptions = ExportOptionsModule.defaults(ExportFormat.Csv, csvPath);
        var csvResult  = ReportService.export(report, csvOptions);

        Console.WriteLine("  [내보내기]");
        Console.WriteLine($"    HTML: {htmlResult}");
        Console.WriteLine($"    CSV:  {csvResult}");

        if (File.Exists(htmlPath))
            Console.WriteLine($"    HTML 크기: {new FileInfo(htmlPath).Length:N0} bytes");
        if (File.Exists(csvPath))
            Console.WriteLine($"    CSV 크기:  {new FileInfo(csvPath).Length:N0} bytes");

        foreach (var p in new[] { htmlPath, csvPath })
            if (File.Exists(p)) File.Delete(p);

        Console.WriteLine();
        Console.WriteLine("  → 다음은 이 모든 것을 인터랙티브 콘솔로 조합한다.");
    }
}
