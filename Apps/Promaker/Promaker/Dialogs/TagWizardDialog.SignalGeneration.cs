using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Plc.Xgi;
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

            // IoList 파이프라인은 여전히 디렉토리 입력을 받는다. AASX 내 Preset 데이터를
            // 휘발성 임시 디렉토리에 txt 로 emit 후 호출 → 즉시 삭제.
            // 영구 AppData 경로는 사용하지 않는다.
            using var tempDir = PresetToTempTemplateDir.Materialize(_store);
            var result = _generator.Generate(_store, tempDir.Path);

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
            // Api_None 행은 의도적으로 API 미바인딩 — 매칭 실패가 아니므로 분류 대상에서 제외.
            if (string.IsNullOrEmpty(row.Api))
                continue;

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
    /// SignalRecord → IoBatchRow 변환 (B안 v2: ApiCallId 기준 1:1 행 생성)
    /// </summary>
    private List<IoBatchRow> ConvertSignalsToRows(GenerationResult result)
    {
        var rows = new List<IoBatchRow>();

        // Api_None 신호: 묶음 없이 신호 1개 = 행 1개 (API 컬럼 빈칸).
        foreach (var s in result.IoSignals.Where(IsApiNone))
            rows.Add(MakeSingleSignalRow(s));

        // 그 외: ApiDefName 기준 IW + QW 페어링.
        var grouped = result.IoSignals
            .Where(s => !IsApiNone(s))
            .GroupBy(s => new { s.ApiCallId, s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;
            var inputSignal  = group.FirstOrDefault(s => s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase));
            var outputSignal = group.FirstOrDefault(s => s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase));

            // ApiCall 매칭 — SignalRecord.ApiCallId 우선, 없으면 이름 fallback (legacy)
            var matchedCall = FindCallByName(_store, key.FlowName, key.WorkName, key.CallName);
            var callId = matchedCall?.Id ?? Guid.Empty;

            var apiCallId = key.ApiCallId;
            string device = "UNKNOWN";
            string api = key.DeviceName;

            ApiCall? matchedApiCall = null;
            if (matchedCall != null)
            {
                if (apiCallId != Guid.Empty)
                    matchedApiCall = matchedCall.ApiCalls.FirstOrDefault(ac => ac.Id == apiCallId);
                if (matchedApiCall == null)
                {
                    matchedApiCall = Device.findApiCallByDeviceName(matchedCall, key.DeviceName, _store)?.Value;
                    apiCallId = matchedApiCall?.Id ?? apiCallId;
                }
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

    private static bool IsApiNone(SignalRecord s) =>
        string.Equals(s.DeviceName, TagWizardDialog.ApiNoneSentinel, StringComparison.OrdinalIgnoreCase);

    private static IoBatchRow MakeSingleSignalRow(SignalRecord s)
    {
        bool isInput  = s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase);
        bool isOutput = s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase);
        return new IoBatchRow(
            callId:     Guid.Empty,
            apiCallId:  s.ApiCallId,
            flow:       s.FlowName,
            work:       s.WorkName,
            device:     s.DeviceAlias,
            api:        "",                                 // Api_None → 빈칸
            inAddress:  isInput  ? s.Address : "",
            inSymbol:   isInput  ? s.VarName : "",
            outAddress: isOutput ? s.Address : "",
            outSymbol:  isOutput ? s.VarName : "");
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
