using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Reverse.Core;

namespace Ds2.Reverse.Studio.Models;

public static class GeneratorFactory
{
    public static GeneratedModel Generate(GeneratorOptions opts) =>
        opts.Case switch
        {
            ModelCase.InlineLine => InlineLineGenerator.Generate(opts),
            ModelCase.StandaloneDag => StandaloneDagGenerator.Generate(opts),
            ModelCase.MultiFlow => MultiFlowGenerator.Generate(opts),
            ModelCase.Branch => BranchGenerator.Generate(opts),
            ModelCase.RecycleLoop => RecycleLoopGenerator.Generate(opts),
            ModelCase.PlcCell => PlcCellGenerator.Generate(opts),
            ModelCase.CapacityVar => CapacityVarGenerator.Generate(opts),
            ModelCase.AdversarialMix => AdversarialMixGenerator.Generate(opts),
            _ => throw new ArgumentException($"unknown case: {opts.Case}")
        };
}

/// <summary>Case A — 인라인 설비. W1 → W2 → ... → Wn (StartReset) + 랜덤 변형.
///
/// 랜덤성:
///   1) stage 수 ±1 변동
///   2) 30% 확률로 일부 stage 에 SENSOR call 추가
///   3) 20% 확률로 일부 stage 가 ADV / SENSOR Group 페어
///   4) 일부 stage 는 ADV/RET 외에 PRE step 추가
/// </summary>
public static class InlineLineGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nStagesBase = Math.Max(2, opts.NStages);
        // ±1 변동 (NStages 가 N 이면 N-1 ~ N+1)
        var nStages = Math.Max(2, nStagesBase + rng.Next(-1, 2));

        var t = ModelBuilder.emptyStore("InlineLine", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Line");

        var workIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();

        for (int i = 1; i <= nStages; i++)
        {
            var localName = $"W{i}";
            var workId = ModelBuilder.addWork(store, flowId, "Line", localName);
            workIds.Add(workId);

            // 각 Work 안 ADV/RET 기본 페어
            var advName = $"S{i}.ADV";
            var retName = $"S{i}.RET";
            var advId = ModelBuilder.addCallWithApi(store, workId, flowId, advName, "");
            var retId = ModelBuilder.addCallWithApi(store, workId, flowId, retName, "");
            callsByName.Add((advName, advId));
            callsByName.Add((retName, retId));

            // 30% 확률로 PRE step 추가 (PRE → ADV → RET)
            bool addPre = rng.NextDouble() < 0.3;
            // 20% 확률로 SENSOR call (ADV 와 Group)
            bool addSensor = rng.NextDouble() < 0.2;

            if (addPre)
            {
                var preName = $"S{i}.PRE";
                var preId = ModelBuilder.addCallWithApi(store, workId, flowId, preName, "");
                callsByName.Add((preName, preId));
                ModelBuilder.addArrowCall(store, workId, preId, advId, ArrowType.Start);
                arrows.Add(new ArrowSpec(preName, advName, ArrowKindStd.Start));
            }

            // ADV → RET (Start)
            ModelBuilder.addArrowCall(store, workId, advId, retId, ArrowType.Start);
            arrows.Add(new ArrowSpec(advName, retName, ArrowKindStd.Start));

            if (addSensor)
            {
                var sensorName = $"S{i}.SENSOR";
                var sensorId = ModelBuilder.addCallWithApi(store, workId, flowId, sensorName, "");
                callsByName.Add((sensorName, sensorId));
                // ADV 와 SENSOR 동시 (Group)
                ModelBuilder.addArrowCall(store, workId, advId, sensorId, ArrowType.Group);
                arrows.Add(new ArrowSpec(advName, sensorName, ArrowKindStd.Group));
            }
        }

        // Works 간 StartReset chain
        for (int i = 0; i < workIds.Count - 1; i++)
        {
            ModelBuilder.addArrowWork(store, mainSys, workIds[i], workIds[i + 1],
                                     ArrowType.StartReset);
            arrows.Add(new ArrowSpec($"Line.W{i + 1}", $"Line.W{i + 2}",
                                    ArrowKindStd.StartReset));
        }

        // Device duration: alias 별 base duration (50~500ms random)
        var deviceDurations = BuildDeviceDurations(callsByName, rng);

        return new GeneratedModel(store, arrows, "InlineLine", opts,
                                 mainSys, flowId, workIds, callsByName,
                                 deviceDurations);
    }

    internal static Dictionary<string, long> BuildDeviceDurations(
        IEnumerable<(string name, Guid id)> calls, Random rng)
    {
        var result = new Dictionary<string, long>();
        var devices = calls
            .Select(c => c.name.IndexOf('.') is int i && i > 0
                ? c.name.Substring(0, i) : c.name)
            .Distinct();
        foreach (var d in devices)
            result[d] = rng.Next(50, 500);
        return result;
    }
}

