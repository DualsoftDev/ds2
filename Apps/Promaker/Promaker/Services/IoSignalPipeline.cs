using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using Plc.Xgi;
using Plc.Xgi.SignalPipelineV2;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// IO 신호 생성/변환 단일 진입점.
/// 내부적으로 통합 SignalPipeline (B안 v3, 단일 패스) 호출 — txt round-trip 없음.
/// </summary>
public static class IoSignalPipeline
{
    public sealed record GenerateResult(
        IReadOnlyList<IoBatchRow> IoRows,
        IReadOnlyList<DummySignalRow> DummyRows,
        IReadOnlyList<string> ErrorMessages,
        IReadOnlyList<string> WarningMessages);

    public static GenerateResult GenerateAll(DsStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        try
        {
            var presets = FBTagMapStore.ToFSharpMap(store);
            var templatePath = FBPortCatalog.DefaultTemplatePath;
            var templateOpt = !string.IsNullOrEmpty(templatePath) && System.IO.File.Exists(templatePath)
                ? FSharpOption<string>.Some(templatePath)
                : FSharpOption<string>.None;

            var pr = Generator.run(store, presets, templateOpt);
            var rows = pr.Signals.ToList();

            return new GenerateResult(
                BuildIoBatchRows(rows),
                BuildDummyRows(rows),
                Array.Empty<string>(),
                pr.Diagnostics.Select(d => $"[{d.Severity}] {d.Code} — {d.Message}").ToList());
        }
        catch (Exception ex)
        {
            return new GenerateResult(
                Array.Empty<IoBatchRow>(), Array.Empty<DummySignalRow>(),
                new[] { ex.Message }, Array.Empty<string>());
        }
    }

    // ── SignalRow → IoBatchRow / DummySignalRow ─────────────────────────────

    public static List<IoBatchRow> BuildIoBatchRows(IReadOnlyList<SignalRow> signals)
    {
        var rows = new List<IoBatchRow>();
        var ioOnly = signals.Where(s => !s.IsSpare && !IsMw(s)).ToList();

        foreach (var s in ioOnly.Where(IsApiNone))
            rows.Add(MakeApiNoneRow(s));

        var grouped = ioOnly.Where(s => !IsApiNone(s))
                            .GroupBy(s => (s.CallId, s.ApiCallId));

        foreach (var g in grouped)
        {
            var input  = g.FirstOrDefault(s => IsIw(s));
            var output = g.FirstOrDefault(s => IsQw(s));
            var sample = input ?? output;
            if (sample == null) continue;

            rows.Add(new IoBatchRow(
                callId:      sample.CallId,
                apiCallId:   sample.ApiCallId,
                flow:        sample.FlowName,
                work:        sample.WorkName,
                device:      sample.DeviceAlias,
                api:         sample.ApiName,
                inAddress:   input?.Address ?? "",
                inSymbol:    input?.VarName ?? "",
                outAddress:  output?.Address ?? "",
                outSymbol:   output?.VarName ?? "",
                outDataType: output != null ? output.DataType.ToString() : "BOOL",
                inDataType:  input  != null ? input.DataType.ToString()  : "BOOL"));
        }
        return rows;
    }

    public static List<IoBatchRow> BuildApiNoneRows(IReadOnlyList<SignalRow> signals) =>
        signals.Where(s => !s.IsSpare && !IsMw(s) && IsApiNone(s))
               .Select(MakeApiNoneRow).ToList();

    public static List<DummySignalRow> BuildDummyRows(IReadOnlyList<SignalRow> signals) =>
        signals.Where(s => !s.IsSpare && IsMw(s))
               .Select(s => new DummySignalRow(
                   Flow:     s.FlowName,
                   Work:     s.WorkName,
                   Call:     s.CallName,
                   Symbol:   s.VarName,
                   Address:  s.Address,
                   Type:     "MW",
                   DataType: s.DataType.ToString()))
               .ToList();

    public static List<UnmatchedSignalRow> ClassifyUnmatched(IEnumerable<IoBatchRow> rows)
    {
        var list = new List<UnmatchedSignalRow>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Api)) continue;
            if (row.CallId != Guid.Empty && row.ApiCallId != Guid.Empty) continue;
            string reason = (row.CallId == Guid.Empty, row.ApiCallId == Guid.Empty) switch
            {
                (true, true)  => "Call 및 ApiCall을 찾을 수 없음",
                (true, false) => "Call을 찾을 수 없음",
                _             => "ApiCall(Device)을 찾을 수 없음",
            };
            list.Add(new UnmatchedSignalRow(
                Flow: row.Flow, Device: row.Device, Api: row.Api,
                OutSymbol: row.OutSymbol, OutAddress: row.OutAddress,
                InSymbol: row.InSymbol, InAddress: row.InAddress,
                FailureReason: reason));
        }
        return list;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static bool IsIw(SignalRow s) => s.Section.IsIwSection;
    private static bool IsQw(SignalRow s) => s.Section.IsQwSection;
    private static bool IsMw(SignalRow s) => s.Section.IsMwSection;

    private static bool IsApiNone(SignalRow s) =>
        s.ApiCallId == Guid.Empty
        || string.Equals(s.ApiName ?? "", IoConstants.ApiNoneSentinel, StringComparison.OrdinalIgnoreCase)
        || s.ApiName == "-"
        || string.IsNullOrEmpty(s.ApiName);

    private static IoBatchRow MakeApiNoneRow(SignalRow s)
    {
        var dt = s.DataType.ToString();
        return new IoBatchRow(
            callId:      Guid.Empty,
            apiCallId:   s.ApiCallId,
            flow:        s.FlowName,
            work:        s.WorkName,
            device:      string.IsNullOrWhiteSpace(s.DeviceAlias) ? IoConstants.ApiNoneSentinel : s.DeviceAlias,
            api:         "",
            inAddress:   IsIw(s) ? s.Address : "",
            inSymbol:    IsIw(s) ? s.VarName : "",
            outAddress:  IsQw(s) ? s.Address : "",
            outSymbol:   IsQw(s) ? s.VarName : "",
            outDataType: IsQw(s) ? dt : "BOOL",
            inDataType:  IsIw(s) ? dt : "BOOL");
    }
}
