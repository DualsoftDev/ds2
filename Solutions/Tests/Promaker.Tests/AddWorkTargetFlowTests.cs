using System;
using System.Collections.Generic;
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
/// AddWork의 target flow 결정(ResolveTargetFlowId) 검증.
/// CanAddWork가 Work/Call selection을 차단하므로 여기서는 Flow / null / 다른 탭 컨텍스트
/// 케이스만 검증한다. lastFlow fallback 시나리오(N2~N4) 포함.
/// </summary>
public sealed class AddWorkTargetFlowTests
{
    // ─── S1: Flow 직접 선택 ───────────────────────────────────────────
    [Fact]
    public void AddWork_uses_selected_flow_directly()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, _, flow2Id) = SetupTwoFlows();

            vm.SelectedNode = new EntityNode(flow2Id, EntityKind.Flow, "Flow2");

            vm.AddWorkCommand.Execute(null);

            Assert.Single(Queries.worksOf(flow2Id, store));
        });
    }

    // ─── N2: System 탭 캔버스 사각지대 — selection 비운 후에도 lastFlow에 머무름 ─────
    [Fact]
    public void System_tab_AddWork_then_clear_selection_then_AddWork_stays_in_same_flow()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, flow1Id, flow2Id) = SetupTwoFlows();

            // 1) Flow2 노드를 선택하고 AddWork
            vm.SelectedNode = new EntityNode(flow2Id, EntityKind.Flow, "Flow2");
            vm.AddWorkCommand.Execute(null);
            Assert.Single(Queries.worksOf(flow2Id, store));

            // 2) 캔버스 빈 영역 클릭을 시뮬레이트 — selection 비움
            vm.SelectedNode = null;

            // 3) 다시 AddWork → Flow1이 아니라 Flow2에 추가되어야 함
            vm.AddWorkCommand.Execute(null);

            Assert.Equal(2, Queries.worksOf(flow2Id, store).Count());
            Assert.Empty(Queries.worksOf(flow1Id, store));
        });
    }

    // ─── N3: 새 프로젝트 직후 lastFlow 없음 → 첫 Flow ──────────────────
    [Fact]
    public void AddWork_with_no_selection_and_no_last_flow_falls_back_to_first_flow()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, flow1Id, flow2Id) = SetupTwoFlows();

            // selection 없음, lastFlow도 없음 (AddWork 호출 이력 없음)
            vm.SelectedNode = null;

            vm.AddWorkCommand.Execute(null);

            Assert.Single(Queries.worksOf(flow1Id, store));
            Assert.Empty(Queries.worksOf(flow2Id, store));
        });
    }

    // ─── N4: lastFlow가 stale(없는 Guid)이면 무시하고 첫 Flow로 fallback ─
    [Fact]
    public void Stale_last_flow_is_ignored_and_falls_back_to_first_flow()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, flow1Id, flow2Id) = SetupTwoFlows();

            // 존재하지 않는 Flow ID로 lastFlow 강제 주입
            SetLastAddWorkTargetFlowId(vm, Guid.NewGuid());
            vm.SelectedNode = null;

            vm.AddWorkCommand.Execute(null);

            // stale은 거부, 첫 Flow로 떨어짐
            Assert.Single(Queries.worksOf(flow1Id, store));
            Assert.Empty(Queries.worksOf(flow2Id, store));
        });
    }

    // ─── S7: 다른 탭의 Flow 선택 시 IsFlowInActiveTab 가드 발동 ─────────
    [Fact]
    public void AddWork_rejects_owning_flow_outside_active_tab_and_uses_tab_root()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, flow1Id, flow2Id) = SetupTwoFlows();

            // Flow2 탭을 활성으로 전환
            vm.Canvas.OpenTabs.Add(new CanvasTab(flow2Id, TabKind.Flow, "Flow2"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs.Last();

            // 그러나 selection은 Flow1
            vm.SelectedNode = new EntityNode(flow1Id, EntityKind.Flow, "Flow1");

            vm.AddWorkCommand.Execute(null);

            // Flow1은 ActiveTab(Flow2 탭)에 속하지 않으므로 거부 → Flow2 탭 RootId 사용
            Assert.Single(Queries.worksOf(flow2Id, store));
            Assert.Empty(Queries.worksOf(flow1Id, store));
        });
    }

    // ─── 헬퍼: Flow2개 + System 탭 ─────────────────────────────────────
    private static (MainViewModel vm, DsStore store, Guid systemId, Guid flow1Id, Guid flow2Id) SetupTwoFlows()
    {
        var vm = new MainViewModel();
        SetDialogService(vm, new SilentDialogService());
        vm.NewProjectCommand.Execute(null);

        var store = GetStore(vm);
        var projectId = Queries.allProjects(store).Head.Id;
        var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
        var flow1Id = Queries.flowsOf(systemId, store).Head.Id;
        var flow2Id = store.AddFlow("Flow2", systemId);

        vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
        vm.Canvas.ActiveTab = vm.Canvas.OpenTabs.First(t => t.Kind == TabKind.System);

        return (vm, store, systemId, flow1Id, flow2Id);
    }

    private static void SetDialogService(MainViewModel vm, IDialogService dialogService)
    {
        typeof(MainViewModel)
            .GetField("_dialogService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, dialogService);
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private static void SetLastAddWorkTargetFlowId(MainViewModel vm, Guid flowId)
    {
        var field = typeof(MainViewModel).GetField("_lastAddWorkTargetFlowId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, (Guid?)flowId);
    }

    private sealed class SilentDialogService : IDialogService
    {
        public string? PromptName(string title, string defaultName) => defaultName;
        public bool Confirm(string message, string title) => true;
        public void ShowWarning(string message) { }
        public bool WarnSimulationEditBlocked(string message) => false;
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => null;
        public bool? ShowDialog(Window dialog) => false;
    }
}
