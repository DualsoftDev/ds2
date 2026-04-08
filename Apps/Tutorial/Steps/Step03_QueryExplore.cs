// ============================================================================
// Step 03: DsQuery 탐색
// ----------------------------------------------------------------------------
// Step 01~02 에서 만든 프로젝트 구조를 DsQuery 로 탐색한다.
//
// 학습 내용:
//   - Queries.allProjects / activeSystemsOf / flowsOf / worksOf
//   - Queries.arrowWorksOf: 화살표 조회
//   - Queries.tryGetName: Guid → 이름 조회
//   - Queries.getTokenSpecs: TokenSpec 목록
//   - StoreHierarchyQueries: 역방향 계층 탐색
//     · parentIdOf: 직계 부모
//     · tryFindSystemIdForEntity: Work → System
//     · tryFindProjectIdForEntity: Work → Project
//
// F# curried 함수 → C# 호출:
//   Queries.activeSystemsOf(projectId, store)  ← 두 인자 한번에
// ============================================================================

using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Core.Store;

namespace Ds2.Tutorial.Steps;

static class Step03_QueryExplore
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 03: DsQuery 탐색 ===\n");
        if (silent) return;

        var store = ctx.Store;

        // ── 계층 탐색 (위→아래) ──────────────────────────────
        Console.WriteLine("  [위→아래 탐색]");

        var projects = Queries.allProjects(store);
        Console.WriteLine($"    Project: {projects.Head.Name}");

        var systems = Queries.activeSystemsOf(ctx.ProjectId, store);
        Console.WriteLine($"    └─ System: {systems.Head.Name}");

        var flows = Queries.flowsOf(ctx.SystemId, store);
        Console.WriteLine($"       └─ Flow: {flows.Head.Name}");

        var works = Queries.worksOf(ctx.FlowId, store);
        foreach (var w in works)
            Console.WriteLine($"          └─ Work: {w.Name}");
        Console.WriteLine();

        // ── 화살표 확인 ──────────────────────────────────────
        Console.WriteLine("  [화살표]");
        var arrows = Queries.arrowWorksOf(ctx.SystemId, store);
        foreach (var a in arrows)
        {
            var src = Queries.tryGetName(store, EntityKind.Work, a.SourceId)?.Value ?? "?";
            var tgt = Queries.tryGetName(store, EntityKind.Work, a.TargetId)?.Value ?? "?";
            Console.WriteLine($"    {src} ──{a.ArrowType}──> {tgt}");
        }
        Console.WriteLine();

        // ── 역방향 계층 탐색 (아래→위) ──────────────────────
        Console.WriteLine("  [아래→위 탐색: PickPart 기준]");
        // F# option<Guid> → C# FSharpOption<Guid>: null=None, ?.Value=Some
        var parentFlow = StoreHierarchyQueries.parentIdOf(store, EntityKind.Work, ctx.W1Id);
        var parentSys  = StoreHierarchyQueries.tryFindSystemIdForEntity(store, EntityKind.Work, ctx.W1Id);
        var parentProj = StoreHierarchyQueries.tryFindProjectIdForEntity(store, EntityKind.Work, ctx.W1Id);
        Console.WriteLine($"    Work → Flow:    {(parentFlow != null ? parentFlow.Value : "N/A")}");
        Console.WriteLine($"    Work → System:  {(parentSys != null ? parentSys.Value : "N/A")}");
        Console.WriteLine($"    Work → Project: {(parentProj != null ? parentProj.Value : "N/A")}");
        Console.WriteLine();

        // ── 기타 쿼리 ────────────────────────────────────────
        Console.WriteLine("  [기타]");
        var name = Queries.tryGetName(store, EntityKind.Work, ctx.W1Id)?.Value ?? "N/A";
        Console.WriteLine($"    tryGetName(W1): {name}");
        var specs = Queries.getTokenSpecs(store);
        Console.WriteLine($"    getTokenSpecs: {specs.Length}개");
        Console.WriteLine();
        Console.WriteLine("  → 구조 확인 완료. Step 04 에서 파일로 저장한다.");
    }
}
