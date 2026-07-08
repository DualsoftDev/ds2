// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Cctv;
using SkiaSharp;

namespace DSPilot.Services;

/// <summary>
/// CCTV 프레임 위에 설비 오버레이를 서버측 합성(SkiaSharp)하는 렌더러. 무상태 static.
///
/// 클라이언트(cctv.html)의 DOM 오버레이(.cctv-ovl-box / .cctv-ovl-pin)를 시각적으로 재현한다:
/// - Flow = ZONE: 라운드 사각 테두리 + 12% 채움 + 상단 라벨 칩
/// - Call = PIN: 원형 헤드(다이아몬드 글리프) + 꼬리 삼각형 + 하단 라벨 칩
/// 좌표는 정규화 0~1 → 네이티브 프레임에 직접 그리므로 letterbox 보정 불필요(px = 정규값 × 프레임 크기).
/// 상태색은 CSS 변수를 서버에서 읽을 수 없어 다크 테마(ds.css .dark-theme) hex 를 고정 사용 —
/// 영상(어두운 배경) 위 시인성이 더 좋은 쪽. 클라 colorOf() 와 상태→색 매핑 동일.
/// </summary>
public static class CctvOverlayRenderer
{
    /// <summary>합성할 오버레이 1개 + 라이브 상태(빈 문자열 = 미상).</summary>
    public sealed record Item(CctvOverlay Overlay, string State);

    // ds.css .dark-theme: --color-warning / --red / --green. 미상(unknown)만 라이트 값(#93A3B5) —
    // 다크 disabled(#5A6B80)는 영상 위에서 안 보임.
    private static readonly SKColor Going = new(0xFF, 0xB0, 0x20);
    private static readonly SKColor Error = new(0xFF, 0x65, 0x52);
    private static readonly SKColor Ready = new(0x3F, 0xD0, 0x8A);
    private static readonly SKColor Unknown = new(0x93, 0xA3, 0xB5);

    // 한글 라벨 렌더 가능한 시스템 폰트 (Windows=맑은 고딕 등, Linux=설치 폰트 매칭). 프로세스 수명 캐시.
    private static readonly Lazy<SKTypeface> LabelTypeface = new(() =>
        SKFontManager.Default.MatchCharacter('한') ?? SKTypeface.Default);

    /// <summary>
    /// baseImage(jpeg/png/webp) 위에 오버레이를 합성해 JPEG 로 반환.
    /// targetWidth 지정 시 비율 유지 다운스케일(업스케일은 안 함).
    /// </summary>
    public static byte[] Render(byte[] baseImage, IReadOnlyList<Item> items, int? targetWidth = null, int jpegQuality = 85)
    {
        // SKBitmap.Decode 는 입력에 따라 null 반환/자체 예외 둘 다 가능 → 호출자 계약은 ArgumentException 하나로 정규화.
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(baseImage); }
        catch (Exception ex) { throw new ArgumentException("이미지를 디코드할 수 없습니다.", ex); }
        using var src = decoded ?? throw new ArgumentException("이미지를 디코드할 수 없습니다.");

        int w = src.Width, h = src.Height;
        if (targetWidth is int tw && tw > 0 && tw < w)
        {
            h = Math.Max(1, (int)Math.Round((double)src.Height * tw / src.Width));
            w = tw;
        }

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.DrawBitmap(src, SKRect.Create(w, h),
            new SKPaint { FilterQuality = SKFilterQuality.Medium });

        // 클라이언트 타일과 비슷한 시각 비중이 되도록 프레임 폭 비례 스케일.
        float pinSize = Math.Clamp(w * 0.045f, 22f, 84f);          // ≈ --cctv-ovl-sz
        float chipFontSize = Math.Clamp(w * 0.016f, 11f, 34f);
        // SkiaSharp 2.88 문자열 텍스트 API 는 SKPaint 기반 (SKFont.MeasureText 는 glyph id 전용).
        using var font = new SKPaint
        {
            IsAntialias = true,
            Typeface = LabelTypeface.Value,
            TextSize = chipFontSize,
            FakeBoldText = true,
            Color = SKColors.White,
        };

        foreach (var it in items)
        {
            var color = ColorOf(it.State);
            if (it.Overlay.CallId is null) DrawZone(canvas, it.Overlay, color, w, h, font);
            else DrawPin(canvas, it.Overlay, color, w, h, pinSize, font);
        }

