// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Generic;
using System.Linq;
using DSPilot.Models;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// '현재 AASX 에 없는 flow(유령 설비)' 판정·정리의 안전장치 고정.
/// <para>
/// 배경: dspFlow/dspCall/dspFlowHistory/oeeDowntimeEvent 는 부팅 시 UPSERT 누적이라 모델을 교체하면
/// 예전 설비가 남는다(부팅 경로에 prune 이 없고, prune 은 실행 중 AASX 변경/수동 동기화에서만 돈다).
/// 표시는 읽기 필터(가역)로, 삭제는 사용자 명시 실행(비가역)으로 분리했다.
/// </para>
/// <para>
/// 여기서 고정하는 두 불변식이 깨지면 사고 방향이 정반대다 —
/// ① 판정 근거가 없을 때(모델 미로드) 필터가 <b>열려야</b> 한다. 닫히면 정상 데이터가 전부 숨는다.
/// ② retain 집합이 비었을 때 정리가 <b>no-op 이어야</b> 한다. 안 막으면 전량 삭제가 된다.
/// </para>
/// </summary>
public class ModelFlowFilterTests
{
    // ── 1. 내부 flow 이름 판정(SSOT) ────────────────────────────────────
    // Promaker 가 만드는 '*_Flow' 는 생산 설비가 아니라 모델 내부 배선 — 어떤 목록에도 안 나와야 한다.
    // 부트스트랩/resync 가 각자 EndsWith 로 중복 구현하던 규칙을 한 곳으로 모은 것이라, 대소문자
    // 무시까지 같아야 두 경로의 flow 집합이 어긋나지 않는다.

    [Theory]
    [InlineData("F1_Flow", true)]
    [InlineData("투입_Flow", true)]
    [InlineData("F1_flow", true)]      // 대소문자 무시
    [InlineData("F1_FLOW", true)]
    [InlineData("투입", false)]
    [InlineData("F1", false)]
    [InlineData("Flow_F1", false)]     // 접미사만 — 접두사는 정상 설비
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsInternalFlowName_matches_suffix_case_insensitively(string? name, bool expected)
        => Assert.Equal(expected, DsProjectService.IsInternalFlowName(name));

    // ── 2. FlowCycle override 정리 ──────────────────────────────────────
    // override 는 설정 파일(appsettings.Production.json)에 있어 DB 초기화로도 안 지워지고, 이름 기준
    // 정리 경로가 없어 모델을 교체할수록 누적된다(실측: F1~F6 + Invertor + TESTFLOW/TESTMAX0/TESTSTOP).
    // 수동 지정 CT 는 DB 없이도 임계 맵에 강제 주입되므로, 남으면 유령이 OEE 설비축에서 안 사라진다.

    private static List<FlowCycleOverride> Overrides(params string[] flowNames) =>
        [.. flowNames.Select(f => new FlowCycleOverride { FlowName = f, IdealCycleTimeMs = 40000 })];

    /// <summary>판정부(순수 함수) 직접 호출 — countOnly=true 는 '지우지 않고 세기'와 같다.</summary>
    private static List<FlowCycleOverride> Stale(List<FlowCycleOverride> ov, params string[] retain)
        => AppSettingsService.SelectStaleFlowCycleOverrides(ov, retain);

    [Fact]
    public void SelectStale_returns_only_flows_absent_from_model()
    {
        var ov = Overrides("F1", "F2", "투입", "이송");
        var stale = Stale(ov, "투입", "이송");

        Assert.Equal("F1, F2", string.Join(", ", stale.Select(o => o.FlowName).OrderBy(x => x)));
    }

    [Fact]
    public void SelectStale_is_case_insensitive_on_retain()
    {
        // dspFlow / AASX / 설정 파일이 서로 다른 대소문자로 같은 설비를 가리키는 경우가 있어,
        // 대소문자만 다른 이름을 유령으로 오판해 사용자 설정을 지우면 안 된다.
        Assert.Empty(Stale(Overrides("투입", "Conveyor1"), "투입", "CONVEYOR1"));
    }

    [Fact]
    public void SelectStale_with_empty_retain_is_noop()
    {
        // ★ 전량 삭제 방지 — retain 이 비는 건 "모델에 설비가 없다"가 아니라 "판정 근거가 없다"는 뜻이다.
        Assert.Empty(Stale(Overrides("F1", "투입")));
    }

    [Fact]
    public void SelectStale_does_not_mutate_input()
    {
        // 미리보기(countOnly) 경로가 이 함수만 호출하므로, 여기서 원본을 건드리면 조회가 삭제가 된다.
        var ov = Overrides("F1", "F2", "투입");
        Assert.Equal(2, Stale(ov, "투입").Count);
        Assert.Equal(3, ov.Count);
    }
}
