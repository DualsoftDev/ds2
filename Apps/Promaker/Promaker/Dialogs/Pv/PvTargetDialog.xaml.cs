using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Promaker.Services;

namespace Promaker.Dialogs.Pv;

/// <summary>다이얼로그의 용도 — 제목/확인 버튼 문구를 가른다 (업로드 대상 vs 가져오기 원본).</summary>
public enum PvTransferIntent
{
    Upload,
    Download,
}

/// <summary>
/// 전송 대상 선택 — macOS Finder 컬럼 뷰(사이트 › 단말 › 상세). 로그인 토큰으로
/// <see cref="IPvClient.Overview"/> 를 받아 트리를 채운다. 서버 정보는 IPvClient 뒤에 있고,
/// 배경·보더·텍스트·선택색·버튼이 전부 테마 브러시(DynamicResource)라 라이트/다크를 따른다.
/// 업로드/가져오기 양쪽이 재사용하므로 문구는 <see cref="PvTransferIntent"/> 로 갈아끼운다.
/// </summary>
public partial class PvTargetDialog : Window
{
    public PvSite? SelectedSite { get; private set; }
    public PvEdge? SelectedEdge { get; private set; }

    private PvTargetDialog(IReadOnlyList<PvSite> sites, PvTransferIntent intent)
    {
        InitializeComponent();
        SiteList.ItemsSource = sites;
        if (intent == PvTransferIntent.Download)
        {
            Title = "가져오기 원본 선택";
            ConfirmButton.Content = "이 단말에서 가져오기";
        }
    }

    /// <summary>조회 후 탐색 창을 띄운다. 선택 시 (사이트, 단말), 취소/실패 시 null.</summary>
    public static (PvSite Site, PvEdge Edge)? Show(
        IPvClient client, string token, PvTransferIntent intent, Window? owner = null)
    {
        var r = client.Overview(token);
        if (!r.Ok)
        {
            DialogHelpers.Warn(owner, r.Message ?? "사이트/단말 조회에 실패했습니다.");
            return null;
        }
        if (r.Sites.Count == 0)
        {
            DialogHelpers.Info(owner, "등록된 사이트가 없습니다.");
            return null;
        }

        var dlg = new PvTargetDialog(r.Sites, intent) { Owner = owner ?? Application.Current?.MainWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedSite is not null && dlg.SelectedEdge is not null)
            return (dlg.SelectedSite, dlg.SelectedEdge);
        return null;
    }

    private void SiteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedSite = SiteList.SelectedItem as PvSite;
        EdgeList.ItemsSource = SelectedSite?.Edges;
        SelectedEdge = null;
        ConfirmButton.IsEnabled = false;
        DetailText.Text = "단말을 선택하세요.";
    }

    private void EdgeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedEdge = EdgeList.SelectedItem as PvEdge;
        ConfirmButton.IsEnabled = SelectedEdge is not null;
        DetailText.Text = SelectedEdge is null
            ? "단말을 선택하세요."
            : $"단말: {SelectedEdge.DisplayName}\n"
              + $"인스턴스 상태: {SelectedEdge.InstanceStatus ?? "-"}\n"
              + $"인스턴스 IP: {SelectedEdge.PublicIp ?? "-"}";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEdge is not null)
            DialogResult = true;
    }
}
