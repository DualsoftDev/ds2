using Ds2.Core;
using Ds2.OpcUa.Server.Server;
using Microsoft.FSharp.Collections;
using Opc.Ua;

namespace Ds2.AasOpcUa.Tutorial.Web.Services;

/// <summary>
/// Tutorial 파일럿 5대 자산(CNC01·PM03·VIB11·VIS02·BCR05)을 서버 기동 후 seed.
/// Ds2.OpcUa.Server 는 이 프로젝트에 대해 아무것도 모르며, 자산 명세는 이 클래스가 소유.
/// </summary>
public sealed class PilotAssetSeeder : IUaAssetSeeder
{
    public IReadOnlyList<int> Seed(DsUaServer server)
    {
        var nm = server.NodeManager;

        int cnc = nm.AddAsset(
            new GlobalAssetId("urn:dualsoft:asset:cnc01"), "CNC01",
            SignalList(
                (new SignalId("line1.cnc01.spindle-speed"), "rpm",  BuiltInType.Double),
                (new SignalId("line1.cnc01.motor-temp"),    "degC", BuiltInType.Double),
                (new SignalId("line1.cnc01.cycle-count"),   "",     BuiltInType.Int64)));

        int pm = nm.AddAsset(
            new GlobalAssetId("urn:dualsoft:asset:pm03"), "PM03",
            SignalList(
                (new SignalId("line1.pm03.active-power"), "kW", BuiltInType.Double)));

        int vib = nm.AddAsset(
            new GlobalAssetId("urn:dualsoft:asset:vib11"), "VIB11",
            SignalList(
                (new SignalId("line1.vib11.rms"), "mm/s", BuiltInType.Double)));

        int vis = nm.AddAsset(
            new GlobalAssetId("urn:dualsoft:asset:vis02"), "VIS02",
            SignalList(
                (new SignalId("line1.vis02.judgement"), "", BuiltInType.String)));

        int bcr = nm.AddAsset(
            new GlobalAssetId("urn:dualsoft:asset:bcr05"), "BCR05",
            SignalList(
                (new SignalId("line1.bcr05.code"), "", BuiltInType.String)));

        return new[] { cnc, pm, vib, vis, bcr };
    }

    /// <summary>C# ValueTuple 배열을 F# `(SignalId * string * BuiltInType) list` 로 변환.</summary>
    private static FSharpList<Tuple<SignalId, string, BuiltInType>> SignalList(
        params (SignalId id, string unit, BuiltInType type)[] items)
    {
        var tuples = items.Select(x => Tuple.Create(x.id, x.unit, x.type));
        return ListModule.OfSeq(tuples);
    }
}
