using System.Windows;
using Ds2.Core;
using Microsoft.FSharp.Core;
using Promaker.Presentation;

namespace Promaker.Dialogs;

public partial class ProjectPropertiesDialog : Window
{
    private const string DefaultIriPrefix = "http://your-company.com/";
    private bool _suppressThemeEvent;

    public string? ResultIriPrefix { get; private set; }
    public string? ResultGlobalAssetId { get; private set; }
    public string? ResultAuthor { get; private set; }
    public string? ResultVersion { get; private set; }
    public string? ResultDescription { get; private set; }
    public bool ThemeChanged { get; private set; }

    public ProjectPropertiesDialog(ProjectProperties properties)
    {
        InitializeComponent();

        _suppressThemeEvent = true;
        if (ThemeManager.CurrentTheme == AppTheme.Dark)
            DarkThemeRadio.IsChecked = true;
        else
            LightThemeRadio.IsChecked = true;
        _suppressThemeEvent = false;

        IriPrefixBox.Text     = properties.IriPrefix?.Value     ?? DefaultIriPrefix;
        GlobalAssetIdBox.Text = properties.GlobalAssetId?.Value  ?? "";
        AuthorBox.Text        = properties.Author?.Value         ?? "";
        VersionBox.Text       = properties.Version?.Value        ?? "";
        DescriptionBox.Text   = properties.Description?.Value    ?? "";

        Loaded += (_, _) => IriPrefixBox.Focus();
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeEvent) return;
        var theme = DarkThemeRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
        if (theme != ThemeManager.CurrentTheme)
        {
            ThemeManager.ApplyTheme(theme);
            ThemeChanged = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultIriPrefix     = IriPrefixBox.Text.Trim();
        ResultGlobalAssetId = GlobalAssetIdBox.Text.Trim();
        ResultAuthor        = AuthorBox.Text.Trim();
        ResultVersion       = VersionBox.Text.Trim();
        ResultDescription   = DescriptionBox.Text.Trim();
        DialogResult = true;
    }
}
