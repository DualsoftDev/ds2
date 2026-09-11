using System.Windows;
using System.Windows.Controls;
using Promaker.Dialogs;

namespace Promaker.Controls;

public partial class MainToolbarEtcContent : UserControl
{
    /// 리본 폭이 모자랄 때 true — 설정/도움말/샘플/정보를 '⋯' 드롭다운으로 접는다.
    /// MainToolbar 가 실측 오버플로 판정으로 내려준다.
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainToolbarEtcContent),
            new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public MainToolbarEtcContent()
    {
        InitializeComponent();
    }

    private void CloseUtilPopup(object sender, RoutedEventArgs e) => UtilMenuToggle.IsChecked = false;
    private void CloseOverflowPopup(object sender, RoutedEventArgs e) => OverflowToggle.IsChecked = false;

    private void OverflowAbout_Click(object sender, RoutedEventArgs e)
    {
        OverflowToggle.IsChecked = false;
        AboutButton_Click(sender, e);
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
    }
}
