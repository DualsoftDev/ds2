using System;
using System.Windows;

namespace Promaker.ViewModels;

internal static class DiscardChangesFlow
{
    public static bool ShouldProceed(MessageBoxResult result, Func<bool> trySave) =>
        result switch
        {
            MessageBoxResult.Yes => trySave(),
            MessageBoxResult.No => true,
            _ => false
        };
}
