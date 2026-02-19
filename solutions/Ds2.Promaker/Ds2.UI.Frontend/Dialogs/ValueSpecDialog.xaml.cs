using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ValueSpecDialog : Window
{
    public ValueSpecDialog(string initialValueSpec, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
            Title = title;

        SpecEditor.LoadFromText(initialValueSpec);
    }

    public string ValueSpecText => SpecEditor.GetText();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
