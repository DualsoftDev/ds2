using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 갱신 범위 분리(RefreshScope) + 캔버스 제자리 조정(reconcile) 검증.
///
/// <para>큰 모델에서 "무슨 동작을 하면 멈췄다 동작"의 원인은 편집 후 화면을 통째로 다시 만드는
/// 것이었다. 캔버스는 ItemsPanel=Canvas 라 가상화가 없어 노드당 89 엘리먼트를 새로 인플레이트한다.
/// 그래서 두 가지를 못 박는다 — ① 캔버스 내용이 그대로인 갱신은 캔버스를 아예 건드리지 않는다,
/// ② 건드려야 할 때도 살아남는 노드는 <b>같은 객체</b>로 유지된다.</para>
///
/// <para>객체 동일성(<c>Assert.Same</c>)을 보는 이유: 같은 값으로 다시 만들면 값 비교는 통과하지만
/// WPF 는 컨테이너를 새로 만든다. 멈춤의 원인은 값이 아니라 객체 교체다.</para>
/// </summary>
public sealed class RebuildScopeTests
{
    /// 속성 변경(WorkPropsChanged) — 캔버스 갱신 자체가 일어나지 않아야 한다.
    [Fact]
    public void Work_property_change_does_not_refresh_canvas()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workIds) = SetupSystemTabWithWorks(1);
            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();

            var refreshes = 0;
            vm.Canvas.RecalculateCanvasSizeRequested = () => refreshes++;

            store.AddWorkCondition(workIds[0], ConditionType.SkipAction);   // → WorkPropsChanged
            StaTestRunner.PumpPendingUi();

            Assert.Equal(0, refreshes);
            Assert.Equal(nodesBefore.Length, vm.Canvas.CanvasNodes.Count);
            for (var i = 0; i < nodesBefore.Length; i++)
                Assert.Same(nodesBefore[i], vm.Canvas.CanvasNodes[i]);
        });
    }

    /// 구조 변경(WorkAdded = RefreshScope.All) — 캔버스는 갱신되어야 한다.
    /// 위 테스트가 "캔버스를 영영 안 고침"으로 통과하는 것을 막는 짝 테스트.
    [Fact]
    public void Structural_change_refreshes_canvas_and_reuses_surviving_nodes()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workIds) = SetupSystemTabWithWorks(2);
            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();

            var refreshes = 0;
            vm.Canvas.RecalculateCanvasSizeRequested = () => refreshes++;

            var flowId = Queries.getWork(workIds[0], store).Value.ParentId;
            store.AddWork("WorkAdded", flowId);
            StaTestRunner.PumpPendingUi();

            Assert.True(refreshes > 0);
            Assert.Equal(nodesBefore.Length + 1, vm.Canvas.CanvasNodes.Count);
            // 살아남은 노드는 같은 객체 — 새로 만들면 WPF 가 컨테이너를 전부 다시 만든다.
            for (var i = 0; i < nodesBefore.Length; i++)
                Assert.Same(nodesBefore[i], vm.Canvas.CanvasNodes[i]);
        });
    }

    /// 삭제 — 없어진 노드만 빠지고 나머지는 같은 객체로, 순서도 그대로 남아야 한다.
    [Fact]
    public void Canvas_refresh_drops_only_removed_node()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workIds) = SetupSystemTabWithWorks(3);
            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();
            Assert.Equal(3, nodesBefore.Length);

            var removedId = nodesBefore[1].Id;
            store.RemoveEntities(new[] { Tuple.Create(EntityKind.Work, removedId) });
            StaTestRunner.PumpPendingUi();

            Assert.Equal(2, vm.Canvas.CanvasNodes.Count);
            Assert.Same(nodesBefore[0], vm.Canvas.CanvasNodes[0]);
            Assert.Same(nodesBefore[2], vm.Canvas.CanvasNodes[1]);
            Assert.DoesNotContain(vm.Canvas.CanvasNodes, n => n.Id == removedId);
        });
    }

    /// 트리 전용 요청과 캔버스 요청이 한 tick 에 겹치면 전면 재구축으로 승격되어야 한다
    /// (승격을 빼먹으면 캔버스가 stale 인 채 남는다).
    [Fact]
    public void Tree_only_and_canvas_requests_in_one_tick_promote_to_full_rebuild()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workIds) = SetupSystemTabWithWorks(1);
            var countBefore = vm.Canvas.CanvasNodes.Count;
            var flowId = Queries.getWork(workIds[0], store).Value.ParentId;

            var refreshes = 0;
            vm.Canvas.RecalculateCanvasSizeRequested = () => refreshes++;

            // 같은 tick 에 속성 변경(트리 전용) + 구조 변경(캔버스 필요) 을 함께 낸다.
            store.AddWorkCondition(workIds[0], ConditionType.SkipAction);
            store.AddWork("WorkAdded", flowId);
            StaTestRunner.PumpPendingUi();

            Assert.True(refreshes > 0);
            Assert.Equal(countBefore + 1, vm.Canvas.CanvasNodes.Count);
        });
    }

    // ─── 헬퍼 ────────────────────────────────────────────────────────
    private static (MainViewModel vm, DsStore store, Guid[] workIds) SetupSystemTabWithWorks(int workCount)
    {
        var vm = new MainViewModel();
        SetDialogService(vm, new SilentDialogService());
        vm.NewProjectCommand.Execute(null);

        var store = GetStore(vm);
        var projectId = Queries.allProjects(store).Head.Id;
        var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
        var flowId = Queries.flowsOf(systemId, store).Head.Id;
        var workIds = Enumerable.Range(1, workCount)
            .Select(i => store.AddWork($"Work{i}", flowId))
            .ToArray();

        vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
        vm.Canvas.ActiveTab = vm.Canvas.OpenTabs.First(t => t.Kind == TabKind.System);
        StaTestRunner.PumpPendingUi();

        Assert.Equal(workCount, vm.Canvas.CanvasNodes.Count);
        return (vm, store, workIds);
    }

    private static void SetDialogService(MainViewModel vm, IDialogService dialogService) =>
        typeof(MainViewModel)
            .GetField("_dialogService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, dialogService);

    private static DsStore GetStore(MainViewModel vm) =>
        (DsStore)typeof(MainViewModel)
            .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private sealed class SilentDialogService : IDialogService
    {
        public string? PromptName(string title, string defaultName) => defaultName;
        public bool Confirm(string message, string title) => true;
        public void ShowWarning(string message) { }
        public bool WarnSimulationEditBlocked(string message, bool isMonitoring) => false;
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => null;
        public bool? ShowDialog(Window dialog) => false;
        public Ds2.Editor.CrossFlowDeviceMode? PromptCrossFlowDeviceMode(CrossFlowDeviceModePromptContext context) => null;
    }
}