        canvas.Flush();
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return data.ToArray();
    }

    /// <summary>클라 colorOf() 와 동일 매핑.</summary>
    private static SKColor ColorOf(string? state) => (state ?? "").ToLowerInvariant() switch
    {
        "going" => Going,
        "error" => Error,
        "ready" or "finish" => Ready,
        _ => Unknown,
    };

    /// <summary>Flow 영역(ZONE): 라운드 사각 테두리 + 12% 채움 + 라벨 칩(.cctv-ovl-box.kind-flow 재현).</summary>
    private static void DrawZone(SKCanvas canvas, CctvOverlay o, SKColor color, int w, int h, SKPaint font)
    {
        var rect = SKRect.Create(
            (float)(o.X * w), (float)(o.Y * h),
            (float)(o.W * w), (float)(o.H * h));
        float stroke = Math.Max(2f, w * 0.0022f);
        float radius = Math.Max(6f, w * 0.008f);          // --radius-md 상당

        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(31) }) // 12%
            canvas.DrawRoundRect(rect, radius, radius, fill);
        using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color })
            canvas.DrawRoundRect(rect, radius, radius, border);

        var label = LabelText(o);
        if (label.Length == 0) return;
        // 칩은 박스 위(top-22px 상당), 상단 여유 없으면 아래(tagBelow), 좌측 가장자리는 안쪽으로(tagInsetLeft).
        float chipH = font.TextSize * 1.7f;
        float chipY = rect.Top - chipH - 2f;
        if (chipY < 2f) chipY = rect.Bottom + 2f;
        float chipX = Math.Max(2f, rect.Left - 2f);
        DrawChip(canvas, label, chipX, chipY, color, font, maxWidth: w * 0.35f);
    }

    /// <summary>Call 핀(PIN): 원형 헤드 + 다이아몬드 글리프 + 꼬리 + 하단 라벨 칩(.cctv-ovl-pin 재현). (x,y)=헤드 중심.</summary>
    private static void DrawPin(SKCanvas canvas, CctvOverlay o, SKColor color, int w, int h, float size, SKPaint font)
    {
        float cx = (float)(o.X * w), cy = (float)(o.Y * h);
        float r = size / 2f;

        // 꼬리(헤드 하단 삼각형) — 헤드보다 먼저 그려 겹침을 헤드가 덮게.
        using (var tail = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color })
        {
            using var path = new SKPath();
            path.MoveTo(cx - size * 0.22f, cy + r - 2f);
            path.LineTo(cx + size * 0.22f, cy + r - 2f);
            path.LineTo(cx, cy + r + size * 0.3f);
            path.Close();
            canvas.DrawPath(path, tail);
        }

        using (var head = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color })
            canvas.DrawCircle(cx, cy, r, head);
        using (var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2f, size * 0.08f), Color = SKColors.White.WithAlpha(102) // 40%
        })
            canvas.DrawCircle(cx, cy, r, ring);

        // 다이아몬드 글리프(Material 아이콘 폰트 대체 — 회전 사각형).
        using (var glyph = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White })
        {
            float g = r * 0.52f;
            using var path = new SKPath();
            path.MoveTo(cx, cy - g);
            path.LineTo(cx + g, cy);
            path.LineTo(cx, cy + g);
            path.LineTo(cx - g, cy);
            path.Close();
            canvas.DrawPath(path, glyph);
        }

        var label = LabelText(o);
        if (label.Length == 0) return;
        float chipY = cy + r + size * 0.3f + 4f;
        DrawChip(canvas, label, cx, chipY, color, font, maxWidth: w * 0.3f, centerX: true);
    }

    /// <summary>라벨 칩(.cctv-ovl-chip/.cctv-pin-lbl 재현): 상태색 배경 + 흰 텍스트. maxWidth 초과 시 말줄임.</summary>
    private static void DrawChip(SKCanvas canvas, string text, float x, float y, SKColor color, SKPaint font,
        float maxWidth, bool centerX = false)
    {
        float padX = font.TextSize * 0.6f;
        var shown = Ellipsize(text, font, maxWidth - padX * 2);
        float textW = font.MeasureText(shown);
        float chipW = textW + padX * 2;
        float chipH = font.TextSize * 1.7f;
        float left = centerX ? x - chipW / 2f : x;

        using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color })
            canvas.DrawRoundRect(SKRect.Create(left, y, chipW, chipH), chipH * 0.22f, chipH * 0.22f, bg);
        // 세로 중앙 정렬: baseline = top + (chipH + capHeight)/2 근사. 텍스트 색/굵기는 font(SKPaint)에 설정됨.
        float baseline = y + chipH / 2f + font.TextSize * 0.36f;
        canvas.DrawText(shown, left + padX, baseline, font);
    }

    /// <summary>maxWidth 를 넘는 라벨을 "…" 말줄임.</summary>
    private static string Ellipsize(string text, SKPaint font, float maxWidth)
    {
        if (maxWidth <= 0 || font.MeasureText(text) <= maxWidth) return text;
        const string dots = "…";
        for (int len = text.Length - 1; len > 0; len--)
        {
            var t = text[..len] + dots;
            if (font.MeasureText(t) <= maxWidth) return t;
        }
        return dots;
    }

    /// <summary>클라 labelText() 와 동일: Label ▸ CallName ▸ FlowName.</summary>
    private static string LabelText(CctvOverlay o)
        => !string.IsNullOrWhiteSpace(o.Label) ? o.Label!
         : !string.IsNullOrWhiteSpace(o.CallName) ? o.CallName
         : o.FlowName ?? "";
}
