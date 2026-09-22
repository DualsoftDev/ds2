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
/// 갱신 범위(RefreshScope) 분리 검증.
///
/// <para>캔버스는 가상화가 없어 <c>CanvasNodes.Clear()</c> + 재추가가 노드마다 89 엘리먼트짜리
/// DataTemplate 을 새로 인플레이트한다. 그래서 큰 모델에서 "무슨 동작을 하면 멈췄다 동작"의 몫이
/// 가장 컸다. 캔버스 노드 집합이 그대로인 갱신(속성 변경)은 캔버스를 건드리지 않아야 한다.</para>
///
/// <para>여기서 객체 동일성(<c>Assert.Same</c>)을 보는 이유: 같은 내용으로 다시 만들면 값 비교는
/// 통과하지만 WPF 는 컨테이너를 통째로 새로 만든다. 멈춤의 원인은 값이 아니라 객체 교체다.</para>
/// </summary>
public sealed class RebuildScopeTests
{
    /// 속성 변경(WorkPropsChanged) — 트리는 다시 굽되 캔버스 노드 객체는 그대로여야 한다.
    [Fact]
    public void Work_property_change_keeps_canvas_node_objects()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workId) = SetupSystemTabWithWork();

            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();
            Assert.NotEmpty(nodesBefore);

            store.AddWorkCondition(workId, ConditionType.SkipAction);   // → WorkPropsChanged
            StaTestRunner.PumpPendingUi();

            Assert.Equal(nodesBefore.Length, vm.Canvas.CanvasNodes.Count);
            for (var i = 0; i < nodesBefore.Length; i++)
                Assert.Same(nodesBefore[i], vm.Canvas.CanvasNodes[i]);
        });
    }

    /// 구조 변경(WorkAdded = RefreshScope.All) — 캔버스도 다시 만들어져야 한다.
    /// 위 테스트가 "캔버스를 영영 안 고침"으로 통과하는 것을 막는 짝 테스트.
    [Fact]
    public void Structural_change_still_rebuilds_canvas()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workId) = SetupSystemTabWithWork();

            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();
            Assert.NotEmpty(nodesBefore);

            var flowId = Queries.getWork(workId, store).Value.ParentId;
            store.AddWork("Work2", flowId);                              // → WorkAdded (scope All)
            StaTestRunner.PumpPendingUi();

            Assert.Equal(nodesBefore.Length + 1, vm.Canvas.CanvasNodes.Count);
            Assert.DoesNotContain(vm.Canvas.CanvasNodes, n => ReferenceEquals(n, nodesBefore[0]));
        });
    }

    /// 트리 전용 요청과 캔버스 요청이 한 tick 에 겹치면 전면 재구축으로 승격되어야 한다
    /// (승격을 빼먹으면 캔버스가 stale 인 채 남는다).
    [Fact]
    public void Tree_only_and_canvas_requests_in_one_tick_promote_to_full_rebuild()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, workId) = SetupSystemTabWithWork();

            var nodesBefore = vm.Canvas.CanvasNodes.ToArray();
            var flowId = Queries.getWork(workId, store).Value.ParentId;

            // 같은 tick 에 속성 변경(트리 전용) + 구조 변경(캔버스 필요) 을 함께 낸다.
            store.AddWorkCondition(workId, ConditionType.SkipAction);
            store.AddWork("Work2", flowId);
            StaTestRunner.PumpPendingUi();

            Assert.Equal(nodesBefore.Length + 1, vm.Canvas.CanvasNodes.Count);
            Assert.DoesNotContain(vm.Canvas.CanvasNodes, n => ReferenceEquals(n, nodesBefore[0]));
        });
    }

    // ─── 헬퍼 ────────────────────────────────────────────────────────
    private static (MainViewModel vm, DsStore store, Guid workId) SetupSystemTabWithWork()
    {
        var vm = new MainViewModel();
        SetDialogService(vm, new SilentDialogService());
        vm.NewProjectCommand.Execute(null);

        var store = GetStore(vm);
        var projectId = Queries.allProjects(store).Head.Id;
        var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
        var flowId = Queries.flowsOf(systemId, store).Head.Id;
        var workId = store.AddWork("Work1", flowId);

        vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
        vm.Canvas.ActiveTab = vm.Canvas.OpenTabs.First(t => t.Kind == TabKind.System);
        StaTestRunner.PumpPendingUi();

        return (vm, store, workId);
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
