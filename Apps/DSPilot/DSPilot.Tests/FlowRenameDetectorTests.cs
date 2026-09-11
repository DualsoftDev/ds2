// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// flow 이름 변경 판정(dspFlow.flowId ↔ 모델 Flow GUID) 규약 고정(2026-09-11).
/// 배경: ReloadAndResync 가 리네임된 flow 를 "사라진 flow" 로 보고 dspFlow/dspCall/dspFlowHistory 를 삭제하던 경로를
/// 보존+승계로 바꿨다. 이 테스트가 고정하는 불변식:
/// ① GUID 같고 이름 다르면 리네임. ② 새 이름 행이 DB 에 이미 있으면 승계하지 않는다(이력 병합 방지, 경고만).
/// ③ flowId 가 없는 행(구 데이터)·이름이 같은 행은 판정 대상이 아니다.
/// </summary>
public class FlowRenameDetectorTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static FlowRenameDetector.DbFlow Row(string name, Guid? id) => new(name, id?.ToString("D"));

    [Fact]
    public void Detects_rename_when_guid_matches_and_name_differs()
    {
        var model = new[] { (A, "#121 LH"), (B, "#131") };
        var db = new[] { Row("#121", A), Row("#131", B) };

        var r = FlowRenameDetector.Detect(model, db);

        var one = Assert.Single(r.Renames);
        Assert.Equal("#121", one.OldName);
        Assert.Equal("#121 LH", one.NewName);
        Assert.Equal(A, one.Id);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Skips_rename_when_new_name_row_already_exists()
    {
        var model = new[] { (A, "X") };
        var db = new[] { Row("OLD", A), Row("X", null) };   // 동명 flow 가 이미 있음(구 데이터라 flowId 없음)

        var r = FlowRenameDetector.Detect(model, db);

        Assert.Empty(r.Renames);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Ignores_rows_without_id_and_unchanged_names()
    {
        var model = new[] { (A, "S"), (B, "T") };
        var db = new[] { Row("S", A), Row("Q", null), Row("T", B) };

        var r = FlowRenameDetector.Detect(model, db);

        Assert.Empty(r.Renames);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void No_ids_in_db_means_no_judgement()
    {
        var model = new[] { (A, "S") };
        var db = new[] { Row("OLD", null) };

        var r = FlowRenameDetector.Detect(model, db);

        Assert.Empty(r.Renames);
    }

    [Fact]
    public void Name_comparison_is_case_sensitive_like_sqlite_unique()
    {
        // SQLite 의 FlowName UNIQUE 는 BINARY 대조 — 대소문자만 다른 이름은 다른 행이므로 리네임으로 승계한다.
        var model = new[] { (A, "abc") };
        var db = new[] { Row("ABC", A) };

        var r = FlowRenameDetector.Detect(model, db);

        var one = Assert.Single(r.Renames);
        Assert.Equal("ABC", one.OldName);
        Assert.Equal("abc", one.NewName);
    }
}
