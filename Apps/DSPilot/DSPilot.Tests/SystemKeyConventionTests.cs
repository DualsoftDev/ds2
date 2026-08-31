// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 멀티 PLC 복합키 (SystemId, 주소) 의 SystemId 표기 규약 고정.
/// 이 표기가 <c>plc.systemId</c> 컬럼과 어긋나면 예외 없이 조회가 0건이 되므로(조용한 실패),
/// 표기 자체를 테스트로 못박는다.
/// </summary>
public class SystemKeyConventionTests
{
    private static readonly Guid Sample = new("A1B2C3D4-1111-2222-3333-444455556666");

    // ── Key: 인메모리 키 표기 ──────────────────────────────────

    [Fact]
    public void Key는_소문자_D_포맷이다()
    {
        Assert.Equal("a1b2c3d4-1111-2222-3333-444455556666", SystemKeyConvention.Key(Sample));
    }

    [Fact]
    public void Key는_문자열_입력도_같은_표기로_정규화한다()
    {
        // 대문자·중괄호·"N" 포맷 모두 같은 키로 수렴해야 캐시/정의 인덱스가 어긋나지 않는다.
        var expected = SystemKeyConvention.Key(Sample);
        Assert.Equal(expected, SystemKeyConvention.Key(Sample.ToString("D").ToUpperInvariant()));
        Assert.Equal(expected, SystemKeyConvention.Key(Sample.ToString("B")));
        Assert.Equal(expected, SystemKeyConvention.Key(Sample.ToString("N")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public void Key는_귀속_미상을_빈_문자열로_돌려준다(string? systemId)
    {
        Assert.Equal(string.Empty, SystemKeyConvention.Key(systemId));
    }

    [Fact]
    public void Key는_null과_Empty_Guid를_모두_미상으로_본다()
    {
        Assert.Equal(string.Empty, SystemKeyConvention.Key((Guid?)null));
        Assert.Equal(string.Empty, SystemKeyConvention.Key(Guid.Empty));
    }

    // ── Scope: SQL 파라미터 표기 ───────────────────────────────

    [Fact]
    public void Scope는_지정시_Key와_같은_표기다()
    {
        Assert.Equal(SystemKeyConvention.Key(Sample), SystemKeyConvention.Scope(Sample));
    }

    [Fact]
    public void Scope는_미상을_null로_돌려준다()
    {
        // ★빈 문자열이면 (@SystemId IS NULL OR ...) 가 열리지 않아 systemId='' 행만 매칭 = 0건.
        Assert.Null(SystemKeyConvention.Scope(null));
        Assert.Null(SystemKeyConvention.Scope(Guid.Empty));
    }
}
