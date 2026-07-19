using System;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs.Pv;

/// <summary>
/// PV 다이얼로그(로그인/회원가입/찾기) code-구성 Window 에 다크 테마를 주입한다.
/// PvDialogStyles.xaml(TextBox/PasswordBox/TextBlock/Button 공통 스타일)을 MergedDictionaries 로 얹고
/// Window 배경/전경을 테마 브러시로 건다. 설치마법사 등 다른 다크 폼과 톤을 맞춘다.
/// </summary>
internal static class PvDialogTheme
{
    public static void Apply(Window dialog)
    {
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Promaker;component/Dialogs/Pv/PvDialogStyles.xaml", UriKind.Relative)
        });
        dialog.SetResourceReference(Control.BackgroundProperty, "SecondaryBackgroundBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
    }
}
