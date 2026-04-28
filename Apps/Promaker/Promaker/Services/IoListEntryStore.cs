using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.Services;

/// <summary>
/// IoList 엔트리 읽기 + 검증 전용 저장소 (B안: IoList = 단일 진실원).
/// 데이터 쓰기는 FBTagMapStore.Save 경로로 일원화되어 여기서는 읽기 API 만 제공.
/// </summary>
public static class IoListEntryStore
{
    private static ControlSystemProperties? TryGetCp(DsStore store)
    {
        var opt = Queries.tryGetPrimaryControlProps(store);
        return Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(opt) ? opt.Value : null;
    }

    public static List<IoListEntryDto> LoadAll(DsStore store)
    {
        var cp = TryGetCp(store);
        return cp == null
            ? new List<IoListEntryDto>()
            : cp.IoListEntries.Select(ToDto).ToList();
    }

    public static List<DummyIoEntryDto> LoadDummies(DsStore store)
    {
        var cp = TryGetCp(store);
        return cp == null
            ? new List<DummyIoEntryDto>()
            : cp.DummyEntries.Select(ToDummyDto).ToList();
    }

    /// <summary>중복 바인딩 + 미바인딩 검증.</summary>
    public static List<string> ValidateBindings(DsStore store)
    {
        var cp = TryGetCp(store);
        if (cp == null) return new();
        var dup = IoListValidation.detectDuplicateBindings(cp.IoListEntries);
        var unbound = IoListValidation.detectUnboundEntries(cp.IoListEntries);
        return dup.Concat(unbound).ToList();
    }

    private static IoListEntryDto ToDto(IoListEntry e) => new()
    {
        Name         = e.Name,
        Address      = e.Address,
        Direction    = e.Direction,
        DataType     = e.DataType,
        Comment      = e.Comment,
        FlowName     = e.FlowName,
        DeviceAlias  = e.DeviceAlias,
        ApiDefName   = e.ApiDefName,
        SystemId     = e.SystemId,
        TargetFBType = e.Binding.TargetFBType,
        TargetFBPort = e.Binding.TargetFBPort,
    };

    private static DummyIoEntryDto ToDummyDto(DummyIoEntry e) => new()
    {
        Name         = e.Name,
        Address      = e.Address,
        Comment      = e.Comment,
        FlowName     = e.FlowName,
        DeviceAlias  = e.DeviceAlias,
        ApiDefName   = e.ApiDefName,
        SystemId     = e.SystemId,
        TargetFBType = e.Binding.TargetFBType,
        TargetFBPort = e.Binding.TargetFBPort,
    };
}

public class IoListEntryDto
{
    public string Name         { get; set; } = "";
    public string Address      { get; set; } = "";
    public string Direction    { get; set; } = "Input";
    public string DataType     { get; set; } = "BOOL";
    public string Comment      { get; set; } = "";
    public string FlowName     { get; set; } = "";
    public string DeviceAlias  { get; set; } = "";
    public string ApiDefName   { get; set; } = "";
    public Guid   SystemId     { get; set; } = Guid.Empty;
    public string TargetFBType { get; set; } = "";
    public string TargetFBPort { get; set; } = "";
}

public class DummyIoEntryDto
{
    public string Name         { get; set; } = "";
    public string Address      { get; set; } = "";
    public string Comment      { get; set; } = "";
    public string FlowName     { get; set; } = "";
    public string DeviceAlias  { get; set; } = "";
    public string ApiDefName   { get; set; } = "";
    public Guid   SystemId     { get; set; } = Guid.Empty;
    public string TargetFBType { get; set; } = "";
    public string TargetFBPort { get; set; } = "";
}
