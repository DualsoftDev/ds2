/// Ds2.Reverse 핵심 타입.
/// PLC capture (log) + arrow 후보로부터 DsStore 모델을 인과 검증과 함께 재구성.
namespace Ds2.Reverse.Core

open System

/// 한 cycle 안 하나의 call event (Going edge).
type CapturedEvent = {
    /// 발화 시각 (ms, t=0 기준 상대값)
    T: int64
    /// 풀네임 — 예: "F1.D1.ADV"
    Name: string
}

/// 인과 후보 arrow (logic 출처).
type ArrowCandidate = {
    /// 소스 변수명 (suffix 형태)
    Src: string
    /// 타겟 변수명
    Tgt: string
    /// 선언된 kind ("trigger" | "group" | "reset" | "trigger_reset" | "mutex")
    DeclaredKind: string
}

/// 인과 점수 — sufficiency / necessity / lag 통계.
type CausationScore = {
    NA: int
    NB: int
    /// P[B fires within window after A]
    Sufficiency: float
    /// P[A precedes B in window]
    Necessity: float
    /// 평균 lag (ms, 음수면 B 가 A 보다 먼저)
    LagMean: float
    LagStd: float
    /// CV (coefficient of variation) — std/mean
    LagCv: float
    AbsLagMean: float
    /// |lag_mean| < parallel_lag_ms → 동시 발화 (group 후보)
    IsParallel: bool
    /// Sequential gate 통과 (positive lag + high suff/necc + stable)
    PassesSeq: bool
    /// Group gate 통과 (parallel + high suff/necc)
    PassesGrp: bool
    /// fail 원인 (디버깅용)
    Reason: string option
}

/// 인과 검출 threshold 설정.
type CausationConfig = {
    WindowMs: int64
    SufficiencyMin: float
    NecessityMin: float
    LagCvMax: float
    LagStdAbsMs: float
    MinFires: int
    ParallelLagMs: float
    /// Cycle period hint (ms) — scenario 가 알면 전달. None 이면 WindowMs 그대로 사용.
    /// 효과: effective_window = min(WindowMs, CycleHintMs * 0.7) — cross-cycle 매칭 차단.
    CycleHintMs: int64 option
}

module CausationConfig =
    let defaults = {
        WindowMs = 3000L
        SufficiencyMin = 0.85
        NecessityMin = 0.85
        LagCvMax = 0.30
        LagStdAbsMs = 150.0
        MinFires = 5
        ParallelLagMs = 50.0
        CycleHintMs = None
    }

    let withCycleHint (cycleMs: int64) (cfg: CausationConfig) =
        { cfg with CycleHintMs = Some cycleMs }

    /// B5.2 Dynamic Threshold — noise level 추정 후 cfg 자동 조정.
    /// noiseLevel: 0.0 (clean) ~ 1.0 (very noisy).
    /// 효과:
    ///   • noisy → suff/necc threshold 완화 (0.85 → 0.75 at 1.0)
    ///   • noisy → LagCvMax 완화 (0.30 → 0.45 at 1.0)
    ///   • clean → 더 strict (suff/necc 0.90 at 0.0)
    let withNoiseLevel (noiseLevel: float) (cfg: CausationConfig) =
        let clamped = max 0.0 (min 1.0 noiseLevel)
        // suff/necc 0.85 baseline. clean -> 0.90, noisy -> 0.75
        let suffMin = 0.85 + (0.5 - clamped) * 0.10   // 0 → 0.90, 1 → 0.80
        let neccMin = suffMin
        let cvMax = 0.30 + clamped * 0.15             // 0 → 0.30, 1 → 0.45
        { cfg with
            SufficiencyMin = max 0.70 (min 0.95 suffMin)
            NecessityMin = max 0.70 (min 0.95 neccMin)
            LagCvMax = cvMax }

/// 게이팅 결과 — 한 arrow 의 최종 분류.
/// EmitSequential 의 ArrowType code: 1=Start, 2=Reset, 3=StartReset, 4=ResetReset.
type GatingDecision =
    | EmitSequential of arrowTypeCode: int * score: CausationScore
    | EmitGroup of CausationScore
    | Dropped of reason: string * score: CausationScore

/// Confidence 등급 — soft classification.
type ConfidenceTier =
    | High      // >= 0.9   : 안전 emit
    | Medium    // 0.7~0.9  : emit + review flag
    | Low       // 0.5~0.7  : 보류 (사용자 검토)
    | Reject    // < 0.5    : drop

/// Arrow confidence — 인과 강도 + 등급 + 근거.
type ArrowConfidence = {
    /// 0~1 의 연속 confidence
    Score: float
    /// 등급화된 결과
    Tier: ConfidenceTier
    /// 신뢰 근거 (디버깅 / UI 표시용)
    Evidence: string list
    /// 표본 크기 기반 reliability multiplier (0.5~1.0)
    NReliability: float
}

/// Multi-source cluster causation 점수. 한 sink 가 여러 source 로부터 트리거 시,
/// 각 source 의 cluster (가장 가까운 preceding source 가 자기 자신인 B 들) 평가.
type ClusterScore = {
    SrcName: string
    /// 이 source 의 발화 수
    NA: int
    /// 전체 sink (B) 발화 수
    NB: int
    /// 이 source 가 책임지는 B 수 (closest preceding 매칭)
    ClusterSize: int
    /// ClusterSize / NA — source 발화당 cluster B 비율
    Suff: float
    /// ClusterSize / NB — sink 의 어느 비율이 이 source 의 cluster
    Coverage: float
    LagMean: float
    LagStd: float
    LagCv: float
    PassesSeq: bool
}

/// 검출 결과 보고 (gap report 의 일부).
/// 검출 파이프라인 동안 누적 업데이트되므로 mutable.
type DetectionReport = {
    mutable TotalCandidates: int
    mutable PassedSeq: int
    mutable PassedGrp: int
    mutable DroppedCausation: int
    mutable RemovedCycle: int
    mutable RemovedTransitive: int
    mutable RemovedGroupDup: int
    mutable FinalArrowCount: int
    DroppedDetail: ResizeArray<string * string * CausationScore * string>
    CycleWarn: ResizeArray<string * string>
    TransitiveLog: ResizeArray<string * string>
    GroupEmitted: ResizeArray<string * string>
    /// Arrow 별 confidence — 검출된 arrows 의 신뢰도 (srcName, tgtName, conf)
    EmittedConfidence: ResizeArray<string * string * ArrowConfidence>
    /// 추정된 noise level (0~1) — autoTune 이 사용한 값
    mutable NoiseLevel: float
    /// 이상 cycle 인덱스 — anomaly detection 활성 시
    AnomalousCycles: ResizeArray<int * float>
}

module DetectionReport =
    let empty () : DetectionReport = {
        TotalCandidates = 0
        PassedSeq = 0
        PassedGrp = 0
        DroppedCausation = 0
        RemovedCycle = 0
        RemovedTransitive = 0
        RemovedGroupDup = 0
        FinalArrowCount = 0
        DroppedDetail = ResizeArray()
        CycleWarn = ResizeArray()
        TransitiveLog = ResizeArray()
        GroupEmitted = ResizeArray()
        EmittedConfidence = ResizeArray()
        NoiseLevel = 0.0
        AnomalousCycles = ResizeArray()
    }
