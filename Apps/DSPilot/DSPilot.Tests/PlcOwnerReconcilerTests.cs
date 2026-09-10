// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using DSPilot.Services;
using Xunit;
using static DSPilot.Services.PlcOwnerReconciler;

namespace DSPilot.Tests;

/// <summary>
/// plc 행 귀속 (GUID, 이름) 이중 키 규약 고정.
/// <para>
/// 배경(2026-09-08 현장): AASX 에 flow 하나를 추가해 재업로드했는데 System GUID 가 전부 새로 발급됨(이름 동일).
/// GUID 단일 키라 새 plc 행+plcTag 가 생기고 옛 행의 이력이 systemId 필터에서 걸러져 "기록이 없어진" 것처럼 보였다.
/// 불변식: ① GUID 일치 = 같은 System ② GUID 없음 + 같은 이름 고아 유일 = 재키잉 ③ 모호/근거 없음 = 새 행(보고)
/// ④ GUID 행 + 같은 이름 고아 공존 = 분리 상태 보고만.
/// </para>
/// </summary>
public class PlcOwnerReconcilerTests
{
    private static readonly Guid Old = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid New = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private static string K(Guid g) => g.ToString("D").ToLowerInvariant();
    private static PlcRow Default() => new(1, null, "DSPilot");

    [Fact]
    public void Guid_match_wins_even_if_name_changed()
    {
        var report = Reconcile(
            new[] { new ModelSystem(Old, "새이름") },
            new[] { Default(), new PlcRow(2, K(Old), "옛이름") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Existing, d.Kind);
        Assert.Equal(2, d.PlcId);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void New_guid_same_name_single_orphan_is_rekeyed()
    {
        var report = Reconcile(
            new[] { new ModelSystem(New, "ub1_#121_#134") },
            new[] { Default(), new PlcRow(2, K(Old), "ub1_#121_#134") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Rekey, d.Kind);
        Assert.Equal(2, d.PlcId);
        Assert.Equal(K(Old), d.OldSystemKey);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Orphan_with_unique_suffix_name_still_matches_base_name()
    {
        // 과거 UNIQUE 회피로 "이름#guid8" 로 저장된 행도 같은 이름으로 본다.
        var report = Reconcile(
            new[] { new ModelSystem(New, "ub1_#135_ST") },
            new[] { Default(), new PlcRow(3, K(Old), "ub1_#135_ST#aaaaaaaa") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Rekey, d.Kind);
        Assert.Equal(3, d.PlcId);
    }

    [Fact]
    public void Live_row_of_another_model_system_is_never_an_orphan_candidate()
    {
        // Other 는 현재 모델에 살아 있는 다른 System — 이름이 같아도 빼앗지 않는다.
        var report = Reconcile(
            new[] { new ModelSystem(New, "동명"), new ModelSystem(Other, "동명") },
            new[] { Default(), new PlcRow(2, K(Other), "동명") });

        var dNew = report.Decisions.Single(d => d.SystemId == New);
        var dOther = report.Decisions.Single(d => d.SystemId == Other);
        Assert.Equal(DecisionKind.Create, dNew.Kind);
        Assert.Equal(DecisionKind.Existing, dOther.Kind);
        Assert.Equal(2, dOther.PlcId);
    }

    [Fact]
    public void Default_row_is_never_rekeyed()
    {
        var report = Reconcile(
            new[] { new ModelSystem(New, "DSPilot") },
            new[] { Default() });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Create, d.Kind);
    }

    [Fact]
    public void Two_same_name_orphans_are_ambiguous_and_reported()
    {
        var report = Reconcile(
            new[] { new ModelSystem(New, "S") },
            new[] { Default(), new PlcRow(2, K(Old), "S"), new PlcRow(3, K(Other), "S#cccccccc") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Create, d.Kind);
        Assert.Null(d.PlcId);
        Assert.Contains(report.Warnings, w => w.Contains("2개") && w.Contains("모호"));
    }

    [Fact]
    public void Different_name_orphan_is_not_evidence()
    {
        var report = Reconcile(
            new[] { new ModelSystem(New, "S") },
            new[] { Default(), new PlcRow(2, K(Old), "T") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Create, d.Kind);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Guid_row_plus_same_name_orphan_is_reported_as_split_not_merged()
    {
        // 현장 9/8 상태: 새 GUID 행(이름#guid8)과 옛 GUID 고아 행이 공존 — 자동 병합 금지, 보고만.
        var report = Reconcile(
            new[] { new ModelSystem(New, "ub1_#121_#134") },
            new[] { Default(), new PlcRow(2, K(Old), "ub1_#121_#134"), new PlcRow(5, K(New), "ub1_#121_#134#bbbbbbbb") });

        var d = Assert.Single(report.Decisions);
        Assert.Equal(DecisionKind.Existing, d.Kind);
        Assert.Equal(5, d.PlcId);
        var w = Assert.Single(report.Warnings);
        Assert.Contains("분리", w);
        Assert.Contains("id=2", w);
    }

    [Fact]
    public void One_orphan_is_claimed_by_at_most_one_system()
    {
        // 같은 이름의 모델 System 이 둘(비정상 모델)이고 고아가 하나면 첫 System 만 재키잉, 둘째는 새 행.
        var report = Reconcile(
            new[] { new ModelSystem(New, "S"), new ModelSystem(Other, "S") },
            new[] { Default(), new PlcRow(2, K(Old), "S") });

        Assert.Equal(1, report.Decisions.Count(d => d.Kind == DecisionKind.Rekey));
        Assert.Equal(1, report.Decisions.Count(d => d.Kind == DecisionKind.Create));
    }

    [Fact]
    public void Empty_guid_system_is_skipped()
    {
        var report = Reconcile(
            new[] { new ModelSystem(Guid.Empty, "S") },
            new[] { Default(), new PlcRow(2, K(Old), "S") });

        Assert.Empty(report.Decisions);
    }

    [Theory]
    [InlineData("ub1_#121_#134", "ub1_#121_#134")]
    [InlineData("ub1_#121_#134#0e51ec47", "ub1_#121_#134")]
    [InlineData("S#12345678", "S")]
    [InlineData("S#1234567", "S#1234567")]      // 7자리 — 접미 아님
    [InlineData("S#12345678z", "S#12345678z")]  // 9자리 — 접미 아님
    [InlineData("S#zzzzzzzz", "S#zzzzzzzz")]    // hex 아님
    [InlineData("#12345678", "#12345678")]      // 접두만 있는 이름은 그대로
    [InlineData("", "")]
    public void BaseName_strips_only_unique_suffix(string input, string expected)
    {
        Assert.Equal(expected, BaseName(input));
    }
}
