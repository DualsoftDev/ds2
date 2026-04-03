using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.IOList;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 신호 생성 + 변환 + 검증
/// </summary>
public partial class TagWizardDialog
{
    /// <summary>
    /// 신호 생성
    /// </summary>
    private bool GenerateSignals()
    {
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            // 주소 설정 파일이 없으면 자동 생성
            EnsureAddressConfigFiles();

            // 프로젝트에서 사용된 시스템 타입 수집 및 템플릿 자동 생성
            EnsureTemplatesForUsedSystemTypes();

            var templateDir = TemplateManager.TemplatesFolderPath;
            var result = _generator.Generate(_store, templateDir);

            if (!_generator.IsSuccess(result))
            {
                // 오류를 다이얼로그 내 탭에 표시
                DisplayErrors(result);
                return true; // Step 3로 이동하여 오류 탭 표시
            }

            // IO 및 Dummy 신호 변환
            _ioRows.Clear();
            _dummyRows.Clear();

            foreach (var row in ConvertSignalsToRows(result))
                _ioRows.Add(row);

            foreach (var row in ConvertDummySignalsToRows(result))
                _dummyRows.Add(row);

            // 매칭 검증 및 분류
            ValidateAndClassifySignals();

            // 상태 메시지
            var unmatchedCount = _unmatchedRows.Count;
            GenerationStatusText.Text = unmatchedCount > 0
                ? $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개 생성 | ⚠ 매칭 실패 {unmatchedCount}개"
                : $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개가 생성되었습니다. 모든 신호가 매칭되었습니다.";

            return true;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"신호 생성 중 예외가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "적용 ▶";
        }
    }

    /// <summary>
    /// 매칭 검증 및 분류
    /// </summary>
    private void ValidateAndClassifySignals()
    {
        _unmatchedRows.Clear();

        foreach (var row in _ioRows)
        {
            if (row.CallId == Guid.Empty || row.ApiCallId == Guid.Empty)
            {
                string reason = (row.CallId == Guid.Empty, row.ApiCallId == Guid.Empty) switch
                {
                    (true, true) => "Call 및 ApiCall을 찾을 수 없음",
                    (true, false) => "Call을 찾을 수 없음",
                    _ => "ApiCall(Device)을 찾을 수 없음",
                };

                _unmatchedRows.Add(new UnmatchedSignalRow(
                    Flow: row.Flow,
                    Device: row.Device,
                    Api: row.Api,
                    OutSymbol: row.OutSymbol,
                    OutAddress: row.OutAddress,
                    InSymbol: row.InSymbol,
                    InAddress: row.InAddress,
                    FailureReason: reason
                ));
            }
        }

        // 탭 표시 업데이트
        if (_unmatchedRows.Count > 0)
        {
            UnmatchedTabItem.Visibility = Visibility.Visible;
            UnmatchedCountText.Text = _unmatchedRows.Count.ToString();
        }
        else
        {
            UnmatchedTabItem.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// SignalRecord → IoBatchRow 변환
    /// </summary>
    private List<IoBatchRow> ConvertSignalsToRows(GenerationResult result)
    {
        var rows = new List<IoBatchRow>();

        // Group signals by Flow/Work/Call/Device
        var grouped = result.IoSignals
            .GroupBy(s => new { s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;

            // Find input and output signals (IoType starts with I/Q)
            var inputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase));
            var outputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase));

            // ApiCall 매칭
            var matchedCall = FindCallByName(_store, key.FlowName, key.WorkName, key.CallName);
            var callId = matchedCall?.Id ?? Guid.Empty;

            var apiCallId = Guid.Empty;
            string device = "UNKNOWN";
            string api = key.DeviceName;  // Initial value (DeviceName represents ApiDef.Name)

            if (matchedCall != null)
            {
                var matchedApiCall = Device.findApiCallByDeviceName(matchedCall, key.DeviceName, _store)?.Value;
                apiCallId = matchedApiCall?.Id ?? Guid.Empty;

                if (matchedApiCall?.ApiDefId?.Value is { } apiDefId)
                {
                    var apiDef = Queries.getApiDef(apiDefId, _store)?.Value;
                    if (apiDef != null)
                    {
                        api = apiDef.Name;
                        if (_store.Systems.TryGetValue(apiDef.ParentId, out var system))
                            device = system.Name;
                    }
                }
            }

            rows.Add(new IoBatchRow(
                callId: callId,
                apiCallId: apiCallId,
                flow: key.FlowName,
                work: key.WorkName,
                device: device,
                api: api,
                inAddress: inputSignal?.Address ?? "",
                inSymbol: inputSignal?.VarName ?? "",
                outAddress: outputSignal?.Address ?? "",
                outSymbol: outputSignal?.VarName ?? ""
            ));
        }

        return rows;
    }

    /// <summary>
    /// DummySignal → DummySignalRow 변환
    /// </summary>
    private List<DummySignalRow> ConvertDummySignalsToRows(GenerationResult result)
    {
        return result.DummySignals
            .Select(signal => new DummySignalRow(
                Flow: signal.FlowName,
                Work: signal.WorkName,
                Call: signal.CallName,
                Symbol: signal.VarName,
                Address: signal.Address,
                Type: signal.IoType
            ))
            .ToList();
    }

    /// <summary>
    /// Call 이름으로 검색
    /// </summary>
    private static Call? FindCallByName(DsStore store, string flowName, string workName, string callName) =>
        Device.findCallByName(flowName, workName, callName, store)?.Value;
}
