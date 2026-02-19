using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ApiCallSpecDialog : Window
{
    public ApiCallSpecDialog(string apiCallName, string outSpecText, string inSpecText)
    {
        InitializeComponent();
        ApiCallNameText.Text = $"ApiCall: {apiCallName}";
        OutSpecEditor.LoadFromText(outSpecText);
        InSpecEditor.LoadFromText(inSpecText);
    }

    public string OutSpecText => OutSpecEditor.GetText();
    public string InSpecText  => InSpecEditor.GetText();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
