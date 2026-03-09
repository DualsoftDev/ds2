using System.Windows;

namespace Promaker.Dialogs;

internal static class DialogHelpers
{
    internal static void Warn(string message) =>
        MessageBox.Show(message, "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 저장되지 않은 변경이 있을 때 확인 팝업.
    /// Yes=저장 후 계속, No=저장 안 하고 계속, Cancel=취소.
    /// </summary>
    internal static MessageBoxResult AskSaveChanges() =>
        MessageBox.Show(
            "현재 프로젝트에 저장되지 않은 변경이 있습니다.\n저장하시겠습니까?",
            "저장 확인",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
}
