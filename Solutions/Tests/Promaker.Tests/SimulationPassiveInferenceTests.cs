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
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);

            var outAddresses = engine.IOMap.TxWorkToOutAddresses.Single(kv => kv.Key == fixture.DeviceWorkId).Value.ToArray();
            var inAddresses = engine.IOMap.RxWorkToInAddresses.Single(kv => kv.Key == fixture.DeviceWorkId).Value.ToArray();

            Assert.Equal(new[] { fixture.OutAddress }, outAddresses);
            Assert.Equal(new[] { fixture.InAddress }, inAddresses);
        });
    }

    [Fact]
    public void Passive_inference_keeps_work_in_finish_during_cleanup_until_next_cycle()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);
            var transitions = new List<Status4>();
            engine.WorkStateChanged += (_, args) =>
            {
                if (args.WorkGuid == fixture.ActiveWorkId)
                    transitions.Add(args.NewState);
            };

            engine.Start();

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
            Assert.Equal(new[] { Status4.Going, Status4.Finish }, transitions);

            ObservePassive(state, fixture.OutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, fixture.ActiveWorkId) == Status4.Going));

            Assert.Equal(new[] { Status4.Going, Status4.Finish, Status4.Going }, transitions);
            Assert.DoesNotContain(Status4.Ready, transitions);
            Assert.DoesNotContain(Status4.Homing, transitions);
        });
    }

    [Fact]
    public void Passive_force_work_finish_in_virtualplant_does_not_auto_transition_to_homing_or_ready()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
    public void Passive_inference_detects_cycle_from_repeating_window_after_noisy_prefix()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
    public void Passive_unsynced_multi_call_work_stays_ready_until_cycle_is_learned()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildUnsyncedMultiCallWorkFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(fixture.Store, engine);

            engine.Start();

            ObservePassive(state, fixture.FirstOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.FirstCallId) == Status4.Going));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.FirstInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.FirstCallId) == Status4.Finish));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.SecondOutAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.SecondCallId) == Status4.Going));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));

            ObservePassive(state, fixture.SecondInAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetCallState(engine, fixture.SecondCallId) == Status4.Finish));
            Assert.Equal(Status4.Ready, GetWorkState(engine, fixture.ActiveWorkId));
        });
    }

    [Fact]
    public void Passive_inference_keeps_finish_during_cleanup_when_learning_window_starts_with_in_off()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = BuildSingleCallFixture();
            var index = SimIndexModule.build(fixture.Store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
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
            using var engine = new EventDrivenEngine(index, RuntimeMode.Monitoring);
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
            ApiDefActionType.Normal,
            FSharpOption<Guid>.Some(deviceWorkId),
            FSharpOption<Guid>.Some(deviceWorkId));
        return apiDefId;
    }

    private static void SetIoTags(DsStore store, Guid callId, Guid apiCallId, string outAddress, string inAddress)
    {
        store.UpdateApiCallIoTags(
            callId,
            apiCallId,
            new IOTag("Out", outAddress, ""),
            new IOTag("In", inAddress, ""));
    }

    private static SimulationPanelState CreatePassiveState(
        DsStore store,
        EventDrivenEngine engine,
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
        EventDrivenEngine engine,
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

    private static Status4 GetWorkState(EventDrivenEngine engine, Guid workGuid)
    {
        var state = engine.GetWorkState(workGuid);
        return state != null && FSharpOption<Status4>.get_IsSome(state) ? state.Value : Status4.Ready;
    }

    private static Status4 GetCallState(EventDrivenEngine engine, Guid callGuid)
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
