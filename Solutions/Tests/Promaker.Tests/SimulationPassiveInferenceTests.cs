using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Ds2.Runtime.IO;
using Microsoft.FSharp.Core;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationPassiveInferenceTests
{
    [Fact]
    public void VirtualPlant_io_map_deduplicates_shared_addresses_across_multi_and_single_calls()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSharedSingleAndMultiCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);

            var outAddresses = engine.IOMap.TxWorkToOutAddresses.Single(kv => kv.Key == fixture.DeviceWorkId).Value.ToArray();
            var inAddresses = engine.IOMap.RxWorkToInAddresses.Single(kv => kv.Key == fixture.DeviceWorkId).Value.ToArray();

            Assert.Equal(new[] { fixture.OutAddress }, outAddresses);
            Assert.Equal(new[] { fixture.InAddress }, inAddresses);
        });
    }

    [Fact]
    public void Passive_force_work_finish_in_virtualplant_does_not_auto_transition_to_homing_or_ready()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var transitions = new List<Status4>();
            engine.WorkStateChanged += (_, args) =>
            {
                if (args.WorkGuid == fixture.ActiveWorkId)
                    transitions.Add(args.NewState);
            };

            engine.Start();

            engine.ForceWorkState(fixture.ActiveWorkId, Status4.Going);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            engine.ForceWorkState(fixture.ActiveWorkId, Status4.Finish);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));

            Thread.Sleep(100);

            Assert.Equal(Status4.Finish, GetWorkState(engine, fixture.ActiveWorkId));
            Assert.Equal(new[] { Status4.Going, Status4.Finish }, transitions);
            Assert.DoesNotContain(Status4.Homing, transitions);
            Assert.DoesNotContain(Status4.Ready, transitions);
        });
    }

    [Fact]
    public void Runtime_engine_try_force_work_state_if_ready_ignores_non_ready_work()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);

            engine.Start();

            engine.TryForceWorkStateIfReady(fixture.ActiveWorkId, Status4.Going);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            engine.TryForceWorkStateIfReady(fixture.ActiveWorkId, Status4.Finish);
            Assert.Equal(Status4.Going, GetWorkState(engine, fixture.ActiveWorkId));

            engine.ForceWorkState(fixture.ActiveWorkId, Status4.Finish);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));

            engine.TryForceWorkStateIfReady(fixture.ActiveWorkId, Status4.Going);
            Assert.Equal(Status4.Finish, GetWorkState(engine, fixture.ActiveWorkId));
        });
    }

    [Fact]
    public void Passive_inference_detects_cycle_from_repeating_window_after_noisy_prefix()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.OutAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_call_inference_allows_shared_multi_and_single_calls_to_light_up_together()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSharedSingleAndMultiCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.SingleCallId) == Status4.Going));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.MultiCallId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.SingleCallId) == Status4.Finish));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.MultiCallId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_call_inference_yields_shared_address_to_expected_group_turn_after_sync()
    {
        // 부분 공유 모델 — 멀티 Call{공유A, 전용B} 와 단독 Call{공유A}. 사이클 학습(Synced) 후엔
        // 기대 그룹이 "차례"를 알므로, 멀티 차례의 공유주소 rising 에 단독 Call 은 Going 을 양보하고
        // 자기 차례(기대 그룹 = 자기 집합)에만 Going 한다. (수정 전: 멀티 차례마다 단독 Call 에
        // 가짜 Going/Finish 1세트 — 간트 Call 행이 사이클당 두 번 도는 것처럼 보이던 오귀속.)
        var fx = BuildPartiallySharedCallsFixture();
        var index = SimIndexModule.build(fx.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring);

        var workStates = new Dictionary<Guid, Status4>();
        var callStates = new Dictionary<Guid, Status4>();
        Status4 GetW(Guid g) => workStates.TryGetValue(g, out var s) ? s : Status4.Ready;
        Status4 GetC(Guid g) => callStates.TryGetValue(g, out var s) ? s : Status4.Ready;

        List<PassiveInferenceAction> Observe(string addr, string val)
        {
            var actions = session.Observe(addr, val, GetW, GetC).ToList();
            foreach (var a in actions)
            {
                if (a.TargetKind == PassiveInferenceTarget.Work) workStates[a.TargetGuid] = a.State;
                else callStates[a.TargetGuid] = a.State;
            }
            return actions;
        }

        void RunCycle()
        {
            // 멀티 차례(공유A + 전용B 동시) → 단독 차례(공유A 단독)
            Observe(fx.SharedOut, "true"); Observe(fx.MultiOnlyOut, "true");
            Observe(fx.SharedIn, "true"); Observe(fx.MultiOnlyIn, "true");
            Observe(fx.SharedOut, "false"); Observe(fx.MultiOnlyOut, "false");
            Observe(fx.SharedIn, "false"); Observe(fx.MultiOnlyIn, "false");
            Observe(fx.SharedOut, "true");
            Observe(fx.SharedIn, "true");
            Observe(fx.SharedOut, "false");
            Observe(fx.SharedIn, "false");
        }

        // Monitoring 은 3사이클 일치 후 Synced — 그 전엔 게이트 비활성(기존 동작).
        RunCycle();
        RunCycle();
        RunCycle();

        // 4번째 사이클, 멀티 차례 — 공유주소 rising 에 단독 Call 은 양보(가짜 Going 없음).
        var atSharedRising = Observe(fx.SharedOut, "true");
        Assert.DoesNotContain(
            atSharedRising,
            a => a.TargetKind == PassiveInferenceTarget.Call
                 && a.TargetGuid == fx.SingleCallId
                 && a.State == Status4.Going);

        // 멀티는 자기 기대(2주소) 충족 시 정상 Going.
        Observe(fx.MultiOnlyOut, "true");
        Assert.Equal(Status4.Going, GetC(fx.MultiCallId));

        Observe(fx.SharedIn, "true"); Observe(fx.MultiOnlyIn, "true");
        Observe(fx.SharedOut, "false"); Observe(fx.MultiOnlyOut, "false");
        Observe(fx.SharedIn, "false"); Observe(fx.MultiOnlyIn, "false");

        // 단독 차례 — 기대 그룹 = 자기 집합이므로 정상 Going.
        var atSingleTurn = Observe(fx.SharedOut, "true");
        Assert.Contains(
            atSingleTurn,
            a => a.TargetKind == PassiveInferenceTarget.Call
                 && a.TargetGuid == fx.SingleCallId
                 && a.State == Status4.Going);
    }

    [Fact]
    public void Passive_call_inference_does_not_yield_for_exclusive_addresses_fired_simultaneously()
    {
        // 실 PLC — 독립 Call 두 개가 같은 스캔(같은 ms)에 동시 발사되면 사이클 학습이 멀티
        // 그룹("Out#a|Out#b")으로 박제한다. 주소 공유가 없으면 오귀속 자체가 불가능한데도
        // 진초집합 조건에 걸려 서로를 상위 묶음으로 오인, *둘 다* 영구 양보하던 회귀
        // (실기: 동시 발사 4쌍 = 8개 Call 의 전이가 Synced 직후부터 소실).
        // 독점 Out 주소 Call 은 차례 게이트 비활성 — Synced 후에도 정상 Going.
        var fx = BuildExclusiveSimultaneousCallsFixture();
        var index = SimIndexModule.build(fx.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring);

        var workStates = new Dictionary<Guid, Status4>();
        var callStates = new Dictionary<Guid, Status4>();
        Status4 GetW(Guid g) => workStates.TryGetValue(g, out var s) ? s : Status4.Ready;
        Status4 GetC(Guid g) => callStates.TryGetValue(g, out var s) ? s : Status4.Ready;

        List<PassiveInferenceAction> Observe(string addr, string val)
        {
            var actions = session.Observe(addr, val, GetW, GetC).ToList();
            foreach (var a in actions)
            {
                if (a.TargetKind == PassiveInferenceTarget.Work) workStates[a.TargetGuid] = a.State;
                else callStates[a.TargetGuid] = a.State;
            }
            return actions;
        }

        void RunCycle()
        {
            // 동시 발사(연속 관측 = 같은 키 → 한 그룹으로 학습) → 동시 In → 정리.
            Observe(fx.OutA, "true"); Observe(fx.OutB, "true");
            Observe(fx.InA, "true"); Observe(fx.InB, "true");
            Observe(fx.OutA, "false"); Observe(fx.OutB, "false");
            Observe(fx.InA, "false"); Observe(fx.InB, "false");
        }

        RunCycle();
        RunCycle();
        RunCycle();

        // 4번째 사이클(Synced 후) — 독점 주소라 양보 없이 둘 다 정상 Going.
        Observe(fx.OutA, "true");
        Observe(fx.OutB, "true");
        Assert.Equal(Status4.Going, GetC(fx.CallAId));
        Assert.Equal(Status4.Going, GetC(fx.CallBId));
    }

    private sealed record ExclusiveSimultaneousFixture(
        DsStore Store,
        Guid CallAId,
        Guid CallBId,
        string OutA,
        string InA,
        string OutB,
        string InB);

    private static ExclusiveSimultaneousFixture BuildExclusiveSimultaneousCallsFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkA = store.AddWork("ADV_A", passiveFlowId);
        var deviceWorkB = store.AddWork("ADV_B", passiveFlowId);

        var apiDefA = AddDeviceApiDef(store, passiveSystemId, "ADV_A", deviceWorkA);
        var apiDefB = AddDeviceApiDef(store, passiveSystemId, "ADV_B", deviceWorkB);

        var callAId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "A", new[] { apiDefA });
        var callBId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "B", new[] { apiDefB });

        const string outA = "%Q3001";
        const string inA = "%I3001";
        const string outB = "%Q3002";
        const string inB = "%I3002";

        SetIoTags(store, callAId, store.Calls[callAId].ApiCalls.Single().Id, outA, inA);
        SetIoTags(store, callBId, store.Calls[callBId].ApiCalls.Single().Id, outB, inB);

        return new ExclusiveSimultaneousFixture(store, callAId, callBId, outA, inA, outB, inB);
    }

    private sealed record PartiallySharedFixture(
        DsStore Store,
        Guid SingleCallId,
        Guid MultiCallId,
        string SharedOut,
        string SharedIn,
        string MultiOnlyOut,
        string MultiOnlyIn);

    private static PartiallySharedFixture BuildPartiallySharedCallsFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkA = store.AddWork("ADV_A", passiveFlowId);
        var deviceWorkB = store.AddWork("ADV_B", passiveFlowId);

        var apiDefA = AddDeviceApiDef(store, passiveSystemId, "ADV_A", deviceWorkA);
        var apiDefB = AddDeviceApiDef(store, passiveSystemId, "ADV_B", deviceWorkB);

        var multiCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "MULTI", new[] { apiDefA, apiDefB });
        var singleCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "SINGLE", new[] { apiDefA });

        const string sharedOut = "%Q2001";
        const string sharedIn = "%I2001";
        const string multiOnlyOut = "%Q2002";
        const string multiOnlyIn = "%I2002";

        var singleApiCallId = store.Calls[singleCallId].ApiCalls.Single().Id;
        SetIoTags(store, singleCallId, singleApiCallId, sharedOut, sharedIn);

        foreach (var apiCall in store.Calls[multiCallId].ApiCalls)
        {
            var isFirst = apiCall.ApiDefId is { } defId
                          && FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId)
                          && apiCall.ApiDefId.Value == apiDefA;
            if (isFirst)
                SetIoTags(store, multiCallId, apiCall.Id, sharedOut, sharedIn);
            else
                SetIoTags(store, multiCallId, apiCall.Id, multiOnlyOut, multiOnlyIn);
        }

        return new PartiallySharedFixture(store, singleCallId, multiCallId, sharedOut, sharedIn, multiOnlyOut, multiOnlyIn);
    }

    [Fact]
    public void Passive_call_inference_does_not_reenter_going_from_input_cleanup_without_a_new_out_on()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);
            var transitions = new List<Status4>();
            engine.CallStateChanged += (_, args) =>
            {
                if (args.CallGuid == fixture.CallId)
                    transitions.Add(args.NewState);
            };

            engine.Start();

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Finish));

            ObservePassive(state, fixture.InAddress, "false");
            StaTestRunner.PumpPendingUi();

            Assert.Equal(Status4.Finish, GetCallState(engine, fixture.CallId));
            Assert.Equal(new[] { Status4.Going, Status4.Finish }, transitions);

            ObservePassive(state, fixture.OutAddress, "false");
            StaTestRunner.PumpPendingUi();
            Assert.Equal(Status4.Finish, GetCallState(engine, fixture.CallId));

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Going));
            Assert.Equal(new[] { Status4.Going, Status4.Finish, Status4.Ready, Status4.Going }, transitions);
        });
    }

    [Fact]
    public void Passive_call_inference_requires_all_apicall_outputs_and_inputs_for_call_state()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildMultiApiCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            // 전체 ApiCall IO 기준: Call 의 모든 ApiCall Out 이 high 여야 Going (첫 매칭만으론 부족).
            ObservePassive(state, fixture.FirstOutAddress, "true");
            ObservePassive(state, fixture.SecondOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Going));

            // 모든 ApiCall In 이 high 여야 Finish.
            ObservePassive(state, fixture.FirstInAddress, "true");
            ObservePassive(state, fixture.SecondInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_call_inference_uses_valuespec_matches_for_output_and_input()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildValueSpecCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.OutAddress, "5");
            StaTestRunner.PumpPendingUi();
            Assert.Equal(Status4.Ready, GetCallState(engine, fixture.CallId));

            ObservePassive(state, fixture.OutAddress, "7");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "8");
            StaTestRunner.PumpPendingUi();
            Assert.Equal(Status4.Going, GetCallState(engine, fixture.CallId));

            ObservePassive(state, fixture.InAddress, "9");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_inference_keeps_finish_during_cleanup_when_learning_window_starts_with_in_off()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));

            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");
            StaTestRunner.PumpPendingUi();

            Assert.Equal(Status4.Finish, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));
        });
    }

    [Fact]
    public void Passive_inference_ignores_duplicate_tokens_within_the_same_learning_group()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassiveRawDirection(state, engine, fixture.OutAddress, "true", isOut: true);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "true", isOut: false);
            ObservePassiveRawDirection(state, engine, fixture.OutAddress, "false", isOut: true);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "false", isOut: false);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "false", isOut: false);

            ObservePassiveRawDirection(state, engine, fixture.OutAddress, "true", isOut: true);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "true", isOut: false);
            ObservePassiveRawDirection(state, engine, fixture.OutAddress, "false", isOut: true);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "false", isOut: false);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "false", isOut: false);
            ObservePassiveRawDirection(state, engine, fixture.InAddress, "false", isOut: false);

            ObservePassiveRawDirection(state, engine, fixture.OutAddress, "true", isOut: true);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            ObservePassiveRawDirection(state, engine, fixture.InAddress, "true", isOut: false);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_inference_normalizes_cleanup_interleaving_between_positive_work_phases()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildBatchedCleanupInterleaveFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.FirstOutAddress, "true");
            ObservePassive(state, fixture.FirstOutAddress, "false");
            ObservePassive(state, fixture.FirstInAddress, "false");
            ObservePassive(state, fixture.SecondOutAddress, "true");
            ObservePassive(state, fixture.SecondOutAddress, "false");
            ObservePassive(state, fixture.SecondInAddress, "false");
            ObservePassive(state, fixture.FirstInAddress, "true");
            ObservePassive(state, fixture.SecondInAddress, "true");
            ObservePassive(state, fixture.FirstInAddress, "false");
            ObservePassive(state, fixture.SecondInAddress, "false");

            ObservePassive(state, fixture.FirstOutAddress, "true");
            ObservePassive(state, fixture.SecondOutAddress, "true");
            ObservePassive(state, fixture.FirstInAddress, "true");
            ObservePassive(state, fixture.SecondInAddress, "true");
            ObservePassive(state, fixture.FirstOutAddress, "false");
            ObservePassive(state, fixture.SecondOutAddress, "false");
            ObservePassive(state, fixture.FirstInAddress, "false");
            ObservePassive(state, fixture.SecondInAddress, "false");

            ObservePassive(state, fixture.FirstOutAddress, "true");
            ObservePassive(state, fixture.SecondOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            ObservePassive(state, fixture.FirstInAddress, "true");
            ObservePassive(state, fixture.SecondInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));
        });
    }

    [Fact]
    public void Passive_inference_uses_work_reset_connections_to_toggle_finished_work_and_calls_back_to_ready()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildResetPairFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);
            var targetWorkTransitions = new List<Status4>();
            var targetCallTransitions = new List<Status4>();
            engine.WorkStateChanged += (_, args) =>
            {
                if (args.WorkGuid == fixture.TargetWorkId)
                    targetWorkTransitions.Add(args.NewState);
            };
            engine.CallStateChanged += (_, args) =>
            {
                if (args.CallGuid == fixture.TargetCallId)
                    targetCallTransitions.Add(args.NewState);
            };

            engine.Start();

            ObservePassive(state, fixture.TargetOutAddress, "true");
            ObservePassive(state, fixture.TargetInAddress, "true");
            ObservePassive(state, fixture.TargetOutAddress, "false");
            ObservePassive(state, fixture.TargetInAddress, "false");

            ObservePassive(state, fixture.TargetOutAddress, "true");
            ObservePassive(state, fixture.TargetInAddress, "true");
            ObservePassive(state, fixture.TargetOutAddress, "false");
            ObservePassive(state, fixture.TargetInAddress, "false");

            ObservePassive(state, fixture.TargetOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Going));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Going));
            ObservePassive(state, fixture.TargetInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Finish));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Finish));
            ObservePassive(state, fixture.TargetOutAddress, "false");
            ObservePassive(state, fixture.TargetInAddress, "false");

            targetWorkTransitions.Clear();
            targetCallTransitions.Clear();

            ObservePassive(state, fixture.PredOutAddress, "true");
            ObservePassive(state, fixture.PredInAddress, "true");
            ObservePassive(state, fixture.PredOutAddress, "false");
            ObservePassive(state, fixture.PredInAddress, "false");

            ObservePassive(state, fixture.PredOutAddress, "true");
            ObservePassive(state, fixture.PredInAddress, "true");
            ObservePassive(state, fixture.PredOutAddress, "false");
            ObservePassive(state, fixture.PredInAddress, "false");

            ObservePassive(state, fixture.PredOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Ready));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Ready));

            ObservePassive(state, fixture.PredInAddress, "true");
            ObservePassive(state, fixture.PredOutAddress, "false");
            ObservePassive(state, fixture.PredInAddress, "false");

            ObservePassive(state, fixture.TargetOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Going));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Going));
            ObservePassive(state, fixture.TargetInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Finish));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Finish));
            ObservePassive(state, fixture.TargetOutAddress, "false");
            ObservePassive(state, fixture.TargetInAddress, "false");

            ObservePassive(state, fixture.PredOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.TargetWorkId) == Status4.Ready));
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.TargetCallId) == Status4.Ready));

            Assert.Equal(
                new[] { Status4.Ready, Status4.Going, Status4.Finish, Status4.Ready },
                targetWorkTransitions);
            Assert.Equal(
                new[] { Status4.Ready, Status4.Going, Status4.Finish, Status4.Ready },
                targetCallTransitions);
        });
    }

    [Fact]
    public void Monitoring_passive_inference_requires_a_third_matching_cycle_before_work_sync()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
            var state = CreatePassiveState(fixture.Store, engine, RuntimeMode.Monitoring);

            engine.Start();

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Going));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.CallId) == Status4.Finish));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            ObservePassive(state, fixture.OutAddress, "true");
            ObservePassive(state, fixture.InAddress, "true");
            ObservePassive(state, fixture.OutAddress, "false");
            ObservePassive(state, fixture.InAddress, "false");

            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            ObservePassive(state, fixture.InAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Finish));
        });
    }

    [Fact]
    public void Monitoring_passive_inference_records_provisional_gap_hint_without_replacing_strict_sync()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring);

        _ = session.DrainLogs();

        _ = session.Observe(
            fixture.OutAddress,
            "true",
            new Func<Guid, Status4>(_ => Status4.Ready),
            new Func<Guid, Status4>(_ => Status4.Ready));
        _ = session.DrainLogs();

        _ = session.Observe(
            fixture.InAddress,
            "true",
            new Func<Guid, Status4>(_ => Status4.Ready),
            new Func<Guid, Status4>(_ => Status4.Ready));
        _ = session.DrainLogs();

        _ = session.Observe(
            fixture.OutAddress,
            "false",
            new Func<Guid, Status4>(_ => Status4.Ready),
            new Func<Guid, Status4>(_ => Status4.Ready));
        _ = session.Observe(
            fixture.InAddress,
            "false",
            new Func<Guid, Status4>(_ => Status4.Ready),
            new Func<Guid, Status4>(_ => Status4.Ready));

        Thread.Sleep(25);

        var actions = session.Observe(
            fixture.OutAddress,
            "true",
            new Func<Guid, Status4>(_ => Status4.Ready),
            new Func<Guid, Status4>(_ => Status4.Ready));
        var logs = session.DrainLogs();

        Assert.DoesNotContain(actions, action => action.TargetKind == PassiveInferenceTarget.Work);
        Assert.DoesNotContain(logs, log => log.Message.Contains("cycle fixed", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Message.Contains("provisional gap head=", StringComparison.Ordinal));
    }

    [Fact]
    public void Monitoring_passive_inference_baselines_first_inactive_observed_plc_snapshot()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var getReady = new Func<Guid, Status4>(_ => Status4.Ready);

        var first = session.Observe(fixture.OutAddress, "false", getReady, getReady);
        var nextRising = session.Observe(fixture.OutAddress, "true", getReady, getReady);

        Assert.Empty(first);
        Assert.Contains(nextRising, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Going);
    }

    [Fact]
    public void Monitoring_passive_inference_treats_first_active_output_as_going()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var getReady = new Func<Guid, Status4>(_ => Status4.Ready);

        var first = session.Observe(fixture.OutAddress, "true", getReady, getReady);
        var duplicate = session.Observe(fixture.OutAddress, "true", getReady, getReady);

        Assert.Contains(first, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Going);
        Assert.Empty(duplicate);
    }

    [Fact]
    public void Monitoring_passive_inference_baseline_snapshot_active_does_not_emit_going()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var getReady = new Func<Guid, Status4>(_ => Status4.Ready);

        session.Baseline(fixture.OutAddress, "true");
        var duplicateSnapshotValue = session.Observe(fixture.OutAddress, "true", getReady, getReady);
        _ = session.Observe(fixture.OutAddress, "false", getReady, getReady);
        var nextRising = session.Observe(fixture.OutAddress, "true", getReady, getReady);

        Assert.Empty(duplicateSnapshotValue);
        Assert.Contains(nextRising, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Going);
    }

    [Fact]
    public void Monitoring_passive_inference_treats_first_active_input_after_output_as_finish()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var getReady = new Func<Guid, Status4>(_ => Status4.Ready);
        var getGoingCall = new Func<Guid, Status4>(guid => guid == fixture.CallId ? Status4.Going : Status4.Ready);

        var going = session.Observe(fixture.OutAddress, "true", getReady, getReady);
        var finish = session.Observe(fixture.InAddress, "true", getReady, getGoingCall);

        Assert.Contains(going, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Going);
        Assert.Contains(finish, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Finish);

        var reorderedSession = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var inputFirst = reorderedSession.Observe(fixture.InAddress, "true", getReady, getReady);
        var outputAfterInput = reorderedSession.Observe(fixture.OutAddress, "true", getReady, getReady);

        Assert.DoesNotContain(inputFirst, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Finish);
        Assert.Contains(outputAfterInput, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Going);
        Assert.Contains(outputAfterInput, action =>
            action.TargetKind == PassiveInferenceTarget.Call &&
            action.State == Status4.Finish);
    }

    [Fact]
    public void Monitoring_abnormal_is_not_ready_until_passive_cycle_learning_is_synced()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring, true);
        var getReady = new Func<Guid, Status4>(_ => Status4.Ready);

        void Observe(string address, string value) =>
            _ = session.Observe(address, value, getReady, getReady);

        Observe(fixture.OutAddress, "false");
        Observe(fixture.InAddress, "false");
        Assert.False(session.IsAbnormalReadyForAddress(fixture.OutAddress));
        Assert.False(session.IsAbnormalReadyForAddress(fixture.InAddress));

        for (var i = 0; i < 2; i++)
        {
            Observe(fixture.OutAddress, "true");
            Observe(fixture.InAddress, "true");
            Observe(fixture.OutAddress, "false");
            Observe(fixture.InAddress, "false");
        }

        Assert.False(session.IsAbnormalReadyForAddress(fixture.OutAddress));
        Assert.False(session.IsAbnormalReadyForAddress(fixture.InAddress));

        Observe(fixture.OutAddress, "true");
        Observe(fixture.InAddress, "true");
        Observe(fixture.OutAddress, "false");
        Observe(fixture.InAddress, "false");

        Assert.True(session.IsAbnormalReadyForAddress(fixture.OutAddress));
        Assert.True(session.IsAbnormalReadyForAddress(fixture.InAddress));
    }

    [Fact]
    public void Monitoring_passive_inference_uses_repeating_gap_profile_to_rotate_cycle_head()
    {
        var fixture = BuildBatchedCleanupInterleaveFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring);

        _ = session.DrainLogs();

        void Observe(string address, string value)
        {
            _ = session.Observe(
                address,
                value,
                new Func<Guid, Status4>(_ => Status4.Ready),
                new Func<Guid, Status4>(_ => Status4.Ready));
        }

        for (var i = 0; i < 3; i++)
        {
            Observe(fixture.FirstOutAddress, "true");
            Observe(fixture.FirstInAddress, "true");
            Observe(fixture.FirstOutAddress, "false");
            Observe(fixture.FirstInAddress, "false");

            Thread.Sleep(25);

            Observe(fixture.SecondOutAddress, "true");
            Observe(fixture.SecondInAddress, "true");
            Observe(fixture.SecondOutAddress, "false");
            Observe(fixture.SecondInAddress, "false");
        }

        var logs = session.DrainLogs();

        Assert.Contains(logs, log => log.Message.Contains("provisional gap head=", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Message.Contains("gap-scored head=", StringComparison.Ordinal) &&
                                     log.Message.Contains("finish=3", StringComparison.Ordinal) &&
                                     log.Message.Contains("outAfterFinish=0", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Message.Contains("cycle fixed", StringComparison.Ordinal) &&
                                     log.Message.Contains("Finish=3", StringComparison.Ordinal) &&
                                     log.Message.Contains("Seq[0..3]=Out#1 | In#1 | Out#0 | In#0", StringComparison.Ordinal));
    }

    [Fact]
    public void Monitoring_passive_inference_rotates_mid_cycle_learning_window_without_ready_to_finish_bootstrap()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildBatchedCleanupInterleaveFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
            var state = CreatePassiveState(fixture.Store, engine, RuntimeMode.Monitoring);

            engine.Start();

            for (var i = 0; i < 3; i++)
            {
                ObservePassiveRawDirection(state, engine, fixture.SecondInAddress, "true", isOut: false);
                ObservePassiveRawDirection(state, engine, fixture.FirstOutAddress, "true", isOut: true);
                ObservePassiveRawDirection(state, engine, fixture.FirstInAddress, "true", isOut: false);
                ObservePassiveRawDirection(state, engine, fixture.SecondOutAddress, "true", isOut: true);
            }

            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassiveRawDirection(state, engine, fixture.SecondInAddress, "true", isOut: false);
            StaTestRunner.PumpPendingUi();
            Assert.NotEqual(Status4.Finish, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassiveRawDirection(state, engine, fixture.FirstOutAddress, "true", isOut: true);
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) != Status4.Ready));
        });
    }

    [Fact]
    public void Control_runtime_hub_session_maps_in_tag_to_inject_without_rx_finish()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Control);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.Control);

        var effects = session.HandleHubTag(fixture.InAddress, "true", "virtualplant");

        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.InjectIoByAddress
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfGoing);
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.Log
            && effect.Severity == RuntimeHubLogSeverity.Finish
            && effect.Message.Contains(fixture.InAddress, StringComparison.Ordinal));
    }

    [Fact]
    public void Monitoring_runtime_hub_session_drives_device_going_without_plc_write()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.Monitoring);

        var effects = session.HandleHubTag(fixture.OutAddress, "true", "control");

        // Monitoring 도 Control/VP 처럼 device work 를 Going 으로 구동한다(=plan: duration 소모 + 상호리셋).
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady
            && effect.State == Status4.Going
            && effect.DelayMs == 0);
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.Log
            && effect.Severity == RuntimeHubLogSeverity.Info
            && effect.Message.Contains(fixture.OutAddress, StringComparison.Ordinal));
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveObserve
            && effect.Address == fixture.OutAddress
            && effect.Value == "true"
            && effect.DelayMs == 0);
        // read-only: 실 장비가 actual IO 의 진실원 — VP 와 달리 PLC 재기록(WriteTag)은 안 한다.
        Assert.DoesNotContain(effects, effect => effect.Kind == RuntimeHubEffectKind.WriteTag);
    }

    [Fact]
    public void VirtualPlant_runtime_hub_session_emits_device_effects_but_leaves_synthetic_in_signals_to_hub_replay()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        var effects = session.HandleHubTag(fixture.OutAddress, "true", "control");

        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady
            && effect.State == Status4.Going
            && effect.DelayMs == 0);
        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkState
            && effect.State == Status4.Finish);
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.WriteTag
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveObserve
            && effect.Address == fixture.OutAddress
            && effect.Value == "true"
            && effect.DelayMs == 0);
        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveObserve
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
    }

    [Fact]
    public void VirtualPlant_runtime_mode_session_replays_its_own_hub_source_for_synthetic_inputs()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeModeSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        Assert.False(session.ShouldIgnoreHubSource("virtualplant"));

        var effects = session.HandleHubTag(fixture.InAddress, "true", "virtualplant");
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.InjectIoByAddress
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveObserve
            && effect.Address == fixture.InAddress
            && effect.Value == "true"
            && effect.DelayMs == 0);
        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfGoing);
    }

    [Fact]
    public void VirtualPlant_hub_session_undefined_output_false_is_inactive()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        var effects = session.HandleHubTag(fixture.OutAddress, "false", "control");

        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady
            && effect.State == Status4.Going);
        Assert.DoesNotContain(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.WriteTag
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.WriteTag
            && effect.Address == fixture.InAddress
            && effect.Value == "false"
            && effect.DelayMs == 0);
    }

    // ── 비-Bool ValueSpec 회귀 가드 ────────────────────────────────────────
    // 이전 HubSession 은 `value = "true"` 와 WriteTag "true"/"false" 하드코딩이라
    // Int8/Float 등 비-Bool spec 인 ApiCall 에서 *VP echo 자체가 안 일어남* → Promaker
    // Control 측 무한 going. test_hs5.json (사용자 reproduction) 의 root cause.
    // 본 가드들은 HubSession 의 active 판정 + echo 값이 ApiCall.OutputSpec/InputSpec 기반으로
    // 동작하는지 직접 검증. Bool spec 시나리오는 기존 테스트 (위) 가 커버.

    [Fact]
    public void Control_hub_session_int_input_spec_active_injects_without_rx_finish()
    {
        var fixture = BuildValueSpecCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Control);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.Control);

        var activeEffects = session.HandleHubTag(fixture.InAddress, "9", "virtualplant");
        Assert.Contains(activeEffects, e =>
            e.Kind == RuntimeHubEffectKind.InjectIoByAddress && e.Value == "9");
        Assert.DoesNotContain(activeEffects, e =>
            e.Kind == RuntimeHubEffectKind.ForceWorkStateIfGoing);

        var inactiveEffects = session.HandleHubTag(fixture.InAddress, "5", "virtualplant");
        Assert.Contains(inactiveEffects, e =>
            e.Kind == RuntimeHubEffectKind.InjectIoByAddress && e.Value == "5");
        Assert.DoesNotContain(inactiveEffects, e =>
            e.Kind == RuntimeHubEffectKind.ForceWorkStateIfGoing);
    }

    [Fact]
    public void VirtualPlant_hub_session_int_output_spec_active_echoes_input_spec_value()
    {
        var fixture = BuildValueSpecCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        // OutputSpec = Int32(Single 7) — "7" 송출 받으면 active 판정.
        var effects = session.HandleHubTag(fixture.OutAddress, "7", "control");

        // VP echo 가 InputSpec.toDefaultString="9" 로 InAddress 에 발사 (이전엔 "true" 하드코딩).
        Assert.Contains(effects, e =>
            e.Kind == RuntimeHubEffectKind.WriteTag
            && e.Address == fixture.InAddress
            && e.Value == "9");
        Assert.Contains(effects, e =>
            e.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady && e.State == Status4.Going);
    }

    [Fact]
    public void VirtualPlant_hub_session_int_output_spec_inactive_resets_input()
    {
        var fixture = BuildValueSpecCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeHubSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        // OutputSpec=Int32(Single 7) — "5" 받으면 inactive (5 ≠ 7) → Going trigger 없음.
        var effects = session.HandleHubTag(fixture.OutAddress, "5", "control");

        Assert.DoesNotContain(effects, e =>
            e.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady && e.State == Status4.Going);
        // active echo "9" 안 들어감.
        Assert.DoesNotContain(effects, e =>
            e.Kind == RuntimeHubEffectKind.WriteTag
            && e.Address == fixture.InAddress
            && e.Value == "9");
        // 대신 InAddress 에 reset value "0" (Int32 의 type 별 reset) 으로 echo.
        Assert.Contains(effects, e =>
            e.Kind == RuntimeHubEffectKind.WriteTag
            && e.Address == fixture.InAddress
            && e.Value == "0");
    }

    [Fact]
    public void Monitoring_runtime_bootstrap_session_queries_snapshot_for_late_join()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new RuntimeBootstrapSession(index, engine.IOMap, RuntimeMode.Monitoring);

        Assert.True(session.RequiresPassiveInference);
        Assert.False(session.StartsWithHomingPhase);
        Assert.True(session.RequiresHubSnapshotSync);

        var queryAddresses = session.BuildHubSnapshotQueryAddresses();
        Assert.Contains(fixture.OutAddress, queryAddresses);
        Assert.Contains(fixture.InAddress, queryAddresses);
    }

    [Fact]
    public void VirtualPlant_runtime_mode_session_replays_current_out_snapshot_for_late_join()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
        var session = new RuntimeModeSession(index, engine.IOMap, RuntimeMode.VirtualPlant);

        var snapshot = new Dictionary<string, string>
        {
            [fixture.OutAddress] = "true",
            [fixture.InAddress] = "false"
        };

        var effects = session.ResolveHubSnapshotEffects(snapshot);

        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady
            && effect.State == Status4.Going);
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.WriteTag
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveObserve
            && effect.Address == fixture.OutAddress
            && effect.Value == "true");
    }

    [Fact]
    public void Control_runtime_bootstrap_session_uses_all_device_addresses_to_infer_state()
    {
        var fixture = BuildUnsyncedMultiCallWorkFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Control);
        var session = new RuntimeBootstrapSession(index, engine.IOMap, RuntimeMode.Control);

        var queryAddresses = session.BuildHubSnapshotQueryAddresses();
        Assert.Contains(fixture.FirstOutAddress, queryAddresses);
        Assert.Contains(fixture.SecondOutAddress, queryAddresses);
        Assert.Contains(fixture.FirstInAddress, queryAddresses);
        Assert.Contains(fixture.SecondInAddress, queryAddresses);

        var snapshot = new Dictionary<string, string>
        {
            [fixture.FirstOutAddress] = "false",
            [fixture.SecondOutAddress] = "true",
            [fixture.FirstInAddress] = "false",
            [fixture.SecondInAddress] = "false"
        };

        var effects = session.ResolveHubSnapshotEffects(snapshot);

        var workStateEffect = Assert.Single(effects.Where(effect => effect.Kind == RuntimeHubEffectKind.ForceWorkState));
        Assert.Equal(Status4.Going, workStateEffect.State);
        Assert.Contains(effects, effect =>
            effect.Kind == RuntimeHubEffectKind.Log
            && effect.Severity == RuntimeHubLogSeverity.System);
    }

    [Fact]
    public void Monitoring_runtime_mode_session_baselines_snapshot_without_replaying_state()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
        var session = new RuntimeModeSession(index, engine.IOMap, RuntimeMode.Monitoring);

        Assert.Equal("monitoring", session.HubSource);
        Assert.True(session.RequiresHubConnection);
        Assert.True(session.RequiresPassiveInference);
        Assert.False(session.StartsWithHomingPhase);
        Assert.True(session.RequiresHubSnapshotSync);
        Assert.False(session.UsesControlWriteBridge);
        Assert.True(session.ShouldIgnoreHubSource("monitoring"));
        Assert.False(session.ShouldIgnoreHubSource("control"));

        var snapshotEffects = session.ResolveHubSnapshotEffects(new Dictionary<string, string>
        {
            [fixture.OutAddress] = "true",
            [fixture.InAddress] = "false"
        });

        Assert.Contains(snapshotEffects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveBaseline
            && effect.Address == fixture.OutAddress
            && effect.Value == "true");
        Assert.Contains(snapshotEffects, effect =>
            effect.Kind == RuntimeHubEffectKind.PassiveBaseline
            && effect.Address == fixture.InAddress
            && effect.Value == "false");
        Assert.DoesNotContain(snapshotEffects, effect => effect.Kind == RuntimeHubEffectKind.PassiveObserve);
        Assert.DoesNotContain(snapshotEffects, effect => effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfReady);
        Assert.DoesNotContain(snapshotEffects, effect => effect.Kind == RuntimeHubEffectKind.WriteTag);
    }

    [Fact]
    public void Control_runtime_mode_session_delegates_hub_and_snapshot_logic()
    {
        var fixture = BuildSingleCallFixture();
        var index = SimIndexModule.build(fixture.Store, 10);
        using ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Control);
        var session = new RuntimeModeSession(index, engine.IOMap, RuntimeMode.Control);

        Assert.Equal("control", session.HubSource);
        Assert.True(session.RequiresHubConnection);
        Assert.True(session.UsesControlWriteBridge);
        Assert.True(session.RequiresHubSnapshotSync);

        var tagEffects = session.HandleHubTag(fixture.InAddress, "true", "virtualplant");
        Assert.Contains(tagEffects, effect =>
            effect.Kind == RuntimeHubEffectKind.InjectIoByAddress
            && effect.Address == fixture.InAddress
            && effect.Value == "true");
        Assert.DoesNotContain(tagEffects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkStateIfGoing);

        var snapshot = new Dictionary<string, string>
        {
            [fixture.OutAddress] = "true",
            [fixture.InAddress] = "false"
        };

        var snapshotEffects = session.ResolveHubSnapshotEffects(snapshot);
        Assert.Contains(snapshotEffects, effect =>
            effect.Kind == RuntimeHubEffectKind.ForceWorkState
            && effect.State == Status4.Going);
    }

    [Fact]
    public void Runtime_hub_effect_pipeline_keeps_zero_delay_effects_in_a_nonblocking_batch()
    {
        var effects = new List<RuntimeHubEffect>();
        RuntimeSessionEffects.addLog(effects, 0, RuntimeHubLogSeverity.Going, "log");
        RuntimeSessionEffects.addWriteTag(effects, 0, "%I1001", "false");
        RuntimeSessionEffects.addPassiveObserve(effects, 0, "%Q1001", "true");
        RuntimeSessionEffects.addForceWorkState(effects, 500, Guid.NewGuid(), Status4.Finish);
        RuntimeSessionEffects.addWriteTag(effects, 500, "%I1001", "true");
        RuntimeSessionEffects.addPassiveObserve(effects, 500, "%I1001", "true");

        var batches = RuntimeHubEffectPipeline.Build(effects);

        Assert.Collection(
            batches,
            batch =>
            {
                Assert.Equal(0, batch.DelayMs);
                Assert.False(batch.AwaitWrites);
                Assert.True(batch.RequiresExclusiveImmediateLane);
                Assert.Equal(
                    new[]
                    {
                        RuntimeHubEffectKind.Log,
                        RuntimeHubEffectKind.WriteTag,
                        RuntimeHubEffectKind.PassiveObserve
                    },
                    batch.Effects.Select(static effect => effect.Kind));
            },
            batch =>
            {
                Assert.Equal(500, batch.DelayMs);
                Assert.True(batch.AwaitWrites);
                Assert.False(batch.RequiresExclusiveImmediateLane);
                Assert.Equal(
                    new[]
                    {
                        RuntimeHubEffectKind.ForceWorkState,
                        RuntimeHubEffectKind.WriteTag,
                        RuntimeHubEffectKind.PassiveObserve
                    },
                    batch.Effects.Select(static effect => effect.Kind));
            });
    }

    [Fact]
    public void Runtime_hub_effect_pipeline_groups_delayed_effects_by_due_time()
    {
        var finishWorkGuid = Guid.NewGuid();
        var effects = new List<RuntimeHubEffect>();
        RuntimeSessionEffects.addWriteTag(effects, 0, "%I1001", "false");
        RuntimeSessionEffects.addPassiveObserve(effects, 0, "%Q1001", "true");
        RuntimeSessionEffects.addForceWorkState(effects, 500, finishWorkGuid, Status4.Finish);
        RuntimeSessionEffects.addWriteTag(effects, 500, "%I1001", "true");
        RuntimeSessionEffects.addLog(effects, 750, RuntimeHubLogSeverity.System, "later");
        RuntimeSessionEffects.addPassiveObserve(effects, 750, "%I1001", "false");

        var batches = RuntimeHubEffectPipeline.Build(effects);

        Assert.Equal(3, batches.Count);
        Assert.Equal(0, batches[0].DelayMs);
        Assert.Equal(500, batches[1].DelayMs);
        Assert.Equal(750, batches[2].DelayMs);
        Assert.False(batches[0].AwaitWrites);
        Assert.True(batches[1].AwaitWrites);
        Assert.True(batches[2].AwaitWrites);
        Assert.True(batches[0].RequiresExclusiveImmediateLane);
        Assert.False(batches[1].RequiresExclusiveImmediateLane);
        Assert.False(batches[2].RequiresExclusiveImmediateLane);
        Assert.Equal(
            new[]
            {
                RuntimeHubEffectKind.Log,
                RuntimeHubEffectKind.PassiveObserve
            },
            batches[2].Effects.Select(static effect => effect.Kind));
    }

    [Fact]
    public void Runtime_hub_effect_pipeline_keeps_delayed_virtualplant_batches_off_the_immediate_lane()
    {
        var effects = new List<RuntimeHubEffect>();
        RuntimeSessionEffects.addLog(effects, 0, RuntimeHubLogSeverity.Homing, "reset");
        RuntimeSessionEffects.addWriteTag(effects, 0, "%I2101", "false");
        RuntimeSessionEffects.addPassiveObserve(effects, 0, "%I2101", "false");
        RuntimeSessionEffects.addPassiveObserve(effects, 0, "%Q1001", "true");
        RuntimeSessionEffects.addForceWorkState(effects, 500, Guid.NewGuid(), Status4.Finish);
        RuntimeSessionEffects.addWriteTag(effects, 500, "%I1101", "true");
        RuntimeSessionEffects.addPassiveObserve(effects, 500, "%I1101", "true");

        var batches = RuntimeHubEffectPipeline.Build(effects);

        Assert.Collection(
            batches,
            batch =>
            {
                Assert.Equal(0, batch.DelayMs);
                Assert.True(batch.RequiresExclusiveImmediateLane);
                Assert.False(batch.AwaitWrites);
            },
            batch =>
            {
                Assert.Equal(500, batch.DelayMs);
                Assert.False(batch.RequiresExclusiveImmediateLane);
                Assert.True(batch.AwaitWrites);
            });
    }

    private static SharedCallFixture BuildSharedSingleAndMultiCallFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);

        var apiDef1Id = AddDeviceApiDef(store, passiveSystemId, "ADV_A", deviceWorkId);
        var apiDef2Id = AddDeviceApiDef(store, passiveSystemId, "ADV_B", deviceWorkId);

        var singleCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_SINGLE", new[] { apiDef1Id });
        var multiCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_MULTI", new[] { apiDef1Id, apiDef2Id });

        const string outAddress = "%Q2001";
        const string inAddress = "%I2001";

        var singleApiCallId = store.Calls[singleCallId].ApiCalls.Single().Id;
        SetIoTags(store, singleCallId, singleApiCallId, outAddress, inAddress);

        foreach (var apiCall in store.Calls[multiCallId].ApiCalls)
            SetIoTags(store, multiCallId, apiCall.Id, outAddress, inAddress);

        return new SharedCallFixture(store, deviceWorkId, singleCallId, multiCallId, outAddress, inAddress);
    }

    private static SingleCallFixture BuildSingleCallFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);
        var apiDefId = AddDeviceApiDef(store, passiveSystemId, "ADV", deviceWorkId);

        var callId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV", new[] { apiDefId });
        var apiCallId = store.Calls[callId].ApiCalls.Single().Id;
        const string outAddress = "%Q3001";
        const string inAddress = "%I3001";
        SetIoTags(store, callId, apiCallId, outAddress, inAddress);

        return new SingleCallFixture(store, activeWorkId, callId, outAddress, inAddress);
    }

    private static MultiApiCallFixture BuildMultiApiCallFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);
        var apiDefOneId = AddDeviceApiDef(store, passiveSystemId, "ADV_A", deviceWorkId);
        var apiDefTwoId = AddDeviceApiDef(store, passiveSystemId, "ADV_B", deviceWorkId);

        var callId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_MULTI", new[] { apiDefOneId, apiDefTwoId });
        var apiCalls = store.Calls[callId].ApiCalls.ToArray();

        const string firstOutAddress = "%Q3501";
        const string firstInAddress = "%I3501";
        const string secondOutAddress = "%Q3502";
        const string secondInAddress = "%I3502";
        SetIoTags(store, callId, apiCalls[0].Id, firstOutAddress, firstInAddress);
        SetIoTags(store, callId, apiCalls[1].Id, secondOutAddress, secondInAddress);

        return new MultiApiCallFixture(
            store,
            callId,
            firstOutAddress,
            firstInAddress,
            secondOutAddress,
            secondInAddress);
    }

    private static ValueSpecCallFixture BuildValueSpecCallFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);
        var apiDefId = AddDeviceApiDef(store, passiveSystemId, "ADV_SPEC", deviceWorkId);

        var callId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_SPEC", new[] { apiDefId });
        var apiCall = store.Calls[callId].ApiCalls.Single();

        const string outAddress = "%Q3601";
        const string inAddress = "%I3601";
        SetIoTags(store, callId, apiCall.Id, outAddress, inAddress);
        apiCall.OutputSpec = ValueSpecModule.singleInt32(7);
        apiCall.InputSpec = ValueSpecModule.singleInt32(9);

        return new ValueSpecCallFixture(store, callId, outAddress, inAddress);
    }

    private static UnsyncedMultiCallWorkFixture BuildUnsyncedMultiCallWorkFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);
        var apiDefRetId = AddDeviceApiDef(store, passiveSystemId, "RET", deviceWorkId);
        var apiDefAdvId = AddDeviceApiDef(store, passiveSystemId, "ADV", deviceWorkId);

        var firstCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "RET", new[] { apiDefRetId });
        var secondCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV", new[] { apiDefAdvId });

        var firstApiCallId = store.Calls[firstCallId].ApiCalls.Single().Id;
        var secondApiCallId = store.Calls[secondCallId].ApiCalls.Single().Id;

        const string firstOutAddress = "%Q4001";
        const string firstInAddress = "%I4001";
        const string secondOutAddress = "%Q4002";
        const string secondInAddress = "%I4002";
        SetIoTags(store, firstCallId, firstApiCallId, firstOutAddress, firstInAddress);
        SetIoTags(store, secondCallId, secondApiCallId, secondOutAddress, secondInAddress);

        return new UnsyncedMultiCallWorkFixture(
            store,
            activeWorkId,
            firstCallId,
            secondCallId,
            firstOutAddress,
            firstInAddress,
            secondOutAddress,
            secondInAddress);
    }

    private static TwoCallWorkFixture BuildBatchedCleanupInterleaveFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var deviceWorkId = store.AddWork("ADV", passiveFlowId);
        var apiDefOneId = AddDeviceApiDef(store, passiveSystemId, "ADV_1", deviceWorkId);
        var apiDefTwoId = AddDeviceApiDef(store, passiveSystemId, "ADV_2", deviceWorkId);

        var firstCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_1", new[] { apiDefOneId });
        var secondCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Dev", "ADV_2", new[] { apiDefTwoId });

        var firstApiCallId = store.Calls[firstCallId].ApiCalls.Single().Id;
        var secondApiCallId = store.Calls[secondCallId].ApiCalls.Single().Id;

        const string firstOutAddress = "%Q5001";
        const string firstInAddress = "%I5001";
        const string secondOutAddress = "%Q5002";
        const string secondInAddress = "%I5002";
        SetIoTags(store, firstCallId, firstApiCallId, firstOutAddress, firstInAddress);
        SetIoTags(store, secondCallId, secondApiCallId, secondOutAddress, secondInAddress);

        return new TwoCallWorkFixture(
            store,
            activeWorkId,
            firstOutAddress,
            firstInAddress,
            secondOutAddress,
            secondInAddress);
    }

    private static ResetPairFixture BuildResetPairFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var predWorkId = store.AddWork("Pred", activeFlowId);
        var targetWorkId = store.AddWork("Target", activeFlowId);
        store.ConnectSelectionInOrder([predWorkId, targetWorkId], ArrowType.Reset);

        var passiveSystemId = store.AddSystem("Passive", projectId, false);
        var passiveFlowId = store.AddFlow("DeviceFlow", passiveSystemId);
        var predDeviceWorkId = store.AddWork("PRED_DEV", passiveFlowId);
        var targetDeviceWorkId = store.AddWork("TARGET_DEV", passiveFlowId);
        var predApiDefId = AddDeviceApiDef(store, passiveSystemId, "PRED_API", predDeviceWorkId);
        var targetApiDefId = AddDeviceApiDef(store, passiveSystemId, "TARGET_API", targetDeviceWorkId);

        var predCallId = store.AddCallWithLinkedApiDefs(predWorkId, "Dev", "PRED", new[] { predApiDefId });
        var targetCallId = store.AddCallWithLinkedApiDefs(targetWorkId, "Dev", "TARGET", new[] { targetApiDefId });

        var predApiCallId = store.Calls[predCallId].ApiCalls.Single().Id;
        var targetApiCallId = store.Calls[targetCallId].ApiCalls.Single().Id;

        const string predOutAddress = "%Q6001";
        const string predInAddress = "%I6001";
        const string targetOutAddress = "%Q6002";
        const string targetInAddress = "%I6002";
        SetIoTags(store, predCallId, predApiCallId, predOutAddress, predInAddress);
        SetIoTags(store, targetCallId, targetApiCallId, targetOutAddress, targetInAddress);

        return new ResetPairFixture(
            store,
            predWorkId,
            targetWorkId,
            predCallId,
            targetCallId,
            predOutAddress,
            predInAddress,
            targetOutAddress,
            targetInAddress);
    }

    private static Guid AddDeviceApiDef(DsStore store, Guid systemId, string name, Guid deviceWorkId)
    {
        var apiDefId = store.AddApiDefWithProperties(name, systemId);
        store.UpdateApiDef(
            apiDefId,
            name,
            ActionType.NewNormal(FSharpOption<int>.None),
            SensingType.NewNormal(FSharpOption<int>.None),
            FSharpOption<Guid>.Some(deviceWorkId),
            FSharpOption<Guid>.Some(deviceWorkId),
            "");
        return apiDefId;
    }

    private static void SetIoTags(DsStore store, Guid callId, Guid apiCallId, string outAddress, string inAddress)
    {
        store.UpdateApiCallIoTagsBatch(new[]
        {
            (callId, apiCallId,
             new IOTag("Out", outAddress, ""),
             new IOTag("In",  inAddress,  ""))
        });
    }

    private static SimulationPanelState CreatePassiveState(
        DsStore store,
        ISimulationEngine engine,
        RuntimeMode runtimeMode = RuntimeMode.VirtualPlant)
    {
        var state = new SimulationPanelState(
            () => store,
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            () => Array.Empty<EntityNode>(),
            _ => { });

        state.SelectedRuntimeMode = runtimeMode;
        SetPrivateField(state, "_simEngine", engine);
        InvokeNonPublic(state, "PreparePassiveModeIoInference");
        return state;
    }

    private static void ObservePassive(SimulationPanelState state, string address, string value)
    {
        InvokeNonPublic(state, "ObserveAndInferPassiveState", address, value);
        StaTestRunner.PumpPendingUi();
    }

    private static void ObservePassiveRawDirection(
        SimulationPanelState state,
        ISimulationEngine engine,
        string address,
        string value,
        bool isOut)
    {
        var mappings = engine.IOMap.Mappings
            .Where(m => (isOut ? m.OutAddress : m.InAddress) == address)
            .ToArray();

        InvokeNonPublic(state, "ObservePassiveSignalDirection", address, value, isOut, mappings);
        StaTestRunner.PumpPendingUi();
    }

    private static Status4 GetWorkState(ISimulationEngine engine, Guid workGuid)
    {
        var state = engine.GetWorkState(workGuid);
        return state != null && FSharpOption<Status4>.get_IsSome(state) ? state.Value : Status4.Ready;
    }

    private static Status4 GetCallState(ISimulationEngine engine, Guid callGuid)
    {
        var state = engine.GetCallState(callGuid);
        return state != null && FSharpOption<Status4>.get_IsSome(state) ? state.Value : Status4.Ready;
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private static object? InvokeNonPublic(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return method.Invoke(instance, args);
    }

    private sealed record SharedCallFixture(
        DsStore Store,
        Guid DeviceWorkId,
        Guid SingleCallId,
        Guid MultiCallId,
        string OutAddress,
        string InAddress);

    private sealed record SingleCallFixture(
        DsStore Store,
        Guid ActiveWorkId,
        Guid CallId,
        string OutAddress,
        string InAddress);

    private sealed record MultiApiCallFixture(
        DsStore Store,
        Guid CallId,
        string FirstOutAddress,
        string FirstInAddress,
        string SecondOutAddress,
        string SecondInAddress);

    private sealed record ValueSpecCallFixture(
        DsStore Store,
        Guid CallId,
        string OutAddress,
        string InAddress);

    private sealed record UnsyncedMultiCallWorkFixture(
        DsStore Store,
        Guid ActiveWorkId,
        Guid FirstCallId,
        Guid SecondCallId,
        string FirstOutAddress,
        string FirstInAddress,
        string SecondOutAddress,
        string SecondInAddress);

    private sealed record TwoCallWorkFixture(
        DsStore Store,
        Guid ActiveWorkId,
        string FirstOutAddress,
        string FirstInAddress,
        string SecondOutAddress,
        string SecondInAddress);

    private sealed record ResetPairFixture(
        DsStore Store,
        Guid PredWorkId,
        Guid TargetWorkId,
        Guid PredCallId,
        Guid TargetCallId,
        string PredOutAddress,
        string PredInAddress,
        string TargetOutAddress,
        string TargetInAddress);
}
