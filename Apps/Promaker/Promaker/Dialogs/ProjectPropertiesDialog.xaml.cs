using System.Windows;
using Ds2.Core;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class ProjectPropertiesDialog : Window
{
    private const string DefaultIriPrefix = "http://your-company.com/";

    public string? ResultIriPrefix { get; private set; }
    public string? ResultGlobalAssetId { get; private set; }
    public string? ResultAuthor { get; private set; }
    public string? ResultVersion { get; private set; }
    public string? ResultDescription { get; private set; }
    public bool ResultSplitDeviceAasx { get; private set; }

    public ProjectPropertiesDialog(ProjectProperties properties)
    {
        InitializeComponent();

        IriPrefixBox.Text     = properties.IriPrefix?.Value     ?? DefaultIriPrefix;
        GlobalAssetIdBox.Text = properties.GlobalAssetId?.Value  ?? "";
        AuthorBox.Text        = properties.Author?.Value         ?? "";
        VersionBox.Text       = properties.Version?.Value        ?? "";
        DescriptionBox.Text   = properties.Description?.Value    ?? "";
        SplitDeviceAasxBox.IsChecked = properties.SplitDeviceAasx;

        Loaded += (_, _) => IriPrefixBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultIriPrefix     = IriPrefixBox.Text.Trim();
        ResultGlobalAssetId = GlobalAssetIdBox.Text.Trim();
        ResultAuthor        = AuthorBox.Text.Trim();
        ResultVersion       = VersionBox.Text.Trim();
        ResultDescription   = DescriptionBox.Text.Trim();
        ResultSplitDeviceAasx = SplitDeviceAasxBox.IsChecked == true;
        DialogResult = true;
    }
}
