// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>사이클 완료 시 기록하는 한 행. 상태는 저장하지 않는다(조회 시 도출).</summary>
/// <param name="Flow">부모(물리 설비) flow 이름.</param>
/// <param name="Branch">분기 이름. 없으면 null — 분기별 R·W·MT중앙 의 키가 된다.</param>
/// <param name="StartMs">시작 경계(epoch ms).</param>
/// <param name="EndMs">끝 경계 = 다음 사이클 시작(epoch ms).</param>
/// <param name="MtMs">경계→마지막 work 끝. work 가 하나도 안 잡히면 null. 비가동 판정 인자(doc/30 §2.4).</param>
/// <param name="RUsedMs">판정 시점에 박제한 기준 R. 0 이면 기준 없음(표본 K 미달).</param>
/// <param name="MtMedianUsedMs">판정 시점에 박제한 MT중앙. 0 이면 MT 축 비교 대상 아님.</param>
/// <param name="WorstWork">게이트 통과 work 중 지속시간 ÷ W 가 가장 컸던 work.</param>
/// <param name="WorstRatio">그 최댓값. 조회 시 κ_work 와 비교한다.</param>
/// <param name="OverflowMs">call 구간이 CT 끝을 넘은 최대량. 조회 시 허용치와 비교한다.</param>
public sealed record CycleRecord(
    string Flow,
    string? Branch,
    long StartMs,
    long EndMs,
    long? MtMs,
    double RUsedMs,
    double MtMedianUsedMs,
    string? WorstWork,
    double WorstRatio,
    long OverflowMs = 0,
    ExcludeReason Exclude = ExcludeReason.None)
{
    public long CtMs => EndMs - StartMs;
    public long? WtMs => MtMs is long mt ? CtMs - mt : null;
}

/// <summary>사이클 안에서 잰 work 하나의 지속시간과, 그때 쓴 기준 W. 게이트에 걸렸으면 표시만 하고 판정엔 안 쓴다.</summary>
public sealed record WorkDuration(string Work, long DurationMs, double WUsedMs, bool Gated = false)
{
    /// <summary>W 대비 배율. W 가 없으면 0 — 비교 대상에서 빠진다.</summary>
    public double Ratio => WUsedMs > 0 ? DurationMs / WUsedMs : 0;
}

/// <summary>work 하나의 현재 기준선 — 중앙값과 게이트 근거 사분위.</summary>
public readonly record struct WorkBaseline(double MedianMs, double Q1Ms, double Q3Ms, int SampleCount);

/// <summary>기준선 스냅샷 한 줄. scope R·MT 는 work='' , W 는 work 별. q1·q3 는 W 에만 있다.</summary>
public sealed record BaselineRow(
    string Scope,
    string Flow,
    string Branch,
    string Work,
    string AsOfDate,
    double ValueMs,
    int SampleCount,
    double? Q1Ms = null,
    double? Q3Ms = null)
{
    public const string ScopeR = "R";
    public const string ScopeW = "W";
    public const string ScopeMt = "MT";
}

/// <summary>시스템(PLC 연결) 관측 이벤트. 계산 인자가 아니라 대조용이다.</summary>
/// <param name="Kind">link = 접속 전이 · gap = 심박 공백 · boot = DSPilot 기동.</param>
public sealed record LinkEventRecord(
    string System,
    long AtMs,
    long? EndMs,
    bool IsConnected,
    string Kind,
    string? Detail,
    string? Source)
{
    public const string KindLink = "link";
    public const string KindGap = "gap";
    public const string KindBoot = "boot";
}

/// <summary>조회에서 돌아오는 사이클 행 — 판정에 필요한 사실 + 표시용 부가 정보.</summary>
public sealed record CycleRow(
    long Id,
    string Flow,
    string? Branch,
    long StartMs,
    long EndMs,
    long CtMs,
    long? MtMs,
    long? WtMs,
    double RUsedMs,
    double MtMedianUsedMs,
    string? WorstWork,
    double WorstRatio,
    long OverflowMs,
    ExcludeReason Exclude)
{
    public CycleFact ToFact() =>
        new(Id, StartMs, EndMs, CtMs, RUsedMs, MtMs, MtMedianUsedMs, WorstRatio, WorstWork, OverflowMs, Exclude);
}
