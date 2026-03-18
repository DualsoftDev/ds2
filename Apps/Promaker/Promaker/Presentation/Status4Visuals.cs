using System.Windows;
using System.Windows.Media;
using Ds2.Core;

namespace Promaker.Presentation;

internal static class Status4Visuals
{
    public static string BrushKey(Status4 status) => status switch
    {
        Status4.Ready => "SimulationReadyBrush",
        Status4.Going => "SimulationGoingBrush",
        Status4.Finish => "SimulationFinishBrush",
        Status4.Homing => "SimulationHomingBrush",
        _ => "SimulationHomingBrush"
    };

    public static Brush ResolveBrush(Status4 status) =>
        Application.Current?.TryFindResource(BrushKey(status)) as Brush ?? Brushes.Gray;

    public static string GanttBarBrushKey(Status4 status) => status switch
    {
        Status4.Ready => "GanttBarReadyBrush",
        Status4.Going => "GanttBarGoingBrush",
        Status4.Finish => "GanttBarFinishBrush",
        Status4.Homing => "GanttBarHomingBrush",
        _ => "GanttBarHomingBrush"
    };

    public static Brush ResolveGanttBarBrush(Status4 status) =>
        Application.Current?.TryFindResource(GanttBarBrushKey(status)) as Brush ?? Brushes.Gray;

    public static string ShortCode(Status4 status) => status.ToString()[..1];

    public static string DisplayName(Status4 status) => status.ToString();
}
