// ============================================================================
// Step 07: Simulation CLI — 인터랙티브 시뮬레이션 콘솔
// ----------------------------------------------------------------------------
// Step 01~06 에서 배운 모든 API 를 조합한 실제 도구.
// Ev2.Runtime.Sim.Console 스타일의 인터랙티브 시뮬레이션.
//
// 키보드 컨트롤:
//   Enter = Root Work 시작
//   Space = Pause / Resume
//   T     = TimeIgnore 토글
//   Home  = Reset
//   End   = Stop + 통계
//   ESC   = 종료
//
// ※ stdin 리다이렉트 시 (전체 실행) 3초 자동 데모 모드
// ============================================================================

using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;

namespace Ds2.Tutorial.Steps;

static class Step07_SimCli
{
    private record WorkInfo(Guid Id, string Name);

    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 07: Simulation CLI ===\n");
        if (silent) return;

        var store = ctx.Store;

        // Duration 설정 (독립 실행 대비)
        store.Works[ctx.W1Id].Properties.Period = TimeSpan.FromMilliseconds(500);
        store.Works[ctx.W2Id].Properties.Period = TimeSpan.FromMilliseconds(800);
        store.Works[ctx.W3Id].Properties.Period = TimeSpan.FromMilliseconds(500);

        var index = SimIndexModule.build(store, 50);
        using var engine = new EventDrivenEngine(index);
        var sim = (ISimulationEngine)engine;

        var works = new WorkInfo[]
        {
            new(ctx.W1Id, "PickPart"),
            new(ctx.W2Id, "WeldJoint"),
            new(ctx.W3Id, "PlacePart"),
        };

        var log = new List<string>();
        sim.WorkStateChanged += (s, e) =>
            log.Add($"[{e.Clock:mm\\:ss\\.ff}] {e.WorkName}: {e.PreviousState} → {e.NewState}");

        sim.SpeedMultiplier = 5.0;

        if (!Console.IsInputRedirected)
            RunInteractive(sim, works, log);
        else
            RunAutoDemo(sim, works, log);
    }

    private static void RunAutoDemo(ISimulationEngine sim, WorkInfo[] works, List<string> log)
    {
        Console.WriteLine("  [Auto Demo] 3초 시뮬레이션...\n");
        sim.Start();

        Thread.Sleep(200);
        // 토큰 투입 후 강제 시작 — 토큰 있어야 후속 Work 로 전달됨
        var token = sim.NextToken();
        sim.SeedToken(works[0].Id, token);
        sim.ForceWorkState(works[0].Id, Status4.Going);
        Console.WriteLine("  Started: PickPart");

        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(500);
            Console.Write("  ");
            PrintStates(sim, works);
            Console.WriteLine();
        }

        sim.Stop();
        PrintFinal(log, sim, works);
    }

    private static void RunInteractive(ISimulationEngine sim, WorkInfo[] works, List<string> log)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  DS2 SIMULATION CLI");
        Console.WriteLine(new string('=', 60));
        Console.ResetColor();
        Console.WriteLine("  PickPart(500ms) → WeldJoint(800ms) → PlacePart(500ms) → [Reset]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [Enter]=Start [Space]=Pause [T]=TimeIgnore [Home]=Reset [End/ESC]=Stop");
        Console.ResetColor();
        Console.WriteLine();

        sim.Start();
        var running = true;
        var paused = false;
        var last = DateTime.MinValue;

        while (running)
        {
            if (Console.KeyAvailable)
            {
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.Escape:
                    case ConsoleKey.End:
                        running = false; break;
                    case ConsoleKey.Enter:
                        foreach (var w in works)
                        {
                            var st = sim.GetWorkState(w.Id);
                            if (st != null && st.Value == Status4.Ready)
                            {
                                // 토큰 투입 + 강제 시작
                                var t = sim.NextToken();
                                sim.SeedToken(w.Id, t);
                                sim.ForceWorkState(w.Id, Status4.Going);
                                Console.WriteLine($"  Started: {w.Name}");
                                break;
                            }
                        }
                        break;
                    case ConsoleKey.Spacebar:
                        paused = !paused;
                        if (paused) { sim.Stop(); Console.WriteLine("  Paused."); }
                        else { sim.Start(); Console.WriteLine("  Resumed."); }
                        break;
                    case ConsoleKey.T:
                        sim.TimeIgnore = !sim.TimeIgnore;
                        Console.WriteLine($"  TimeIgnore: {(sim.TimeIgnore ? "ON" : "OFF")}");
                        break;
                    case ConsoleKey.Home:
                        sim.Reset(); Console.WriteLine("  Reset."); break;
                }
            }
            if (!paused && (DateTime.Now - last).TotalMilliseconds > 200)
            {
                Console.Write("\r  ");
                PrintStates(sim, works);
                Console.Write("   ");
                last = DateTime.Now;
            }
            Thread.Sleep(50);
        }

        sim.Stop();
        Console.WriteLine();
        PrintFinal(log, sim, works);
    }

    private static void PrintStates(ISimulationEngine sim, WorkInfo[] works)
    {
        foreach (var w in works)
        {
            var st = sim.GetWorkState(w.Id);
            var s = st != null ? st.Value.ToString() : "?";
            Console.ForegroundColor = s switch
            {
                "Going" => ConsoleColor.Yellow,
                "Finish" => ConsoleColor.Green,
                "Hold" or "Homing" => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            };
            Console.Write($"{w.Name}:{s}  ");
        }
        Console.ResetColor();
    }

    private static void PrintFinal(List<string> log, ISimulationEngine sim, WorkInfo[] works)
    {
        Console.WriteLine($"\n  상태변화: {log.Count}건");
        foreach (var e in log.TakeLast(8))
            Console.WriteLine($"    {e}");
        Console.WriteLine("\n  최종:");
        foreach (var w in works)
        {
            var st = sim.GetWorkState(w.Id);
            Console.WriteLine($"    {w.Name}: {(st != null ? st.Value.ToString() : "?")}");
        }
    }
}
