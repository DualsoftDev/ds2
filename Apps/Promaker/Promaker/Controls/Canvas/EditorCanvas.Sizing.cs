namespace Promaker.Controls;

public partial class EditorCanvas
{
    private const double MinCanvasWidth = 3000;
    private const double MinCanvasHeight = 2000;
    private const double CanvasAspectRatio = MinCanvasWidth / MinCanvasHeight;
    private const double CanvasEdgePadding = 100;

    /// <summary>현재 노드들의 위치를 기반으로 캔버스 크기를 다시 계산합니다.
    /// 비율(<see cref="CanvasAspectRatio"/>)을 유지하며 최소 <see cref="MinCanvasWidth"/>×<see cref="MinCanvasHeight"/>까지만 줄어듭니다.</summary>
    public void RecalculateCanvasSize()
    {
        var nodes = ActiveCanvasState?.CanvasNodes;

        double maxX = 0;
        double maxY = 0;

        if (nodes is not null)
        {
            foreach (var n in nodes)
            {
                var right = n.X + n.Width;
                var bottom = n.Y + n.Height;
                if (right > maxX) maxX = right;
                if (bottom > maxY) maxY = bottom;
            }
        }

        var requiredW = maxX + CanvasEdgePadding;
        var requiredH = maxY + CanvasEdgePadding;

        // 비율 유지: width = max(requiredW, requiredH * aspect, minW)
        var width = Math.Max(MinCanvasWidth, Math.Max(requiredW, requiredH * CanvasAspectRatio));
        var height = width / CanvasAspectRatio;

        ApplyCanvasSize(width, height);
    }

    private void ApplyCanvasSize(double width, double height)
    {
        SlideContainer.Width = width;
        SlideContainer.Height = height;
        MainCanvas.Width = width;
        MainCanvas.Height = height;
        CanvasGridBackground.Width = width;
        CanvasGridBackground.Height = height;
    }
}
