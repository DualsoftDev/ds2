// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Cctv;
using DSPilot.Services;
using SkiaSharp;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// CctvOverlayRenderer(스냅샷 오버레이 합성) 검증.
/// 픽셀 단위 시각 검증은 하지 않는다(폰트/AA 환경 의존) — 산출물이 유효 JPEG 인지, 크기 계약(원본 유지/
/// 다운스케일만), 한글 라벨·경계 좌표에서 예외 없이 그리는지를 본다.
/// </summary>
public class CctvOverlayRendererTests
{
    /// <summary>합성 입력용 단색 테스트 프레임(JPEG).</summary>
    private static byte[] MakeFrame(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(new SKColor(20, 30, 40));
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static (int w, int h) DecodeSize(byte[] jpeg)
    {
        using var bmp = SKBitmap.Decode(jpeg);
        Assert.NotNull(bmp);
        return (bmp!.Width, bmp.Height);
    }

    private static CctvOverlay Zone(double x, double y, double w, double h, string? label = null) => new()
    {
        Id = $"z_{x}_{y}", CameraName = "cam", FlowId = Guid.NewGuid(), FlowName = "가공",
        X = x, Y = y, W = w, H = h, Label = label,
    };

    private static CctvOverlay Pin(double x, double y, string? label = null) => new()
    {
        Id = $"p_{x}_{y}", CameraName = "cam", CallId = Guid.NewGuid(), CallName = "CYL1.ADV",
        X = x, Y = y, W = 0.01, H = 0.01, Label = label,
    };

    [Fact]
    public void Render_존과핀_유효JPEG_원본크기유지()
    {
        var frame = MakeFrame(640, 360);
        var items = new List<CctvOverlayRenderer.Item>
        {
            new(Zone(0.1, 0.1, 0.3, 0.25, "투입 구역"), "Going"),
            new(Pin(0.7, 0.5, "실린더 전진"), "Error"),
            new(Pin(0.4, 0.8), "Ready"),      // 라벨 없음 → CallName 폴백
            new(Zone(0.5, 0.6, 0.2, 0.2), ""), // 상태 미상 → unknown 색
        };

        var outBytes = CctvOverlayRenderer.Render(frame, items);

        Assert.NotEmpty(outBytes);
        Assert.Equal((640, 360), DecodeSize(outBytes));
        // JPEG SOI 매직
        Assert.Equal(0xFF, outBytes[0]);
        Assert.Equal(0xD8, outBytes[1]);
    }

    [Fact]
    public void Render_width지정_비율유지_다운스케일()
    {
        var frame = MakeFrame(1920, 1080);
        var outBytes = CctvOverlayRenderer.Render(frame, [], targetWidth: 640);
        Assert.Equal((640, 360), DecodeSize(outBytes));
    }

    [Fact]
    public void Render_width가원본이상_업스케일안함()
    {
        var frame = MakeFrame(640, 360);
        var outBytes = CctvOverlayRenderer.Render(frame, [], targetWidth: 4000);
        Assert.Equal((640, 360), DecodeSize(outBytes));
    }

    [Fact]
    public void Render_경계좌표_상단칩아래로_예외없음()
    {
        var frame = MakeFrame(640, 360);
        var items = new List<CctvOverlayRenderer.Item>
        {
            new(Zone(0.0, 0.0, 0.2, 0.1, "상단 모서리"), "Finish"), // 칩이 위로 못 나감 → 아래(tagBelow)
            new(Zone(0.85, 0.9, 0.3, 0.3, "프레임 밖으로 넘침"), "Going"),
            new(Pin(0.0, 0.0, "좌상단 핀"), "Going"),
            new(Pin(1.0, 1.0, "우하단 핀"), "Error"),
        };
        var outBytes = CctvOverlayRenderer.Render(frame, items);
        Assert.Equal((640, 360), DecodeSize(outBytes));
    }

    [Fact]
    public void Render_긴한글라벨_말줄임_예외없음()
    {
        var frame = MakeFrame(320, 180); // 작은 프레임 → maxWidth 좁음 → Ellipsize 경로 강제
        var items = new List<CctvOverlayRenderer.Item>
        {
            new(Zone(0.1, 0.3, 0.4, 0.3, "아주아주아주아주 긴 한글 설비 라벨 이름입니다"), "Going"),
            new(Pin(0.6, 0.4, "이것도 몹시 긴 핀 라벨 텍스트"), "Ready"),
        };
        var outBytes = CctvOverlayRenderer.Render(frame, items);
        Assert.NotEmpty(outBytes);
    }

    [Fact]
    public void Render_손상입력_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            CctvOverlayRenderer.Render([1, 2, 3, 4], []));
    }
}
