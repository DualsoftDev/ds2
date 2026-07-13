// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Heatmap;

/// <summary>
/// Call 동작편차의 로버스트(중앙값 기반) 통계 — 사람 개입·대기가 섞인 공정에서 평균/σ 가
/// 이봉(bimodal) 분포로 오염돼 CV 수백%로 부풀던 문제의 표시측 해법.
/// 평균/σ(라이브 Welford, dspCall 저장)는 그대로 두고(CCTV/Flow 소비처 파급 차단),
/// 이 통계는 매칭 완료된 실행 기록에서 사후 산출해 메모리 캐시로만 제공한다.
/// </summary>
/// <param name="MedianMs">실행시간 중앙값(ms) — 대기 표본에 끌려가지 않는 대표값.</param>
/// <param name="P10Ms">10 백분위(ms) — 정상범위 하한(실행의 80%가 P10~P90 안).</param>
/// <param name="P90Ms">90 백분위(ms) — 정상범위 상한.</param>
/// <param name="RobustCv">로버스트 변동계수 = (1.4826×MAD)/중앙값. MAD=0(표본 절반 이상 동일값)이면 IQR/1.349 폴백.</param>
/// <param name="RecentRobustCv">최근 창(마지막 200회)의 로버스트 CV. 표본 30회 미만이면 null(전체값으로 폴백) — "평소 대비 악화" 판정의 분자.</param>
/// <param name="DelayCount">지연 표본 수 = 실행시간 &gt; max(중앙값×3, 2000ms). 대기·개입 추정 건수(편차와 분리 표기).</param>
/// <param name="SampleCount">산출에 쓰인 매칭 실행 기록 수(라이브 GoingCount 와 다를 수 있음).</param>
public sealed record CallRobustStats(
    double MedianMs,
    double P10Ms,
    double P90Ms,
    double RobustCv,
    double? RecentRobustCv,
    int DelayCount,
    int SampleCount);