/// <summary>Case B — 단독 Work + Internal random DAG.</summary>
public static class StandaloneDagGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nCalls = Math.Max(3, opts.NCalls);
        var density = Math.Clamp(opts.Density, 0.05, 0.8);
        var groupProb = Math.Clamp(opts.GroupProb, 0.0, 0.3);

        var t = ModelBuilder.emptyStore("DagWork", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Flow1");
        var workId = ModelBuilder.addWork(store, flowId, "Flow1", "WorkA");

        var callIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        for (int i = 0; i < nCalls; i++)
        {
            var name = $"N{i}.S";
            var cid = ModelBuilder.addCallWithApi(store, workId, flowId, name, "");
            callIds.Add(cid);
            callsByName.Add((name, cid));
        }

        // Random DAG: i < j with probability density
        var arrows = new List<ArrowSpec>();
        for (int i = 0; i < nCalls; i++)
        {
            for (int j = i + 1; j < nCalls; j++)
            {
                if (rng.NextDouble() < density)
                {
                    bool isGroup = (j - i == 1) && rng.NextDouble() < groupProb;
                    var at = isGroup ? ArrowType.Group : ArrowType.Start;
                    ModelBuilder.addArrowCall(store, workId, callIds[i], callIds[j], at);
                    arrows.Add(new ArrowSpec($"N{i}.S", $"N{j}.S",
                        isGroup ? ArrowKindStd.Group : ArrowKindStd.Start));
                }
            }
        }

        // 최소 1 arrow 보장 — 너무 sparse 면 chain 한 줄 추가
        if (arrows.Count == 0 && nCalls >= 2)
        {
            ModelBuilder.addArrowCall(store, workId, callIds[0], callIds[1], ArrowType.Start);
            arrows.Add(new ArrowSpec("N0.S", "N1.S", ArrowKindStd.Start));
        }

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);

        return new GeneratedModel(store, arrows, "DagWork", opts,
                                 mainSys, flowId, new List<Guid> { workId }, callsByName,
                                 deviceDurations);
    }
}

