using System;
using System.Collections.Generic;
using Ds2.Core.Store;

namespace Ds2.Reverse.Studio.Models;

public enum ModelCase { InlineLine, StandaloneDag, MultiFlow, Branch, RecycleLoop, PlcCell, CapacityVar, AdversarialMix }

public enum ArrowKindStd { Start, Group, Reset, StartReset, ResetReset }

public record ArrowSpec(string Src, string Tgt, ArrowKindStd Kind);

public record GeneratorOptions(
    ModelCase Case,
    int? Seed,
    // Case A
    int NStages,
    int Capacity,
    int LagMs,
    int JitterMs,
    // Case B
    int NCalls,
    double Density,
    double GroupProb,
    // Case C (MultiFlow)
    int NFlows = 3,
    int StagesPerFlow = 4,
    // Case D (Branch)
    int NBranches = 3,
    double BranchEntropy = 0.5,   // 0=uniform, 1=heavy bias on first branch
    // Case E (Recycle Loop)
    int RecycleStages = 5,
    double RecycleProbability = 0.15,
    // Case F (PLC Cell)
    bool PlcUseRobot = true,
    bool PlcUseConveyor = true,
    bool PlcUseJig = true,
    // Case G (Capacity Variable)
    int CapMinTokens = 1,
    int CapMaxTokens = 5,
    // Case H (Adversarial Mix)
    int AdvSpuriousCount = 3,
    double AdvNoiseLevel = 0.3
);

public record GeneratedModel(
    DsStore Store,
    IReadOnlyList<ArrowSpec> GroundTruth,
    string CaseName,
    GeneratorOptions Options,
    Guid MainSystemId,
    Guid MainFlowId,
    IReadOnlyList<Guid> WorkIds,
    IReadOnlyList<(string name, Guid id)> CallsByName,
    IReadOnlyDictionary<string, long> DeviceDurations);

public record CapturedEventRow(long T, long EndT, string Name);

public record ArrowDiff(string Src, string Tgt, string Type, string Status, string? Note,
    double? Confidence = null, string? Tier = null);

public record DetectionMetrics(
    int TruthCount,
    int DetectedCount,
    int TP, int FP, int FN,
    double Precision, double Recall, double F1,
    IReadOnlyList<ArrowDiff> Diffs,
    double NoiseLevel = 0.0,
    int AnomalousCyclesCount = 0,
    IReadOnlyList<(int CycleIdx, double Score)>? AnomalousCycles = null);

public record ReverseResult(
    DsStore DetectedStore,
    DetectionMetrics Metrics);
