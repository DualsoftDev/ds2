using System.Windows;

namespace Promaker.Dialogs.ConfigEditor;

public partial class ConfigEditorWindow : Window
{
    public ConfigEditorWindow(ConfigEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 저장 완료 시 창 닫기
        viewModel.CloseRequested += () => Close();
    }
}
