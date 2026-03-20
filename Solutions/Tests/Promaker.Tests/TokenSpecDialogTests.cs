using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class TokenSpecDialogTests
{
    [Fact]
    public void WorkCombo_SelectionChanged_updates_bound_row()
    {
        StaTestRunner.Run(() =>
        {
            var work = new WorkOption(Guid.NewGuid(), "SourceA");
            var dialog = (TokenSpecDialog)RuntimeHelpers.GetUninitializedObject(typeof(TokenSpecDialog));
            var row = new TokenSpecRow(1, "SpecA", "");
            var combo = new ComboBox
            {
                DataContext = row,
                ItemsSource = new[] { work }
            };
            combo.SelectedItem = work;

            var method = typeof(TokenSpecDialog).GetMethod(
                "WorkCombo_SelectionChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var args = new SelectionChangedEventArgs(
                Selector.SelectionChangedEvent,
                Array.Empty<object>(),
                new object[] { work });

            method.Invoke(dialog, [combo, args]);

            Assert.Equal(work.Id, row.WorkId?.Value);
            Assert.Equal(work.Name, row.WorkName);
        });
    }
}
