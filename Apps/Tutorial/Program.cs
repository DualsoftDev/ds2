// ============================================================================
// Ds2 코드 이관 튜토리얼
// ----------------------------------------------------------------------------
// 하나의 프로젝트가 Step 1→8 까지 점진적으로 완성되는 구조.
//
//   전체 실행(0): 빈 Store → 엔티티 → 화살표 → 쿼리 → 저장 → 시뮬레이션
//                 → 보고서 → CLI  (하나의 Context 가 쭉 흘러감)
//   개별 실행(N): 해당 Step 직전까지 자동 구성 후 실행
// ============================================================================

using Ds2.Tutorial.Steps;

log4net.Config.BasicConfigurator.Configure();

// ── 메뉴 ───────────────────────────────────────────────────
Console.WriteLine("====================================");
Console.WriteLine("  Ds2 코드 이관 튜토리얼");
Console.WriteLine("====================================");
Console.WriteLine();
Console.WriteLine("  1. 엔티티 추가        — Project/System/Flow/Work 생성");
Console.WriteLine("  2. 화살표 연결        — Start/Reset 화살표");
Console.WriteLine("  3. DsQuery 탐색       — 계층 쿼리 + 역방향 탐색");
Console.WriteLine("  4. 저장 / 불러오기    — JSON/Mermaid/AASX/CSV");
Console.WriteLine("  5. 시뮬레이션 + 토큰  — 엔진 실행 + 토큰 흐름");
Console.WriteLine("  6. 보고서 생성        — HTML/CSV 내보내기");
Console.WriteLine("  7. Simulation CLI     — 인터랙티브 콘솔");
Console.WriteLine("  8. Convert CLI        — 변환 파이프라인");
Console.WriteLine();
Console.Write("스텝 번호 (0=전체): ");

var input = Console.ReadLine()?.Trim() ?? "0";
var step = int.TryParse(input, out var n) ? n : 0;

// ── Step 등록 (번호 → Run 함수) ───────────────────────────
var steps = new Dictionary<int, (string Name, Action<TutorialContext> Run)>
{
    [1] = ("엔티티 추가",        ctx => Step01_AddEntities.Run(ctx)),
    [2] = ("화살표 연결",        ctx => Step02_AddArrows.Run(ctx)),
    [3] = ("DsQuery 탐색",      ctx => Step03_QueryExplore.Run(ctx)),
    [4] = ("저장 / 불러오기",    ctx => Step04_SaveLoad.Run(ctx)),
    [5] = ("시뮬레이션 + 토큰",  ctx => Step05_Simulation.Run(ctx)),
    [6] = ("보고서 생성",        ctx => Step06_Report.Run(ctx)),
    [7] = ("Simulation CLI",    ctx => Step07_SimCli.Run(ctx)),
    [8] = ("Convert CLI",       ctx => Step08_ConvertCli.Run(ctx)),
};

// ── 실행 ───────────────────────────────────────────────────
if (step == 0)
{
    // 전체 실행: 하나의 Context 가 Step 1→8 까지 흘러감
    var ctx = TutorialContext.CreateEmpty();
    foreach (var (k, v) in steps.OrderBy(x => x.Key))
    {
        try
        {
            v.Run(ctx);
            Console.WriteLine($"  >>> PASS\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  >>> FAIL: {ex.Message}\n");
        }
    }
}
else if (steps.TryGetValue(step, out var s))
{
    // 개별 실행: 해당 Step 직전까지 자동 구성
    var ctx = TutorialContext.BuildUpTo(step);
    s.Run(ctx);
}
else
{
    Console.WriteLine($"알 수 없는 스텝: {step}");
}
