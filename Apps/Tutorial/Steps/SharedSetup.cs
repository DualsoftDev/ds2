// ============================================================================
// SharedSetup — 튜토리얼 공유 컨텍스트
// ----------------------------------------------------------------------------
// 전체 실행(0): 빈 Store 에서 시작 → 각 Step 이 순차적으로 쌓아감
// 개별 실행(N): 해당 Step 직전까지 자동 구성 후 실행
// ============================================================================

using Ds2.Core;
using Ds2.Store;

namespace Ds2.Tutorial.Steps;

/// <summary>
/// Step 간에 전달되는 공유 컨텍스트.
/// 전체 실행 시 하나의 인스턴스가 Step 1→8 까지 흘러간다.
/// </summary>
class TutorialContext
{
    public DsStore Store { get; set; } = DsStore.empty();

    // Id 는 Step 1 에서 할당
    public Guid ProjectId { get; set; }
    public Guid SystemId { get; set; }
    public Guid FlowId { get; set; }
    public Guid W1Id { get; set; }  // PickPart
    public Guid W2Id { get; set; }  // WeldJoint
    public Guid W3Id { get; set; }  // PlacePart

    /// <summary>빈 컨텍스트 생성 (Step 1 시작점)</summary>
    public static TutorialContext CreateEmpty() => new();

    /// <summary>
    /// 특정 Step 직전까지의 상태를 자동 구성.
    /// 개별 실행 시 사용 — Step N 을 독립 실행하려면 1~(N-1) 을 먼저 돌려야 함.
    /// </summary>
    public static TutorialContext BuildUpTo(int step)
    {
        var ctx = CreateEmpty();
        if (step > 1) Step01_AddEntities.Run(ctx, silent: true);
        if (step > 2) Step02_AddArrows.Run(ctx, silent: true);
        // Step 3+ 는 추가 준비 불필요 (쿼리/저장/시뮬레이션은 현재 Store 그대로 사용)
        return ctx;
    }
}
