using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.View3D;

namespace Promaker.ViewModels;

public partial class ThreeDViewState
{
    private static SceneEngine CreateSceneEngine(DsStore store)
    {
        var layoutDir = GetLayoutDirectory();
        return new SceneEngine(store, new Ds2.View3D.Persistence.JsonFileLayoutStore(layoutDir));
    }

    private void ClearCaches()
    {
        _callToDevice.Clear();
        _callToApiDef.Clear();
        _deviceToSystem.Clear();
        _apiDefStateCache.Clear();
        _deviceToApiDefs.Clear();
    }

    private void BuildMappings(DsStore store, SceneData scene)
    {
        var deviceNameToId = scene.Devices.ToDictionary(d => d.Name, d => d.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (systemId, system) in store.Systems)
        {
            if (deviceNameToId.TryGetValue(system.Name, out var deviceId))
                _deviceToSystem[deviceId] = systemId;
        }

        // Store all ApiDefs for each Device (from SceneData)
        foreach (var deviceNode in scene.Devices)
        {
            var apiDefList = deviceNode.ApiDefs.ToList();
            if (apiDefList.Count > 0)
            {
                _deviceToApiDefs[deviceNode.Id] = new HashSet<Guid>(
                    apiDefList.Select(a => a.Id));
            }
        }

        BuildCallToDeviceMapping(store, deviceNameToId);
    }

    /// <summary>
    /// Call → ApiDef → TargetDevice 체인 추적으로 정확한 매핑 구축.
    /// ApiDef.ParentId = System (Device)이므로 ApiCall을 통해 어느 Device가 호출되는지 추적한다.
    /// </summary>
    private void BuildCallToDeviceMapping(DsStore store, Dictionary<string, Guid> deviceNameToId)
    {
        foreach (var (callId, call) in store.Calls)
        {
            foreach (var apiCall in call.ApiCalls)
            {
                if (apiCall.ApiDefId == null) continue;

                var apiDefId = apiCall.ApiDefId.Value;

                // ApiCall → ApiDef 추적
                if (!store.ApiDefs.TryGetValue(apiDefId, out var apiDef))
                    continue;

                // Call → ApiDef 매핑
                _callToApiDef[callId] = apiDefId;

                // ApiDef.ParentId = TargetSystem (Device)
                var targetSystemId = apiDef.ParentId;
                if (!store.Systems.TryGetValue(targetSystemId, out var targetSystem))
                    continue;

                // System.Name → DeviceId 매핑
                if (deviceNameToId.TryGetValue(targetSystem.Name, out var deviceId))
                {
                    _callToDevice[callId] = deviceId;
                    break;  // 하나의 Call은 하나의 주요 Device에만 매핑
                }
            }
        }
    }

}