/// <summary>Case C — Multi-Flow Inline. 1~20 flows. 각 flow 안 inline chain.
/// Cross-flow arrows 로 flow 간 동기화 (***REDACTED***EVO 의 station chain 패턴 모방).
/// </summary>
public static class MultiFlowGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nFlows = Math.Clamp(opts.NFlows, 1, 20);
        var stagesPerFlow = Math.Max(2, opts.StagesPerFlow);

        var t = ModelBuilder.emptyStore("MultiFlow", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var allWorkIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();
        var firstWorkByFlow = new List<(string flowName, Guid workId)>();
        var lastWorkByFlow = new List<(string flowName, Guid workId)>();
        Guid firstFlowId = Guid.Empty;

        for (int f = 1; f <= nFlows; f++)
        {
            var flowName = $"F{f}";
            var flowId = ModelBuilder.addFlow(store, mainSys, flowName);
            if (f == 1) firstFlowId = flowId;
            // ±1 stage 변동
            var nStages = Math.Max(2, stagesPerFlow + rng.Next(-1, 2));
            var workIdsInFlow = new List<Guid>();

            for (int i = 1; i <= nStages; i++)
            {
                var localName = $"W{i}";
                var workId = ModelBuilder.addWork(store, flowId, flowName, localName);
                workIdsInFlow.Add(workId);
                allWorkIds.Add(workId);

                var advName = $"S{f}{i}.ADV";
                var retName = $"S{f}{i}.RET";
                var advId = ModelBuilder.addCallWithApi(store, workId, flowId, advName, "");
                var retId = ModelBuilder.addCallWithApi(store, workId, flowId, retName, "");
                callsByName.Add((advName, advId));
                callsByName.Add((retName, retId));

                ModelBuilder.addArrowCall(store, workId, advId, retId, ArrowType.Start);
                arrows.Add(new ArrowSpec(advName, retName, ArrowKindStd.Start));
            }

            // Flow 내 StartReset chain
            for (int i = 0; i < workIdsInFlow.Count - 1; i++)
            {
                ModelBuilder.addArrowWork(store, mainSys, workIdsInFlow[i], workIdsInFlow[i + 1],
                                         ArrowType.StartReset);
                arrows.Add(new ArrowSpec($"{flowName}.W{i + 1}", $"{flowName}.W{i + 2}",
                                        ArrowKindStd.StartReset));
            }

            firstWorkByFlow.Add((flowName, workIdsInFlow[0]));
            lastWorkByFlow.Add((flowName, workIdsInFlow[^1]));
        }

        // Cross-flow chain: F1.last → F2.first → F2.last → F3.first → ...
        for (int f = 0; f < nFlows - 1; f++)
        {
            var src = lastWorkByFlow[f];
            var tgt = firstWorkByFlow[f + 1];
            ModelBuilder.addArrowWork(store, mainSys, src.workId, tgt.workId, ArrowType.Start);
            // Cross-flow arrow — 이름 형식 "{flow}.{work}"
            var srcWorkName = store.Works[src.workId].LocalName;
            var tgtWorkName = store.Works[tgt.workId].LocalName;
            arrows.Add(new ArrowSpec($"{src.flowName}.{srcWorkName}",
                                    $"{tgt.flowName}.{tgtWorkName}",
                                    ArrowKindStd.Start));
        }

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);
        return new GeneratedModel(store, arrows, "MultiFlow", opts,
                                 mainSys, firstFlowId, allWorkIds, callsByName, deviceDurations);
    }
}

/// <summary>Case D — Branch / Choice. A 발화 후 N branches 중 하나 (확률적) 선택.
/// 차종 변경, 모드 전환 등 모방.
/// </summary>
public static class BranchGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nBranches = Math.Clamp(opts.NBranches, 2, 4);

        var t = ModelBuilder.emptyStore("Branch", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Line");
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();
        var workIds = new List<Guid>();

        // 공통 W1 (분기 전)
        var w1 = ModelBuilder.addWork(store, flowId, "Line", "W1");
        workIds.Add(w1);
        var aAdv = ModelBuilder.addCallWithApi(store, w1, flowId, "A.ADV", "");
        var aRet = ModelBuilder.addCallWithApi(store, w1, flowId, "A.RET", "");
        callsByName.Add(("A.ADV", aAdv));
        callsByName.Add(("A.RET", aRet));
        ModelBuilder.addArrowCall(store, w1, aAdv, aRet, ArrowType.Start);
        arrows.Add(new ArrowSpec("A.ADV", "A.RET", ArrowKindStd.Start));

        // N 개의 branch (W2_B1, W2_B2, ...) — A.RET 후 하나 활성화
        for (int b = 0; b < nBranches; b++)
        {
            var branchName = $"W2B{b + 1}";
            var wb = ModelBuilder.addWork(store, flowId, "Line", branchName);
            workIds.Add(wb);
            var advName = $"B{b + 1}.ADV";
            var retName = $"B{b + 1}.RET";
            var advId = ModelBuilder.addCallWithApi(store, wb, flowId, advName, "");
            var retId = ModelBuilder.addCallWithApi(store, wb, flowId, retName, "");
            callsByName.Add((advName, advId));
            callsByName.Add((retName, retId));
            ModelBuilder.addArrowCall(store, wb, advId, retId, ArrowType.Start);
            arrows.Add(new ArrowSpec(advName, retName, ArrowKindStd.Start));

            // A.RET → Bk.ADV (Start) — workWorks
            ModelBuilder.addArrowWork(store, mainSys, w1, wb, ArrowType.Start);
            arrows.Add(new ArrowSpec($"Line.W1", $"Line.{branchName}", ArrowKindStd.Start));
        }

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);
        return new GeneratedModel(store, arrows, "Branch", opts,
                                 mainSys, flowId, workIds, callsByName, deviceDurations);
    }
}

