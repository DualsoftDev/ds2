// ============================================================================
// Step 05: 시뮬레이션 + 토큰
// ----------------------------------------------------------------------------
// Step 01~02 에서 만든 프로젝트에 Duration 과 Token 을 설정하고 시뮬레이션.
//
// 학습 내용:
//   - WorkProperties.Duration: Work 실행 시간 (TimeSpan)
//   - SimIndexModule.build(store, tickMs): SimIndex 빌드
//   - EventDrivenEngine + ISimulationEngine
//   - SpeedMultiplier, TimeIgnore
//   - WorkStateChanged / TokenEvent 이벤트
//   - TokenRole.Source → 토큰 발행
//   - GraphValidator: 그래프 사전 검증 (6종)
//     · findUnresetWorks: Reset 화살표 누락
//     · findDeadlockCandidates: 순환 의존 데드락
//     · findSourceCandidates: Source 미지정 후보
//     · findSourcesWithPredecessors: Source인데 선행자 있음
//     · findGroupWorksWithoutIgnore: Group에 Ignore 누락
//     · findTokenUnreachableWorks: 토큰 도달 불가
//
// 핵심 네임스페이스:
//   Ds2.Runtime.Sim.Engine       — EventDrivenEngine, ISimulationEngine
//   Ds2.Runtime.Sim.Engine.Core  — SimIndexModule, GraphValidator
// ============================================================================

using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;

namespace Ds2.Tutorial.Steps;

static class Step05_Simulation
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 05: 시뮬레이션 + 토큰 ===\n");
        if (silent) return;

        var store = ctx.Store;

        // ── 1. Duration + Token 설정 ─────────────────────────
        var p1 = new SimulationWorkProperties { Duration = TimeSpan.FromMilliseconds(300) };
        store.Works[ctx.W1Id].SetSimulationProperties(p1);
        var p2 = new SimulationWorkProperties { Duration = TimeSpan.FromMilliseconds(500) };
        store.Works[ctx.W2Id].SetSimulationProperties(p2);
        var p3 = new SimulationWorkProperties { Duration = TimeSpan.FromMilliseconds(300) };
        store.Works[ctx.W3Id].SetSimulationProperties(p3);
        store.Works[ctx.W1Id].TokenRole = TokenRole.Source;

        Console.WriteLine("  [설정]");
        Console.WriteLine("    PickPart:  300ms, TokenRole=Source");
        Console.WriteLine("    WeldJoint: 500ms");
        Console.WriteLine("    PlacePart: 300ms");
        Console.WriteLine();

        // ── 2. SimIndex + 그래프 검증 ────────────────────────
        var index = SimIndexModule.build(store, 50);
        Console.WriteLine("  [SimIndex]");
        Console.WriteLine($"    Works={index.AllWorkGuids.Length}, Calls={index.AllCallGuids.Length}, Tick={index.TickMs}ms");
        Console.WriteLine($"    TokenSources={index.TokenSourceGuids.Length}, TokenSinks={index.TokenSinkGuids.Count}");

        // GraphValidator: 시뮬레이션 전 그래프 문제 사전 감지
        // 각 함수는 (Guid * string * string) list 반환 — (workGuid, systemName, workName)
        var unreset    = GraphValidator.findUnresetWorks(index);           // Reset 화살표 누락
        var deadlock   = GraphValidator.findDeadlockCandidates(index);    // 순환 의존 데드락
        var srcCand    = GraphValidator.findSourceCandidates(index);      // Source 미지정 후보
        var srcWithPred = GraphValidator.findSourcesWithPredecessors(index); // Source인데 선행자 있음
        var groupNoIgn = GraphValidator.findGroupWorksWithoutIgnore(index); // Group에 Ignore 누락
        var unreachable = GraphValidator.findTokenUnreachableWorks(index);  // 토큰 도달 불가

        Console.WriteLine("  [GraphValidator]");
        Console.WriteLine($"    unresetWorks:          {unreset.Length}");
        Console.WriteLine($"    deadlockCandidates:    {deadlock.Length}");
        Console.WriteLine($"    sourceCandidates:      {srcCand.Length}");
        Console.WriteLine($"    sourcesWithPreds:      {srcWithPred.Length}");
        Console.WriteLine($"    groupWithoutIgnore:    {groupNoIgn.Length}");
        Console.WriteLine($"    tokenUnreachable:      {unreachable.Length}");
        Console.WriteLine();

        // ── 3. 엔진 실행 + 이벤트 수집 ──────────────────────
        using var engine = new EventDrivenEngine(index);
        var sim = (ISimulationEngine)engine;

        var stateLog = new List<string>();
        sim.WorkStateChanged += (s, e) =>
            stateLog.Add($"    [{e.Clock:mm\\:ss\\.ff}] {e.WorkName}: {e.PreviousState} → {e.NewState}");

        var tokenLog = new List<string>();
        sim.TokenEvent += (s, e) =>
        {
            var target = e.TargetWorkName != null ? $" → {e.TargetWorkName.Value}" : "";
            tokenLog.Add($"    [{e.Clock:mm\\:ss\\.ff}] {e.Kind}: {e.WorkName}{target}");
        };

        Console.WriteLine("  [실행] 10배속, 2초간");
        sim.SpeedMultiplier = 10.0;
        sim.Start();
        Thread.Sleep(300);

        // 수동 토큰 투입
        var token = sim.NextToken();
        sim.SeedToken(ctx.W1Id, token);

        Thread.Sleep(2000);
        sim.Stop();
        Console.WriteLine();

        // ── 4. 상태 변화 로그 ────────────────────────────────
        Console.WriteLine($"  [상태 변화: {stateLog.Count}건]");
        foreach (var entry in stateLog.Take(12))
            Console.WriteLine(entry);
        if (stateLog.Count > 12)
            Console.WriteLine($"    ... (총 {stateLog.Count}건)");
        Console.WriteLine();

        // ── 5. 토큰 이벤트 로그 ──────────────────────────────
        Console.WriteLine($"  [토큰 이벤트: {tokenLog.Count}건]");
        foreach (var entry in tokenLog.Take(10))
            Console.WriteLine(entry);
        Console.WriteLine();

        // ── 6. 토큰 추적 ────────────────────────────────────
        var origin = sim.GetTokenOrigin(token);
        Console.WriteLine("  [토큰 추적]");
        if (origin != null)
            Console.WriteLine($"    Token #{token.Item} 발행: {origin.Value.Item1} #{origin.Value.Item2}");
        Console.WriteLine($"    완료된 토큰: {sim.State.CompletedTokens.Length}개");
        Console.WriteLine();

        // ── 7. Reset + TimeIgnore 비교 ──────────────────────
        sim.Reset();
        stateLog.Clear();
        tokenLog.Clear();
        sim.TimeIgnore = true;

        // TimeIgnore 모드에서도 토큰이 있어야 Work 가 시작됨
        var token2 = sim.NextToken();
        sim.SeedToken(ctx.W1Id, token2);

        sim.Start();
        Thread.Sleep(500);
        sim.Stop();
        Console.WriteLine($"  [TimeIgnore=true] 상태변화 {stateLog.Count}건 (Duration 무시, 즉시 전이)");
        Console.WriteLine();
        Console.WriteLine("  → 다음은 이 결과로 보고서를 만든다.");
    }
}
