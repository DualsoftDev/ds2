// ============================================================================
// Step 01: 엔티티 추가
// ----------------------------------------------------------------------------
// 빈 Store 에서 시작하여 프로젝트 구조를 하나씩 만든다.
//
//   Project: CarAssembly
//     └─ System: WeldingLine
//          └─ Flow: MainProcess
//               ├─ Work: PickPart
//               ├─ Work: WeldJoint
//               └─ Work: PlacePart
//
// 학습 내용:
//   - DsStore.empty(): 빈 Store 생성
//   - 엔티티 생성자: Project, DsSystem, Flow, Work (internal 생성자)
//   - ImportPlanModule.ofSeq: 등록 계획 수립
//   - ImportPlanModule.applyDirect: Store 에 일괄 적용
//   - F# module/type 이름 동일 → C# 에서 {Name}Module 접미사
// ============================================================================

using Ds2.Core;
using Ds2.Store;

namespace Ds2.Tutorial.Steps;

static class Step01_AddEntities
{
    public static void Run(TutorialContext ctx, bool silent = false)
    {
        if (!silent) Console.WriteLine("=== Step 01: 엔티티 추가 ===\n");

        var store = ctx.Store;

        // ── Guid 사전 할당 ───────────────────────────────────
        // 화살표 연결 등에서 Id 로 참조하므로 미리 할당
        ctx.ProjectId = Guid.NewGuid();
        ctx.SystemId  = Guid.NewGuid();
        ctx.FlowId    = Guid.NewGuid();
        ctx.W1Id      = Guid.NewGuid();
        ctx.W2Id      = Guid.NewGuid();
        ctx.W3Id      = Guid.NewGuid();

        // ── 엔티티 인스턴스 생성 ─────────────────────────────
        // 생성자는 internal — InternalsVisibleTo("Ds2.Tutorial") 필요
        var project = new Project("CarAssembly")  { Id = ctx.ProjectId };
        var system  = new DsSystem("WeldingLine") { Id = ctx.SystemId };
        var flow    = new Flow("MainProcess", ctx.SystemId) { Id = ctx.FlowId };
        var w1      = new Work("PickPart",  ctx.FlowId) { Id = ctx.W1Id };
        var w2      = new Work("WeldJoint", ctx.FlowId) { Id = ctx.W2Id };
        var w3      = new Work("PlacePart", ctx.FlowId) { Id = ctx.W3Id };

        // ── ImportPlan 으로 일괄 등록 ────────────────────────
        // ImportPlanOperation: F# DU (Discriminated Union)
        // NewAddSystem, NewAddFlow, NewAddWork, NewLinkSystemToProject
        var plan = ImportPlanModule.ofSeq([
            ImportPlanOperation.NewAddSystem(system),
            ImportPlanOperation.NewAddFlow(flow),
            ImportPlanOperation.NewAddWork(w1),
            ImportPlanOperation.NewAddWork(w2),
            ImportPlanOperation.NewAddWork(w3),
            ImportPlanOperation.NewLinkSystemToProject(ctx.ProjectId, ctx.SystemId, true),
        ]);

        store.Projects.Add(ctx.ProjectId, project);
        ImportPlanModule.applyDirect(store, plan);

        if (silent) return;

        Console.WriteLine("  엔티티 등록 완료:");
        Console.WriteLine($"    Project: {project.Name}");
        Console.WriteLine($"    System:  {system.Name}");
        Console.WriteLine($"    Flow:    {flow.Name}");
        Console.WriteLine($"    Works:   {w1.Name}, {w2.Name}, {w3.Name}");
        Console.WriteLine();
        Console.WriteLine($"  Store 상태: Projects={store.Projects.Count}, Systems={store.Systems.Count}, " +
                          $"Flows={store.Flows.Count}, Works={store.Works.Count}");
        Console.WriteLine();
        Console.WriteLine("  → 아직 화살표가 없다. Step 02 에서 연결한다.");
    }
}
