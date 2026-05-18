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
    public void OnWorkStateChanged(Guid workId, Status4 newState)
    {
        // Work state is not visualized in Device Scene (only Call/ApiDef/Device)
    }

    public async Task ShowApiDefConnections(Guid deviceId, string apiDefName,
        IEnumerable<object> outgoing, IEnumerable<object> incoming)
    {
        if (!CanSendMessage()) return;
        await SendAsync(new
        {
            type = "showApiDefConnections",
            deviceId = deviceId.ToString(),
            apiDefName,
            outgoing = outgoing.ToArray(),
            incoming = incoming.ToArray()
        });
    }

    public void OnCallStateChanged(Guid callId, Status4 newState)
    {
        if (!CanSendMessage()) return;

        // Buffer ApiDef state
        if (_callToApiDef.TryGetValue(callId, out var apiDefId))
        {
            _apiDefStateCache[apiDefId] = newState;
            _pendingApiDefStates[apiDefId] = ToStateCode(newState);
        }

        // Buffer Device state (derived from all its ApiDefs)
        if (_callToDevice.TryGetValue(callId, out var deviceId) &&
            _deviceToApiDefs.TryGetValue(deviceId, out var apiDefIds))
        {
            var deviceState = Status4.Ready;
            foreach (var id in apiDefIds)
            {
                if (_apiDefStateCache.GetValueOrDefault(id, Status4.Ready) == Status4.Going)
                {
                    deviceState = Status4.Going;
                    break;
                }
            }
            _pendingDeviceStates[deviceId] = ToStateCode(deviceState);
        }

        ScheduleFlush();
    }

    /// <summary>
    /// UI 스레드 Background 우선순위로 배치 전송 예약.
    /// 최소 FlushIntervalMs 간격으로 쓰로틀링하여 WebView2 과부하를 방지한다.
    /// </summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;

        var elapsed = (DateTime.UtcNow - _lastFlushTime).TotalMilliseconds;
        if (elapsed >= FlushIntervalMs)
        {
            // 충분히 시간이 지남 — 즉시 flush
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                FlushPendingStates);
        } 
        else
        {
            // 아직 간격 미달 — 남은 시간만큼 지연 후 flush
            var delay = TimeSpan.FromMilliseconds(FlushIntervalMs - elapsed);
            var timer = new System.Windows.Threading.DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = delay
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FlushPendingStates();
            };
            timer.Start();
        }
    }

    private void FlushPendingStates()
    {
        _flushScheduled = false;
        _lastFlushTime = DateTime.UtcNow;
        if (_sendToWebView == null) return;

        // ApiDef + Device 상태를 단일 메시지로 합쳐서 WebView2 마샬링 횟수를 줄인다
        object[]? apiDefArr = null;
        object[]? deviceArr = null;

        if (_pendingApiDefStates.Count > 0)
        {
            apiDefArr = _pendingApiDefStates
                .Select(kv => (object)new { id = kv.Key.ToString(), state = kv.Value })
                .ToArray();
            _pendingApiDefStates.Clear();
        }

        if (_pendingDeviceStates.Count > 0)
        {
            deviceArr = _pendingDeviceStates
                .Select(kv => (object)new { id = kv.Key.ToString(), state = kv.Value })
                .ToArray();
            _pendingDeviceStates.Clear();
        }

        if (apiDefArr != null || deviceArr != null)
        {
            // fire-and-forget: UI 스레드 블로킹 방지
            _ = SendAsync(new
            {
                type = "batchStateUpdate",
                apiDefStates = apiDefArr,
                deviceStates = deviceArr
            });
        }
    }

}
