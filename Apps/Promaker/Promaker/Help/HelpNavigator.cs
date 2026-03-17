using System.Windows;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;

namespace Promaker.Help;

public static class HelpNavigator
{
    public static ICommand NavigateCommand { get; } = new RelayCommand<string?>(Navigate);

    public static void Navigate(string? topic)
    {
        // TODO: 영상 준비 후 topic별 URL로 Process.Start
        MessageBox.Show("준비 중입니다.", "도움말", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
