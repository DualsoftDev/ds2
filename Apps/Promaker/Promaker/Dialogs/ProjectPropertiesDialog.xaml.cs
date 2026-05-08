using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

/// <summary>
/// 프로젝트 메타 편집 다이얼로그 — 이름 / 작성자 / 버전 / 설명.
/// 환경(앱 전역) 설정은 별도 <see cref="ApplicationSettingsDialog"/> 에서 편집한다.
/// </summary>
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
        AuthorBox.Text      = project?.Author ?? "";
        VersionBox.Text     = project?.Version ?? "";
        DescriptionBox.Text = "";

        Loaded += (_, _) => ProjectNameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultProjectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text)
            ? _initialProjectName
            : ProjectNameBox.Text.Trim();

        ResultAuthor   = AuthorBox.Text?.Trim() ?? "";
        ResultVersion  = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        ResultDateTime = DateTimeOffset.Now;

        DialogResult = true;
    }
}
