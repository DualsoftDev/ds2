// ============================================================================
// Step 02: 화살표 연결
// ----------------------------------------------------------------------------
// Step 01 에서 만든 Work 들을 화살표로 연결한다.
//
//   PickPart ──Start──> WeldJoint ──Start──> PlacePart
//       ↑                                        │
//       └──────────── Reset ─────────────────────┘
//
// 학습 내용:
//   - ArrowBetweenWorks: Work 간 화살표 (parentId=systemId)
//   - ArrowType.Start: 선행 Work 완료 → 후속 Work 시작
//   - ArrowType.Reset: 마지막 Work 완료 → 첫 Work 리셋 (순환)
//   - ImportPlanOperation.NewAddArrowWork: 화살표 추가
//   - Queries.arrowWorksOf: 시스템 내 화살표 조회
// ============================================================================

using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Core.Store;

namespace Ds2.Tutorial.Steps;

static class Step02_AddArrows
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 02: 화살표 연결 ===\n");

        var store = ctx.Store;

        // ── 화살표 추가 ──────────────────────────────────────
        // PickPart ──Start──→ WeldJoint ──Start──→ PlacePart
        // ↑                                        │
        // └──────────── Reset ─────────────────────┘
        // ArrowBetweenWorks(systemId, sourceId, targetId, arrowType)
        // parentId = systemId (DsSystem 의 자식)
        var plan = ImportPlanModule.ofSeq([
            // Start: PickPart → WeldJoint → PlacePart
            ImportPlanOperation.NewAddArrowWork(
                new ArrowBetweenWorks(ctx.SystemId, ctx.W1Id, ctx.W2Id, ArrowType.Start)),
            ImportPlanOperation.NewAddArrowWork(
                new ArrowBetweenWorks(ctx.SystemId, ctx.W2Id, ctx.W3Id, ArrowType.Start)),
            // Reset: PlacePart → PickPart (순환 루프)
            ImportPlanOperation.NewAddArrowWork(
                new ArrowBetweenWorks(ctx.SystemId, ctx.W3Id, ctx.W1Id, ArrowType.Reset)),
        ]);
        ImportPlanModule.applyDirect(store, plan);

        if (silent) return;

        // ── 화살표 확인 ──────────────────────────────────────
        var arrows = Queries.arrowWorksOf(ctx.SystemId, store);
        Console.WriteLine($"  화살표 {arrows.Length}개 연결 완료:");
        foreach (var a in arrows)
        {
            // tryGetName 은 F# option → C# 에서 null 이면 None
            var src = Queries.tryGetName(store, EntityKind.Work, a.SourceId)?.Value ?? "?";
            var tgt = Queries.tryGetName(store, EntityKind.Work, a.TargetId)?.Value ?? "?";
            Console.WriteLine($"    {src} ──{a.ArrowType}──> {tgt}");
        }
        Console.WriteLine();
        Console.WriteLine($"  Store 상태: ArrowWorks={store.ArrowWorks.Count}");
        Console.WriteLine();
        Console.WriteLine("  → 프로젝트 구조가 완성되었다. Step 03 에서 쿼리로 탐색한다.");
    }
}
