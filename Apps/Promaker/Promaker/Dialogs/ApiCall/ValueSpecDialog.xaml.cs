using System.Windows;

namespace Promaker.Dialogs;

public partial class ValueSpecDialog : Window
{
    public ValueSpecDialog(string initialValueSpec, int typeIndex, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
            Title = title;

        SpecEditor.LoadFrom(initialValueSpec, typeIndex);
    }

    public string ValueSpecText => SpecEditor.GetText();
    public int TypeIndex => SpecEditor.GetTypeIndex();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
