using System.Windows;

namespace CostSim;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string ResultText { get; private set; } = "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = ValueTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
