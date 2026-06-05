using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.Dialogs;

public partial class ProjectPropertiesDialog : Window
{
    private readonly string _initialProjectName;

    public string? ResultProjectName { get; private set; }
    public string ResultAuthor { get; private set; } = "";
    public DateTimeOffset ResultDateTime { get; private set; } = DateTimeOffset.Now;
    public string ResultVersion { get; private set; } = "1.0.0";

    public ProjectPropertiesDialog(string projectName, DsStore store)
    {
        InitializeComponent();

        _initialProjectName = string.IsNullOrWhiteSpace(projectName) ? "NewProject" : projectName.Trim();
        ProjectNameBox.Text = _initialProjectName;
        var projects = Queries.allProjects(store);
        var project  = projects.IsEmpty ? null : projects.Head;
        AuthorBox.Text        = project?.Author ?? "";
        VersionBox.Text       = project?.Version ?? "";
        DescriptionBox.Text   = "";

        Loaded += (_, _) => ProjectNameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultProjectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? _initialProjectName : ProjectNameBox.Text.Trim();

        ResultAuthor = AuthorBox.Text?.Trim() ?? "";
        ResultVersion = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        ResultDateTime = DateTimeOffset.Now;

        DialogResult = true;
    }
}
