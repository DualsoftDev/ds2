using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
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

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index, RuntimeMode.VirtualPlant);
            var state = CreatePassiveState(store, engine);
            var transitions = new List<Status4>();
            engine.WorkStateChanged += (_, args) =>
            {
                if (args.WorkGuid == activeWorkId)
                    transitions.Add(args.NewState);
            };

            engine.Start();

            ObservePassive(state, outAddress, "true");
            ObservePassive(state, inAddress, "true");
            ObservePassive(state, outAddress, "false");
            ObservePassive(state, inAddress, "false");

            ObservePassive(state, outAddress, "true");
            ObservePassive(state, inAddress, "true");
            ObservePassive(state, outAddress, "false");
            ObservePassive(state, inAddress, "false");

            ObservePassive(state, outAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, activeWorkId) == Status4.Going));

            ObservePassive(state, inAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, activeWorkId) == Status4.Finish));

            ObservePassive(state, outAddress, "false");
            ObservePassive(state, inAddress, "false");
            StaTestRunner.PumpPendingUi();

            Assert.Equal(Status4.Finish, GetWorkState(engine, activeWorkId));
            Assert.Equal(new[] { Status4.Going, Status4.Finish }, transitions);

            ObservePassive(state, outAddress, "true");
            Assert.True(StaTestRunner.WaitUntil(1000, () => GetWorkState(engine, activeWorkId) == Status4.Going));

            Assert.Equal(new[] { Status4.Going, Status4.Finish, Status4.Going }, transitions);
            Assert.DoesNotContain(Status4.Ready, transitions);
            Assert.DoesNotContain(Status4.Homing, transitions);
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

    private static SimulationPanelState CreatePassiveState(DsStore store, EventDrivenEngine engine)
    {
        var state = new SimulationPanelState(
            () => store,
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            () => Array.Empty<EntityNode>(),
            _ => { });

        state.SelectedRuntimeMode = RuntimeMode.VirtualPlant;
        SetPrivateField(state, "_simEngine", engine);
        InvokeNonPublic(state, "PreparePassiveModeIoInference");
        return state;
    }

    private static void ObservePassive(SimulationPanelState state, string address, string value)
    {
        InvokeNonPublic(state, "ObserveAndInferPassiveState", address, value);
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
}