/// <summary>Case E — Recycle Loop. Token 이 라인 끝에서 시작으로 재진입 (재작업).
/// 시뮬은 일정 확률로 token 이 마지막 work 후 첫 work 로 돌아감 → 인과 cycle.
/// </summary>
public static class RecycleLoopGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nStages = Math.Max(3, opts.RecycleStages);

        var t = ModelBuilder.emptyStore("RecycleLoop", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Line");
        var workIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();

        for (int i = 1; i <= nStages; i++)
        {
            var localName = $"W{i}";
            var workId = ModelBuilder.addWork(store, flowId, "Line", localName);
            workIds.Add(workId);

            var advName = $"S{i}.ADV";
            var retName = $"S{i}.RET";
            var advId = ModelBuilder.addCallWithApi(store, workId, flowId, advName, "");
            var retId = ModelBuilder.addCallWithApi(store, workId, flowId, retName, "");
            callsByName.Add((advName, advId));
            callsByName.Add((retName, retId));
            ModelBuilder.addArrowCall(store, workId, advId, retId, ArrowType.Start);
            arrows.Add(new ArrowSpec(advName, retName, ArrowKindStd.Start));
        }

        // Inline StartReset chain
        for (int i = 0; i < workIds.Count - 1; i++)
        {
            ModelBuilder.addArrowWork(store, mainSys, workIds[i], workIds[i + 1],
                                     ArrowType.StartReset);
            arrows.Add(new ArrowSpec($"Line.W{i + 1}", $"Line.W{i + 2}",
                                    ArrowKindStd.StartReset));
        }

        // Recycle arrow: 마지막 work → 첫 work (Start, 재진입)
        ModelBuilder.addArrowWork(store, mainSys, workIds[^1], workIds[0],
                                 ArrowType.Start);
        arrows.Add(new ArrowSpec($"Line.W{nStages}", "Line.W1", ArrowKindStd.Start));

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);
        return new GeneratedModel(store, arrows, "RecycleLoop", opts,
                                 mainSys, flowId, workIds, callsByName, deviceDurations);
    }
}

/// <summary>Case F — PLC-Realistic Cell. Robot + Conveyor + Jig + Sensors.
/// 표준 sequence: Conveyor.IN → Jig.CLAMP → Robot.WELD → Jig.UNCLAMP → Conveyor.OUT.
/// 각 device 별 다른 timing.
/// </summary>
public static class PlcCellGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();

        var t = ModelBuilder.emptyStore("PlcCell", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Cell");
        var workIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();

        // 5 stages: IN → CLAMP → WELD → UNCLAMP → OUT
        var stageDefs = new List<(string work, string device, string api)>
        {
            ("Loading", "Conveyor", "IN"),
            ("Clamping", "Jig", "CLAMP"),
            ("Welding", "Robot", "WELD"),
            ("Unclamping", "Jig", "UNCLAMP"),
            ("Unloading", "Conveyor", "OUT"),
        };

        var stageCalls = new List<Guid>();
        foreach (var (workName, device, api) in stageDefs)
        {
            var workId = ModelBuilder.addWork(store, flowId, "Cell", workName);
            workIds.Add(workId);
            var callName = $"{device}.{api}";
            var cid = ModelBuilder.addCallWithApi(store, workId, flowId, callName, "");
            callsByName.Add((callName, cid));
            stageCalls.Add(cid);
        }

        // Inline StartReset chain
        for (int i = 0; i < workIds.Count - 1; i++)
        {
            ModelBuilder.addArrowWork(store, mainSys, workIds[i], workIds[i + 1],
                                     ArrowType.StartReset);
            arrows.Add(new ArrowSpec(
                $"Cell.{stageDefs[i].work}",
                $"Cell.{stageDefs[i + 1].work}",
                ArrowKindStd.StartReset));
        }

        // Sensor calls — 각 stage 의 done 감지
        var sensorWork = ModelBuilder.addWork(store, flowId, "Cell", "Sensors");
        workIds.Add(sensorWork);
        for (int i = 0; i < stageDefs.Count; i++)
        {
            var sName = $"Sensor.{stageDefs[i].api}_DONE";
            var sId = ModelBuilder.addCallWithApi(store, sensorWork, flowId, sName, "");
            callsByName.Add((sName, sId));
        }

        // Device-specific durations: Conveyor 0.3s, Jig 0.2s, Robot 1.5s, Sensor 0.05s
        var deviceDurations = new Dictionary<string, long>
        {
            ["Conveyor"] = 300 + rng.Next(-50, 51),
            ["Jig"] = 200 + rng.Next(-30, 31),
            ["Robot"] = 1500 + rng.Next(-200, 201),
            ["Sensor"] = 50 + rng.Next(-10, 11),
        };
        return new GeneratedModel(store, arrows, "PlcCell", opts,
                                 mainSys, flowId, workIds, callsByName, deviceDurations);
    }
}

