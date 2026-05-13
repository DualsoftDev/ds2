using System.Windows;

namespace Promaker.Dialogs;

public partial class ApiCallSpecDialog : Window
{
    public ApiCallSpecDialog(string apiCallName, string outSpecText, int outTypeIndex, string inSpecText, int inTypeIndex, bool useInputSensor)
    {
        InitializeComponent();
        ApiCallNameText.Text = $"ApiCall: {apiCallName}";
        OutSpecEditor.LoadFrom(outSpecText, outTypeIndex);
        InSpecEditor.LoadFrom(inSpecText, inTypeIndex);
        UseInputSensorCheck.IsChecked = useInputSensor;
        UpdateInSpecEditorEnabled();
    }

    public string OutSpecText => OutSpecEditor.GetText();
    public int OutSpecTypeIndex => OutSpecEditor.GetTypeIndex();
    public string InSpecText => InSpecEditor.GetText();
    public int InSpecTypeIndex => InSpecEditor.GetTypeIndex();
    public bool UseInputSensor => UseInputSensorCheck.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void UseInputSensorCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateInSpecEditorEnabled();
    }

    private void UpdateInSpecEditorEnabled()
    {
        // UseInputSensor=false 면 실 센서 안 쓰므로 InSpec 의미 없음 → 시각적 disable.
        if (InSpecEditor != null)
            InSpecEditor.IsEnabled = UseInputSensorCheck?.IsChecked == true;
    }
}
