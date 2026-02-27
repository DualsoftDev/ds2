using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

internal static class DialogHelpers
{
    internal static void Warn(string message) =>
        MessageBox.Show(message, "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
}
