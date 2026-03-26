using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class DialogHelpersAndGanttShapeTests
{
    [Fact]
    public void PickCallFromList_returns_void()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var type = assembly.GetType("Promaker.Dialogs.DialogHelpers", throwOnError: true)!;
        var method = type.GetMethod(
            "PickCallFromList",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Theory]
    [InlineData(nameof(GanttTimelineEntry.Id))]
    [InlineData(nameof(GanttTimelineEntry.Name))]
    [InlineData(nameof(GanttTimelineEntry.Kind))]
    [InlineData(nameof(GanttTimelineEntry.ParentWorkId))]
    [InlineData(nameof(GanttTimelineEntry.SystemName))]
    [InlineData(nameof(GanttTimelineEntry.RowIndex))]
    public void GanttTimelineEntry_identity_properties_are_init_only(string propertyName)
    {
        var property = typeof(GanttTimelineEntry).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.NotNull(property!.SetMethod);
        Assert.Contains(
            typeof(IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
    }
}
