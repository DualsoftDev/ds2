// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using DSPilot.Models;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 분기 정의·경계 override 의 Call 참조(GUID+이름 이중 키) 재해석 규약 고정.
/// <para>
/// 배경(2026-09-07 현장): 사용자가 Call 이름만 바꿔 AASX 를 재업로드 → 이름 스냅샷 48종이 유령이 되어
/// 9 flow 전부 분기 저장이 거절되고 일부 flow 재계산이 건너뛰어졌다. 이 테스트가 고정하는 불변식:
/// ① GUID 가 같으면 이름이 바뀌어도 같은 call — 스냅샷을 새 이름으로 갱신한다.
/// ② GUID 가 없거나(구 데이터) 모델에 없어도 이름이 정확히 있으면 그 call — GUID 를 채운다.
/// ③ 둘 다 없으면 유령 — 지우지 않고 보고만 한다(사용자 결정).
/// </para>
/// </summary>
public class CallRefReconcilerTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static FlowCallLookup Model(params (Guid, string)[] calls) => FlowCallLookup.From(calls);

    // ── ① 리네임 추종 ──────────────────────────────────────────────
    [Fact]
    public void Rename_followed_by_guid_for_head_tail_and_excluded()
    {
        var set = new FlowBranchSet
        {
            FlowName = "#132",
            Branches =
            [
                new FlowBranchDef
                {
                    Name = "O100",
                    StartCallName = "[H] A.up", StartCallId = A.ToString("D"),
                    EndCallName = "[T] B.down", EndCallId = B.ToString("D"),
                    ExcludedCallNames = ["[H] C.up"], ExcludedCallIds = [C.ToString("D")],
                },
            ],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal("A.up", b.StartCallName);
        Assert.Equal("B.down", b.EndCallName);
        Assert.Equal(["C.up"], b.ExcludedCallNames);
        Assert.Equal([C.ToString("D")], b.ExcludedCallIds);
        Assert.True(report.Changed);
        Assert.Equal(3, report.Renamed.Count);
        Assert.Empty(report.Ghosts);
        Assert.Contains(report.Renamed, r => r.Role == CallRefRole.BranchExcluded && r.OldName == "[H] C.up" && r.NewName == "C.up");
    }

    // ── ② 구 데이터(GUID 없음) → 이름으로 GUID 채움 ─────────────────
    [Fact]
    public void Legacy_without_ids_gets_ids_filled_by_name_without_renaming()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches =
            [
                new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["C.up"] },
            ],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal(A.ToString("D"), b.StartCallId);
        Assert.Equal(B.ToString("D"), b.EndCallId);
        Assert.Equal([C.ToString("D")], b.ExcludedCallIds);
        Assert.Equal("A.up", b.StartCallName);          // 이름은 그대로
        Assert.Empty(report.Renamed);
        Assert.Equal(3, report.FilledIds);
        Assert.True(report.Changed);
    }

    // 재생성/프로젝트 간 복사 = GUID 는 바뀌고 이름은 같음 → 새 GUID 로 갈아탄다(이름 폴백).
    [Fact]
    public void Recreated_call_with_new_guid_is_rebound_by_name()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", StartCallId = A.ToString("D"), EndCallName = "B.down", EndCallId = B.ToString("D") }],
        };
        var newA = Guid.NewGuid();
        var lookup = Model((newA, "A.up"), (B, "B.down"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        Assert.Equal(newA.ToString("D"), set.Branches[0].StartCallId);
        Assert.Equal(1, report.FilledIds);
        Assert.Empty(report.Ghosts);
    }

    // ── ③ 유령 = 보고만, 값 불변 ────────────────────────────────────
    [Fact]
    public void Ghost_is_reported_and_kept_not_deleted()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["GONE.up", "C.up"] }],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal(["GONE.up", "C.up"], b.ExcludedCallNames);        // 유령 유지
        Assert.Equal([null, C.ToString("D")], b.ExcludedCallIds);      // 병렬 리스트 길이 유지, 유령은 null
        Assert.Single(report.Ghosts);
        Assert.Equal(CallRefRole.BranchExcluded, report.Ghosts[0].Role);
        Assert.Equal("GONE.up", report.Ghosts[0].Name);
    }

    [Fact]
    public void Unchanged_definition_reports_no_change()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", StartCallId = A.ToString("D"), EndCallName = "B.down", EndCallId = B.ToString("D"), ExcludedCallNames = ["C.up"], ExcludedCallIds = [C.ToString("D")] }],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        Assert.False(report.Changed);
        Assert.Empty(report.Ghosts);
    }

    // 병렬 리스트 길이 불일치(외부 편집) → GUID 를 버리고 이름으로 재해석, 길이 복원.
    [Fact]
    public void Mismatched_excluded_ids_length_falls_back_to_names()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["C.up", "A.up"], ExcludedCallIds = ["bogus"] }],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal(2, b.ExcludedCallIds!.Count);
        Assert.Equal([C.ToString("D"), A.ToString("D")], b.ExcludedCallIds);
    }

    // 리네임 추종으로 옛 이름·새 이름이 공존하면 하나로 접는다(반증 규칙 의미는 동일).
    [Fact]
    public void Rename_collision_in_excluded_is_deduplicated()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["[H] C.up", "C.up"], ExcludedCallIds = [C.ToString("D"), null] }],
        };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        Assert.Equal(["C.up"], set.Branches[0].ExcludedCallNames);
        Assert.Equal([C.ToString("D")], set.Branches[0].ExcludedCallIds);
    }

    // ── flow 경계 override ─────────────────────────────────────────
    [Fact]
    public void Override_head_tail_follow_guid_and_report_ghost()
    {
        var ov = new FlowCycleOverride { FlowName = "F", StartCallName = "[H] A.up", StartCallId = A.ToString("D"), EndCallName = "GONE.down" };
        var lookup = Model((A, "A.up"), (B, "B.down"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileOverride(ov, lookup, report);

        Assert.Equal("A.up", ov.StartCallName);
        Assert.Equal("GONE.down", ov.EndCallName);
        Assert.Single(report.Renamed);
        Assert.Single(report.Ghosts);
        Assert.Equal(CallRefRole.OverrideEnd, report.Ghosts[0].Role);
    }

    // ── 저장 경로 / 응답용 보조 ─────────────────────────────────────
    [Fact]
    public void StampIds_fills_from_names_and_leaves_unknown_null()
    {
        var def = new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["C.up", "GONE"] };
        var lookup = Model((A, "A.up"), (B, "B.down"), (C, "C.up"));

        CallRefReconciler.StampIds(def, lookup);

        Assert.Equal(A.ToString("D"), def.StartCallId);
        Assert.Equal(B.ToString("D"), def.EndCallId);
        Assert.Equal([C.ToString("D"), null], def.ExcludedCallIds);
    }

    [Fact]
    public void UnknownNames_lists_head_tail_excluded_ghosts_once()
    {
        var def = new FlowBranchDef { Name = "X", StartCallName = "GONE.up", EndCallName = "B.down", ExcludedCallNames = ["GONE.up", "C.up", "OTHER"] };
        var lookup = Model((B, "B.down"), (C, "C.up"));

        var unknown = CallRefReconciler.UnknownNames(def, lookup);

        Assert.Equal(["GONE.up", "OTHER"], unknown);
    }

    [Fact]
    public void Lookup_name_match_is_case_insensitive_and_trimmed()
    {
        var lookup = Model((A, "A.up"));
        Assert.True(lookup.TryGetId(" a.UP ", out var id));
        Assert.Equal(A, id);
        Assert.False(lookup.TryGetName("not-a-guid", out _));
        Assert.True(lookup.TryGetName(A.ToString("D").ToUpperInvariant(), out var name));
        Assert.Equal("A.up", name);
    }

    // ── ④ 배선 지문(2026-09-11) — 이름·GUID 둘 다 바뀌어도 주소가 같으면 같은 call ──────────
    private static FlowCallLookup ModelSig(params (Guid, string, string?)[] calls) => FlowCallLookup.From(calls);

    [Fact]
    public void Signature_build_is_order_independent_and_null_when_unwired()
    {
        var a = CallSignature.Build([("%IW1.0", "%QW2.0"), ("%IW1.1", null)]);
        var b = CallSignature.Build([(null, "%QW2.0"), ("%IW1.1", null), ("%IW1.0", null)]);
        Assert.Equal(a, b);
        Assert.Equal("I:%IW1.0;I:%IW1.1;O:%QW2.0", a);
        Assert.Null(CallSignature.Build([(null, null), ("", " ")]));
    }

    [Fact]
    public void Rename_with_guid_reissue_is_rematched_by_signature()
    {
        var newId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches =
            [
                new FlowBranchDef
                {
                    Name = "X",
                    StartCallName = "OLD.up", StartCallId = A.ToString("D"), StartCallSig = "I:%IW1.0;O:%QW2.0",
                    EndCallName = "B.down", EndCallId = B.ToString("D"), ExcludedCallNames = [],
                },
            ],
        };
        var lookup = ModelSig((newId, "NEW.up", "I:%IW1.0;O:%QW2.0"), (B, "B.down", "O:%QW9.9"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal("NEW.up", b.StartCallName);
        Assert.Equal(newId.ToString("D"), b.StartCallId);
        Assert.Single(report.Rematched);
        Assert.Equal("OLD.up", report.Rematched[0].OldName);
        Assert.Equal("NEW.up", report.Rematched[0].NewName);
        Assert.Empty(report.Ghosts);
        Assert.True(report.Changed);
    }

    [Fact]
    public void Ambiguous_signature_stays_ghost()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches =
            [
                new FlowBranchDef
                {
                    Name = "X", StartCallName = "GONE.up", StartCallSig = "O:%QW1.0",
                    EndCallName = "B.down", EndCallId = B.ToString("D"), ExcludedCallNames = [],
                },
            ],
        };
        // 공용 주소 — 두 call 이 같은 지문. 자동 확정하면 오분류가 조용히 생기므로 유령으로 남긴다.
        var lookup = ModelSig((A, "A.up", "O:%QW1.0"), (C, "C.up", "O:%QW1.0"), (B, "B.down", null));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        Assert.Equal("GONE.up", set.Branches[0].StartCallName);
        Assert.Single(report.Ghosts);
        Assert.Empty(report.Rematched);
        Assert.Equal(2, lookup.SigCandidates("O:%QW1.0"));
    }

    [Fact]
    public void Signature_is_backfilled_and_follows_rewiring()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches =
            [
                new FlowBranchDef
                {
                    Name = "X",
                    StartCallName = "A.up", StartCallId = A.ToString("D"),
                    EndCallName = "B.down", EndCallId = B.ToString("D"), EndCallSig = "O:%QW0.0",
                    ExcludedCallNames = ["C.up"], ExcludedCallIds = [C.ToString("D")],
                },
            ],
        };
        var lookup = ModelSig((A, "A.up", "O:%QW1.0"), (B, "B.down", "O:%QW2.0"), (C, "C.up", "O:%QW3.0"));
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        var b = set.Branches[0];
        Assert.Equal("O:%QW1.0", b.StartCallSig);      // 백필
        Assert.Equal("O:%QW2.0", b.EndCallSig);        // 배선 변경 추종(이름·GUID 는 그대로)
        Assert.Equal(["O:%QW3.0"], b.ExcludedCallSigs);
        Assert.Equal(2, report.FilledSigs);
        Assert.Equal(1, report.Rewired);
        Assert.Equal(0, report.FilledIds);
        Assert.True(report.Changed);
        Assert.Empty(report.Ghosts);
    }

    [Fact]
    public void Unwired_model_keeps_existing_signature()
    {
        var set = new FlowBranchSet
        {
            FlowName = "F",
            Branches = [new FlowBranchDef { Name = "X", StartCallName = "A.up", StartCallId = A.ToString("D"), StartCallSig = "O:%QW1.0", EndCallName = "B.down", EndCallId = B.ToString("D"), ExcludedCallNames = [] }],
        };
        var lookup = ModelSig((A, "A.up", null), (B, "B.down", null));   // 미결선 모델
        var report = new CallRefReconcileReport();

        CallRefReconciler.ReconcileBranchSet(set, lookup, report);

        Assert.Equal("O:%QW1.0", set.Branches[0].StartCallSig);
        Assert.False(report.Changed);
    }

    [Fact]
    public void StampIds_also_stamps_signatures_and_inherits_keys_for_unresolved()
    {
        var def = new FlowBranchDef { Name = "X", StartCallName = "A.up", EndCallName = "B.down", ExcludedCallNames = ["C.up", "GONE"] };
        var lookup = ModelSig((A, "A.up", "O:%QW1.0"), (B, "B.down", null), (C, "C.up", "O:%QW3.0"));

        CallRefReconciler.StampIds(def, lookup);

        Assert.Equal("O:%QW1.0", def.StartCallSig);
        Assert.Null(def.EndCallSig);
        Assert.Equal(["O:%QW3.0", null], def.ExcludedCallSigs);
        Assert.Equal([C.ToString("D"), null], def.ExcludedCallIds);

        // 이전 저장분이 'GONE' 을 다른 분기에서 GUID·지문과 함께 들고 있었다 → 격리 보존 시 그 키를 승계.
        var gone = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var existing = new FlowBranchSet
        {
            FlowName = "F",
            Branches =
            [
                new FlowBranchDef
                {
                    Name = "Y", StartCallName = "GONE", StartCallId = gone.ToString("D"), StartCallSig = "O:%QW7.7",
                    EndCallName = "B.down", ExcludedCallNames = [],
                },
            ],
        };
        CallRefReconciler.InheritUnresolved(def, existing);

        Assert.Equal(gone.ToString("D"), def.ExcludedCallIds![1]);
        Assert.Equal("O:%QW7.7", def.ExcludedCallSigs![1]);
        Assert.Equal(C.ToString("D"), def.ExcludedCallIds[0]);   // 해석된 것은 그대로
        Assert.Equal("O:%QW3.0", def.ExcludedCallSigs[0]);
    }

    [Fact]
    public void InheritUnresolved_without_existing_set_is_noop()
    {
        var def = new FlowBranchDef { Name = "X", StartCallName = "GONE", EndCallName = "B.down", ExcludedCallNames = ["Z"], ExcludedCallIds = [null], ExcludedCallSigs = [null] };
        CallRefReconciler.InheritUnresolved(def, null);
        Assert.Null(def.StartCallId);
        Assert.Equal([null], def.ExcludedCallIds);
    }
}
