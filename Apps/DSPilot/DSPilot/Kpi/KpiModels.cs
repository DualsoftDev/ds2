// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>사이클 완료 시 기록하는 한 행. 상태는 저장하지 않는다(조회 시 도출).</summary>
/// <param name="Flow">부모(물리 설비) flow 이름.</param>
/// <param name="Branch">분기 이름. 없으면 null — 분기별 R 의 키가 된다.</param>
/// <param name="StartMs">시작 head(epoch ms).</param>
/// <param name="EndMs">끝 head = 다음 사이클 시작(epoch ms).</param>
/// <param name="MtMs">tail 이 있을 때만. 진단 표시 전용.</param>
/// <param name="RUsedMs">판정 시점에 박제한 기준 R. 0 이면 기준 없음(표본 K 미달).</param>
/// <param name="WorstWork">work 지속시간 ÷ W 가 가장 컸던 work.</param>
/// <param name="WorstRatio">그 최댓값. 조회 시 κ_비가동 과 비교한다.</param>
public sealed record CycleRecord(
    string Flow,
    string? Branch,
    long StartMs,
    long EndMs,
    long? MtMs,
    double RUsedMs,
    string? WorstWork,
    double WorstRatio,
    ExcludeReason Exclude = ExcludeReason.None)
{
    public long CtMs => EndMs - StartMs;
    public long? WtMs => MtMs is long mt ? CtMs - mt : null;
}

/// <summary>사이클 안에서 잰 work 하나의 지속시간과, 그때 쓴 기준 W.</summary>
public sealed record WorkDuration(string Work, long DurationMs, double WUsedMs)
{
    /// <summary>W 대비 배율. W 가 없으면 0 — 비교 대상에서 빠진다.</summary>
    public double Ratio => WUsedMs > 0 ? DurationMs / WUsedMs : 0;
}

/// <summary>기준선 스냅샷 한 줄.</summary>
public sealed record BaselineRow(
    string Scope,
    string Flow,
    string Branch,
    string Work,
    string AsOfDate,
    double ValueMs,
    int SampleCount)
{
    public const string ScopeR = "R";
    public const string ScopeW = "W";
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
    string? WorstWork,
    double WorstRatio,
    ExcludeReason Exclude)
{
    public CycleFact ToFact() =>
        new(Id, StartMs, EndMs, CtMs, RUsedMs, WorstRatio, WorstWork, Exclude);
}
