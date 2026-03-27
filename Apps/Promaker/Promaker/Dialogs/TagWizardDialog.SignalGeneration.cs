using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Ds2.IOList;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// 신호 생성
    /// </summary>
    private bool GenerateSignals()
    {
        var configPath = TemplateManager.AddressConfigPath;

        if (!System.IO.File.Exists(configPath))
        {
            DialogHelpers.ShowThemedMessageBox(
                "address_config.txt 파일을 찾을 수 없습니다.\n\n" +
                "템플릿 폴더를 열어서 설정 파일을 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            var templateDir = TemplateManager.TemplatesFolderPath;
            var result = _generator.Generate(_store, templateDir);

            if (!_generator.IsSuccess(result))
            {
                var errors = _generator.GetErrorSummary(result);
                DialogHelpers.ShowThemedMessageBox(
                    $"신호 생성 중 오류가 발생했습니다:\n\n{errors}",
                    "TAG Wizard - 오류",
                    MessageBoxButton.OK,
                    "✖");
                return false;
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
    /// 신호 적용
    /// </summary>
    private bool ApplySignals()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 IO 신호가 없습니다.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        var validRows = _ioRows.Where(r => r.ApiCallId != Guid.Empty && r.CallId != Guid.Empty).ToList();
        var unmatchedCount = _unmatchedRows.Count;

        if (unmatchedCount > 0)
        {
            var result = DialogHelpers.ShowThemedMessageBox(
                $"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않았습니다.\n\n" +
                $"'매칭 실패' 탭에서 상세 내역을 확인할 수 있습니다.\n\n" +
                $"✓ 매칭된 {validRows.Count}개 항목만 적용됩니다.\n\n" +
                $"계속하시겠습니까?",
                "TAG Wizard - 확인",
                MessageBoxButton.YesNo,
                "?");

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";

            _successCount = 0;
            var failedItems = new List<string>();

            foreach (var row in validRows)
            {
                try
                {
                    _store.UpdateApiCallIoTags(
                        row.CallId,
                        row.ApiCallId,
                        row.OutSymbol,
                        row.OutAddress,
                        row.InSymbol,
                        row.InAddress);

                    _successCount++;
                }
                catch (Exception ex)
                {
                    failedItems.Add($"{row.Flow}/{row.Device}/{row.Api}: {ex.Message}");
                }
            }

            // 완료 메시지 구성
            var summary = new StringBuilder();
            summary.AppendLine($"✅ {_successCount}개 ApiCall에 IO 태그가 성공적으로 적용되었습니다.");
            summary.AppendLine($"📊 IO 신호: {_ioRows.Count}개");
            summary.AppendLine($"📊 Dummy 신호: {_dummyRows.Count}개");

            if (failedItems.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"⚠️ {failedItems.Count}개 항목 적용 실패:");
                foreach (var item in failedItems.Take(3))
                {
                    summary.AppendLine($"  • {item}");
                }
                if (failedItems.Count > 3)
                {
                    summary.AppendLine($"  ... 외 {failedItems.Count - 3}개");
                }
            }

            CompletionSummaryText.Text = summary.ToString();

            return _successCount > 0;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
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
                // DeviceName은 ApiDef.Name이므로 ApiDefId를 통해 매칭
                var matchedApiCall = matchedCall.ApiCalls
                    .FirstOrDefault(ac =>
                    {
                        // ApiDefId 확인
                        if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId))
                            return false;

                        var apiDefId = ac.ApiDefId.Value;

                        // ApiDef 조회
                        var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                        if (!FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                            return false;

                        var apiDef = apiDefOption.Value;

                        // ApiDef.Name과 DeviceName 비교
                        return apiDef.Name.Equals(key.DeviceName, StringComparison.OrdinalIgnoreCase);
                    });
                apiCallId = matchedApiCall?.Id ?? Guid.Empty;

                // Extract Device (System.Name) and Api (ApiDef.Name) from matched ApiCall
                if (matchedApiCall != null && FSharpOption<Guid>.get_IsSome(matchedApiCall.ApiDefId))
                {
                    var apiDefId = matchedApiCall.ApiDefId.Value;
                    var apiDefOption = DsQuery.getApiDef(apiDefId, _store);
                    if (FSharpOption<ApiDef>.get_IsSome(apiDefOption))
                    {
                        var apiDef = apiDefOption.Value;
                        api = apiDef.Name;

                        // Get parent System name
                        if (_store.Systems.TryGetValue(apiDef.ParentId, out var system))
                        {
                            device = system.Name;
                        }
                    }
                }
            }

            rows.Add(new IoBatchRow(
                callId: callId,
                apiCallId: apiCallId,
                flow: key.FlowName,
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
    private static Call? FindCallByName(DsStore store, string flowName, string workName, string callName)
    {
        var flows = DsQuery.allFlows(store);

        foreach (var flow in flows)
        {
            if (!flow.Name.Equals(flowName, StringComparison.OrdinalIgnoreCase))
                continue;

            var works = DsQuery.worksOf(flow.Id, store);
            foreach (var work in works)
            {
                if (!work.Name.Equals(workName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var calls = DsQuery.callsOf(work.Id, store);
                foreach (var call in calls)
                {
                    if (call.Name.Equals(callName, StringComparison.OrdinalIgnoreCase))
                        return call;
                }
            }
        }

        return null;
    }
}
