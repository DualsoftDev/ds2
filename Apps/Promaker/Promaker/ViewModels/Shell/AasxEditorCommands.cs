using CommunityToolkit.Mvvm.Input;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>유틸 메뉴 "AASX 에디터 열기" — 설치본(또는 dev 빌드본)의 AasxEditor.Desktop 을 실행.</summary>
    [RelayCommand]
    private void OpenAasxEditor() => AasxEditorLauncher.Open();
}
