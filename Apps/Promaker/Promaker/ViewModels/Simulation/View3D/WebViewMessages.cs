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
    private static double CalculateFloorSize(IEnumerable<DeviceInfo> devices, IEnumerable<FlowZone> flowZones)
    {
        var posDevices = devices.Where(d => d.Position != null).ToList();
        var flowZonesList = flowZones.ToList();

        if (posDevices.Count == 0 && flowZonesList.Count == 0) return MinFloorSize;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        // Include device positions
        foreach (var d in posDevices)
        {
            var pos = d.Position!.Value;
            minX = Math.Min(minX, pos.X);
            maxX = Math.Max(maxX, pos.X);
            minZ = Math.Min(minZ, pos.Z);
            maxZ = Math.Max(maxZ, pos.Z);
        }

        // Include flow zone bounds
        foreach (var zone in flowZonesList)
        {
            var zoneMinX = zone.CenterX - zone.SizeX / 2;
            var zoneMaxX = zone.CenterX + zone.SizeX / 2;
            var zoneMinZ = zone.CenterZ - zone.SizeZ / 2;
            var zoneMaxZ = zone.CenterZ + zone.SizeZ / 2;

            minX = Math.Min(minX, zoneMinX);
            maxX = Math.Max(maxX, zoneMaxX);
            minZ = Math.Min(minZ, zoneMinZ);
            maxZ = Math.Max(maxZ, zoneMaxZ);
        }

        if (double.IsInfinity(minX)) return MinFloorSize;

        var spanX = maxX - minX;
        var spanZ = maxZ - minZ;
        var span = Math.Max(spanX, spanZ);

        return Math.Max(span + FloorSizeMargin, MinFloorSize);
    }

    private async Task SendInitMessage(IEnumerable<FlowZone> flowZones, double floorSize)
    {
        // 커스텀 JSON 모델 레지스트리 (추후 프로젝트 디렉터리에서 로드 예정)
        var customModels = LoadCustomModelRegistry();

        await SendAsync(new
        {
            type = "init",
            config = new { flowZones = ToJsFlowZones(flowZones), floorSize, customModels }
        });
    }

    // FlowZone + Device 를 단일 init 메시지로 전송 — "화면 두 번 바뀜" 제거용.
    // JS 쪽은 customModels 해시가 동일하면 preload 캐시 재사용 후 devices 를 한 프레임에 배치한다.
    private async Task SendSceneInitMessage(
        IEnumerable<FlowZone> flowZones,
        IEnumerable<DeviceInfo> devices,
        double floorSize)
    {
        var customModels = LoadCustomModelRegistry();

        var deviceList = devices
            .Where(d => d.Position != null)
            .Select(d =>
            {
                var pos = d.Position!.Value;
                return new
                {
                    device = new
                    {
                        id = d.Id.ToString(),
                        name = d.Name,
                        flowName = d.FlowName,
                        modelType = d.ModelType,
                        deviceType = d.ModelType,
                        state = "R",
                        isUsedInSimulation = d.IsUsedInSimulation,
                        apiDefs = d.ApiDefs.Select(a => new
                        {
                            id = a.Id.ToString(),
                            name = a.Name,
                            callerCount = a.CallerCount
                        }).ToArray()
                    },
                    x = pos.X,
                    z = pos.Z
                };
            })
            .ToArray();

        await SendAsync(new
        {
            type = "init",
            config = new
            {
                flowZones = ToJsFlowZones(flowZones),
                devices = deviceList,
                floorSize,
                customModels
            }
        });
    }

    /// <summary>
    /// 커스텀 JSON 디바이스 모델 레지스트리를 직렬화 가능한 형태로 반환.
    /// 키 정렬(Ordinal) — JS 쪽 JSON.stringify 해시 비교의 false-positive 방지.
    /// </summary>
    private Dictionary<string, object> LoadCustomModelRegistry()
    {
        var dict = _customModelRegistry?.ToSerializableDictionary()
                   ?? new Dictionary<string, object>();
        return dict.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                   .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task SendDevices(IEnumerable<DeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            if (device.Position == null) continue;
            var pos = device.Position.Value;

            await SendAsync(new
            {
                type = "addDevice",
                device = new
                {
                    id = device.Id.ToString(),
                    name = device.Name,
                    flowName = device.FlowName,
                    modelType = device.ModelType,
                    deviceType = device.ModelType,
                    state = "R",
                    isUsedInSimulation = device.IsUsedInSimulation,
                    apiDefs = device.ApiDefs.Select(a => new
                    {
                        id = a.Id.ToString(),
                        name = a.Name,
                        callerCount = a.CallerCount
                    }).ToArray()
                },
                x = pos.X,
                z = pos.Z
            });
        }
    }


}
