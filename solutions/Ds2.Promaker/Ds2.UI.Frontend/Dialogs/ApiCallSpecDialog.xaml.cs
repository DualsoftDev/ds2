using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ApiCallSpecDialog : Window
{
    public ApiCallSpecDialog(string apiCallName, string outSpecText, int outTypeIndex, string inSpecText, int inTypeIndex)
    {
        InitializeComponent();
        ApiCallNameText.Text = $"ApiCall: {apiCallName}";
        OutSpecEditor.LoadFrom(outSpecText, outTypeIndex);
        InSpecEditor.LoadFrom(inSpecText, inTypeIndex);
    }

    public string OutSpecText => OutSpecEditor.GetText();
    public string InSpecText => InSpecEditor.GetText();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
