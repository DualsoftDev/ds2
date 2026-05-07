using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AAStoPLC.Ir;

namespace Promaker.Controls.ExpressionEditor.Rendering;

/// <summary>
/// CoilLayout 결과를 WPF Canvas 에 렌더 — Pre-FB 식 미리보기.
/// 좌측 power rail + 컨택트 + 가로/세로 라인 + 우측 출력 (사각형 placeholder).
/// </summary>
public static class LdPreviewRenderer
{
    private const double MinColW  = 80.0;
    private const double LabelPad = 18.0;
    private const double LabelFontSize = 10.0;
    private const double RowH    = 40.0;
    private const double LeftPad = 16.0;
    private const double TopPad  = 12.0;

    // 동적 col 폭 — 각 컬럼의 가장 긴 라벨에 맞춰 너비 결정.
    private static double[] _colWidths = Array.Empty<double>();

    public static void Render(Canvas canvas, LdLayoutResult layout)
    {
        canvas.Children.Clear();
        if (layout == null) return;

        var fg     = (Brush)(canvas.TryFindResource("PrimaryTextBrush") ?? Brushes.Gainsboro);
        var wire   = (Brush)(canvas.TryFindResource("BorderBrush") ?? Brushes.Gray);
        var rail   = (Brush)(canvas.TryFindResource("AccentBrush") ?? Brushes.SteelBlue);

        int rows  = Math.Max(1, layout.Rows);
        int endCol = 1;
        foreach (var e in layout.Elements)
        {
            int c = e switch
            {
                LdLayoutElement.Contact c1   => c1.col + 1,
                LdLayoutElement.HorzFill h   => h.toCol + 1,
                LdLayoutElement.VertLink v   => v.col + 1,
                _ => 1
            };
            if (c > endCol) endCol = c;
        }
        // FB 핀은 endCol + 여백 1 col 위치.
        int pinCol = endCol + 1;

        // 컬럼별 최대 라벨 폭 측정 — 각 col 의 컨택트 label 너비 + padding.
        _colWidths = new double[Math.Max(pinCol + 1, 1)];
        for (int i = 0; i < _colWidths.Length; i++) _colWidths[i] = MinColW;
        var typeface = new Typeface("Segoe UI");
        foreach (var el in layout.Elements)
        {
            if (el is LdLayoutElement.Contact ct)
            {
                var w = MeasureText(ct.name ?? "", typeface) + LabelPad;
                if (ct.col >= 0 && ct.col < _colWidths.Length && w > _colWidths[ct.col])
                    _colWidths[ct.col] = w;
            }
        }

        // x offset 누적
        double width = LeftPad;
        for (int i = 0; i < _colWidths.Length; i++) width += _colWidths[i];
        canvas.Width  = width + 80;
        canvas.Height = TopPad  + rows * RowH + TopPad;

        // 좌측 power rail
        canvas.Children.Add(new Line
        {
            X1 = LeftPad, Y1 = TopPad,
            X2 = LeftPad, Y2 = TopPad + rows * RowH,
            Stroke = rail, StrokeThickness = 2,
        });

        foreach (var el in layout.Elements)
        {
            switch (el)
            {
                case LdLayoutElement.Contact c:
                    DrawContact(canvas, c.row, c.col, c.kind, c.name, fg, wire);
                    break;
                case LdLayoutElement.HorzFill h:
                    DrawHorzFill(canvas, h.row, h.fromCol, h.toCol, wire);
                    break;
                case LdLayoutElement.VertLink v:
                    DrawVertLink(canvas, v.row, v.col, wire);
                    break;
            }
        }

        // 메인 row 의 식 끝 col 부터 FB 핀 col 까지 가로선 (와이어 연결).
        double mainY = YMid(0);
        double mainStart = X(endCol);
        double pinX = X(pinCol);
        canvas.Children.Add(new Line
        {
            X1 = mainStart, Y1 = mainY, X2 = pinX, Y2 = mainY,
            Stroke = wire, StrokeThickness = 1.4,
        });

        // FB 입력 핀 placeholder
        var pinRect = new Rectangle
        {
            Width = 28, Height = RowH * 0.6, Stroke = fg, StrokeThickness = 1,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(pinRect, pinX);
        Canvas.SetTop(pinRect, TopPad + (RowH - RowH * 0.6) / 2);
        canvas.Children.Add(pinRect);
        var pinLabel = new TextBlock { Text = "→ Target", Foreground = fg, FontSize = 10 };
        Canvas.SetLeft(pinLabel, pinX + 32);
        Canvas.SetTop(pinLabel, TopPad + (RowH - 14) / 2);
        canvas.Children.Add(pinLabel);
    }

    private static double X(int col)
    {
        double x = LeftPad;
        for (int i = 0; i < col && i < _colWidths.Length; i++) x += _colWidths[i];
        return x;
    }
    private static double ColWidthAt(int col) =>
        (col >= 0 && col < _colWidths.Length) ? _colWidths[col] : MinColW;
    private static double YMid(int row) => TopPad + row * RowH + RowH / 2;

    private static double MeasureText(string text, Typeface tf)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, tf, LabelFontSize, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static void DrawContact(Canvas c, int row, int col, LdContactKind kind, string name, Brush fg, Brush wire)
    {
        double cellLeft  = X(col);
        double cellW     = ColWidthAt(col);
        double cellRight = cellLeft + cellW;
        double y = YMid(row);
        double contactW = 36;
        double contactLeftX  = cellLeft + (cellW - contactW) / 2;
        double contactRightX = contactLeftX + contactW;
        // 와이어 좌 (rail / 이전 cell 끝 → 컨택트 좌측)
        c.Children.Add(new Line { X1 = cellLeft, Y1 = y, X2 = contactLeftX, Y2 = y, Stroke = wire, StrokeThickness = 1.4 });
        // 컨택트 양쪽 세로선
        c.Children.Add(new Line { X1 = contactLeftX,  Y1 = y - 10, X2 = contactLeftX,  Y2 = y + 10, Stroke = fg, StrokeThickness = 1.4 });
        c.Children.Add(new Line { X1 = contactRightX, Y1 = y - 10, X2 = contactRightX, Y2 = y + 10, Stroke = fg, StrokeThickness = 1.4 });
        // NC / Pulse / Inverter 표식
        if (kind == LdContactKind.NcContact)
            c.Children.Add(new Line { X1 = contactLeftX,  Y1 = y + 10, X2 = contactRightX, Y2 = y - 10, Stroke = fg, StrokeThickness = 1.0 });
        else if (kind == LdContactKind.RisingPulse)
            AddText(c, "P", contactLeftX + contactW / 2 - 4, y - 6, 10, fg);
        else if (kind == LdContactKind.FallingPulse)
            AddText(c, "N", contactLeftX + contactW / 2 - 4, y - 6, 10, fg);
        else if (kind == LdContactKind.Inverter)
            AddText(c, "✱", contactLeftX + contactW / 2 - 6, y - 8, 14, fg);
        // 와이어 우 (컨택트 우측 → cell 끝)
        c.Children.Add(new Line { X1 = contactRightX, Y1 = y, X2 = cellRight, Y2 = y, Stroke = wire, StrokeThickness = 1.4 });
        // 라벨 (컨택트 위)
        AddText(c, name ?? "", cellLeft + 4, y - 24, 10, fg);
    }

    private static void DrawHorzFill(Canvas c, int row, int fromCol, int toCol, Brush wire)
    {
        double y = YMid(row);
        c.Children.Add(new Line
        {
            X1 = X(fromCol), Y1 = y, X2 = X(toCol + 1), Y2 = y,
            Stroke = wire, StrokeThickness = 1.4,
        });
    }

    private static void DrawVertLink(Canvas c, int row, int col, Brush wire)
    {
        double x = X(col + 1) - 0.5;
        double y1 = YMid(row);
        double y2 = YMid(row + 1);
        c.Children.Add(new Line { X1 = x, Y1 = y1, X2 = x, Y2 = y2, Stroke = wire, StrokeThickness = 1.4 });
    }

    private static void AddText(Canvas c, string text, double x, double y, double size, Brush fg)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }
}
