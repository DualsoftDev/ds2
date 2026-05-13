using System.Windows;

namespace Promaker.Dialogs;

public partial class ApiCallSpecDialog : Window
{
    public ApiCallSpecDialog(string apiCallName, string outSpecText, int outTypeIndex, string inSpecText, int inTypeIndex, bool skipInputSensor)
    {
        InitializeComponent();
        ApiCallNameText.Text = $"ApiCall: {apiCallName}";
        OutSpecEditor.LoadFrom(outSpecText, outTypeIndex);
        InSpecEditor.LoadFrom(inSpecText, inTypeIndex);
        SkipInputSensorCheck.IsChecked = skipInputSensor;
        UpdateInSpecEditorEnabled();
    }

    public string OutSpecText => OutSpecEditor.GetText();
    public int OutSpecTypeIndex => OutSpecEditor.GetTypeIndex();
    public string InSpecText => InSpecEditor.GetText();
    public int InSpecTypeIndex => InSpecEditor.GetTypeIndex();
    public bool SkipInputSensor => SkipInputSensorCheck.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SkipInputSensorCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateInSpecEditorEnabled();
    }

    private void UpdateInSpecEditorEnabled()
    {
        // SkipInputSensor=true 면 실 센서 안 쓰므로 InSpec 의미 없음 → 시각적 disable.
        if (InSpecEditor != null)
            InSpecEditor.IsEnabled = SkipInputSensorCheck?.IsChecked != true;
    }
}
