using System;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class GanttChartControlRenderingTests
{
    [Fact]
    public void ResolveRowBackgroundResourceKey_returns_work_brush_for_work_entries()
    {
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "WorkA",
            Kind = EntityKind.Work
        };

        var key = GanttChartControl.ResolveRowBackgroundResourceKey(entry);

        Assert.Equal("GanttWorkRowBackgroundBrush", key);
    }

    [Fact]
    public void ResolveRowBackgroundResourceKey_returns_call_brush_for_call_entries()
    {
        var entry = new GanttTimelineEntry
        {
            Id = Guid.NewGuid(),
            Name = "CallA",
            Kind = EntityKind.Call
        };

        var key = GanttChartControl.ResolveRowBackgroundResourceKey(entry);

        Assert.Equal("GanttCallRowBackgroundBrush", key);
    }
}
