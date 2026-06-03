/// Cycle 시뮬레이션 — 인과 모델 따라 events 생성.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

module Simulator =

    /// VLINE 시나리오의 한 사이클 발화 패턴.
    /// (offset_ms, name) — t0 기준 상대 시각.
    type CyclePattern = {
        Offsets: (int64 * string) list
        Jitter: int64
    }

    /// VLINE 기본 시나리오: 인과 chain + parallel + confounded + spurious.
    let vlinePattern (rng: Random) : CyclePattern =
        let parallelT = 510L
        let parallelB = parallelT + int64 (rng.Next(-5, 6))
        // confounded: S1.DONE 후 lag 가 100~1200ms 사이 균등 → CV 매우 큼
        let confoundedOffset = 500L + int64 (rng.Next(100, 1200))
        let spuriousT = int64 (rng.Next(0, 4900))
        { Offsets = [
            0L,    "F1.PRE.START"
            100L,  "F1.PRE.DONE"
            200L,  "F1.S1.START"
            500L,  "F1.S1.DONE"
            parallelT, "F1.S2A.START"
            parallelB, "F1.S2B.START"
            900L,  "F1.S2A.DONE"
            1000L, "F1.S2B.DONE"
            1100L, "F1.S3.START"
            1500L, "F1.S3.DONE"
            confoundedOffset, "F1.Y1.SHADOW"
            spuriousT, "F1.X1.PING"
          ]
          Jitter = 30L }

    /// N cycle 시뮬 → CapturedEvent list.
    let simulate (seed: int) (cycleMs: int64) (nCycles: int) (patternBuilder: Random -> CyclePattern) : CapturedEvent list =
        let rng = Random(seed)
        let events = ResizeArray<CapturedEvent>()
        for i in 0 .. nCycles - 1 do
            let t0 = int64 i * cycleMs
            let pattern = patternBuilder rng
            for (off, name) in pattern.Offsets do
                let jit = int64 (rng.Next(int -pattern.Jitter, int pattern.Jitter + 1))
                events.Add { T = t0 + off + jit; Name = name }
        events
        |> Seq.sortBy (fun e -> e.T)
        |> List.ofSeq

    /// N cycle 시뮬 with cycle-index-aware pattern (drift 등 stateful 시나리오용).
    /// 호출마다 fresh state 보장 — 같은 시나리오를 multi-seed 로 돌릴 때 누적 없음.
    let simulateCycleAware (seed: int) (cycleMs: int64) (nCycles: int)
                          (patternBuilder: int -> Random -> CyclePattern) : CapturedEvent list =
        let rng = Random(seed)
        let events = ResizeArray<CapturedEvent>()
        for i in 0 .. nCycles - 1 do
            let t0 = int64 i * cycleMs
            let pattern = patternBuilder i rng
            for (off, name) in pattern.Offsets do
                let jit = int64 (rng.Next(int -pattern.Jitter, int pattern.Jitter + 1))
                events.Add { T = t0 + off + jit; Name = name }
        events
        |> Seq.sortBy (fun e -> e.T)
        |> List.ofSeq