/// <summary>Case G — Capacity Variable. Cycle 마다 다른 token 수 (1~N).
/// 라인 idle / burst / 평상 구간 혼합.
/// </summary>
public static class CapacityVarGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var nStages = 4;   // fixed for capacity variant

        var t = ModelBuilder.emptyStore("CapacityVar", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Line");
        var workIds = new List<Guid>();
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();

        for (int i = 1; i <= nStages; i++)
        {
            var workId = ModelBuilder.addWork(store, flowId, "Line", $"W{i}");
            workIds.Add(workId);
            var advName = $"S{i}.ADV";
            var retName = $"S{i}.RET";
            var advId = ModelBuilder.addCallWithApi(store, workId, flowId, advName, "");
            var retId = ModelBuilder.addCallWithApi(store, workId, flowId, retName, "");
            callsByName.Add((advName, advId));
            callsByName.Add((retName, retId));
            ModelBuilder.addArrowCall(store, workId, advId, retId, ArrowType.Start);
            arrows.Add(new ArrowSpec(advName, retName, ArrowKindStd.Start));
        }

        for (int i = 0; i < workIds.Count - 1; i++)
        {
            ModelBuilder.addArrowWork(store, mainSys, workIds[i], workIds[i + 1],
                                     ArrowType.StartReset);
            arrows.Add(new ArrowSpec($"Line.W{i + 1}", $"Line.W{i + 2}",
                                    ArrowKindStd.StartReset));
        }

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);
        return new GeneratedModel(store, arrows, "CapacityVar", opts,
                                 mainSys, flowId, workIds, callsByName, deviceDurations);
    }
}

/// <summary>Case H — Adversarial Mix. 진짜 인과 + 의도된 spurious + noise + confounded.
/// Algorithm 의 false-positive resistance 검증.
/// </summary>
public static class AdversarialMixGenerator
{
    public static GeneratedModel Generate(GeneratorOptions opts)
    {
        var rng = opts.Seed is int s ? new Random(s) : new Random();
        var spuriousCount = Math.Max(1, opts.AdvSpuriousCount);

        var t = ModelBuilder.emptyStore("AdversarialMix", "Main");
        var store = t.Item1;
        var mainSys = t.Item3;

        var flowId = ModelBuilder.addFlow(store, mainSys, "Adv");
        var workId = ModelBuilder.addWork(store, flowId, "Adv", "Main");
        var workIds = new List<Guid> { workId };
        var callsByName = new List<(string, Guid)>();
        var arrows = new List<ArrowSpec>();

        // 진짜 인과 chain: A → B → C
        var ids = new Dictionary<string, Guid>();
        foreach (var n in new[] { "A.S", "B.S", "C.S" })
        {
            var cid = ModelBuilder.addCallWithApi(store, workId, flowId, n, "");
            callsByName.Add((n, cid));
            ids[n] = cid;
        }
        ModelBuilder.addArrowCall(store, workId, ids["A.S"], ids["B.S"], ArrowType.Start);
        arrows.Add(new ArrowSpec("A.S", "B.S", ArrowKindStd.Start));
        ModelBuilder.addArrowCall(store, workId, ids["B.S"], ids["C.S"], ArrowType.Start);
        arrows.Add(new ArrowSpec("B.S", "C.S", ArrowKindStd.Start));

        // Spurious calls (random fire timing — 인과 X)
        for (int i = 0; i < spuriousCount; i++)
        {
            var n = $"N{i + 1}.X";
            var cid = ModelBuilder.addCallWithApi(store, workId, flowId, n, "");
            callsByName.Add((n, cid));
            // 모델에는 spurious arrow 추가 (truth-set 에는 없지만 candidate 로 표시 위해)
        }

        var deviceDurations = InlineLineGenerator.BuildDeviceDurations(callsByName, rng);
        return new GeneratedModel(store, arrows, "AdversarialMix", opts,
                                 mainSys, flowId, workIds, callsByName, deviceDurations);
    }
}
