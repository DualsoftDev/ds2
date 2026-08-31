using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

/// <summary>
/// 다른 프로젝트 파일(headless 로드된 임시 store)에서 가져올 System 을 고르는 다이얼로그.
/// 이름 입력은 없다 — 충돌 개명은 코어(nextUniqueName)가 전담하고 결과만 통보 (설계 §4-2).
/// </summary>
public partial class ProjectImportDialog : Window
{
    /// <summary>체크박스 항목 1건. IsChecked 는 OK 시점에만 읽으므로 INPC 불필요.</summary>
    public sealed class SystemPickItem
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool IsActive { get; init; }
        public bool IsChecked { get; set; }
    }

    private readonly List<SystemPickItem> _activeItems = [];
    private readonly List<SystemPickItem> _passiveItems = [];

    /// <summary>[가져오기] 확정 시 선택된 루트들 (설비=IsActive true / 디바이스=false).</summary>
    public List<SystemImportRoot> SelectedRoots { get; } = [];

    public ProjectImportDialog(DsStore sourceStore, Project sourceProject, string sourceLabel)
    {
        InitializeComponent();
        SourceText.Text = $"원본: {sourceLabel}";

        foreach (var id in sourceProject.ActiveSystemIds)
        {
            if (!sourceStore.SystemsReadOnly.TryGetValue(id, out var system))
                continue;
            var flows = Queries.flowsOf(id, sourceStore);
            var workCount = flows.Sum(f => Queries.worksOf(f.Id, sourceStore).Count());
            _activeItems.Add(new SystemPickItem
            {
                Id = id,
                Name = system.Name,
                Detail = $"Flow {flows.Length} · Work {workCount}",
                IsActive = true,
            });
        }

        foreach (var id in sourceProject.PassiveSystemIds)
        {
            if (!sourceStore.SystemsReadOnly.TryGetValue(id, out var system))
                continue;
            var apiDefCount = Queries.apiDefsOf(id, sourceStore).Length;
            _passiveItems.Add(new SystemPickItem
            {
                Id = id,
                Name = system.Name,
                Detail = $"Action {apiDefCount}",
                IsActive = false,
            });
        }

        ActiveList.ItemsSource = _activeItems;
        PassiveList.ItemsSource = _passiveItems;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedRoots.Clear();
        foreach (var item in _activeItems.Concat(_passiveItems).Where(i => i.IsChecked))
            SelectedRoots.Add(new SystemImportRoot(item.Id, item.IsActive));

        if (SelectedRoots.Count == 0)
        {
            MessageBox.Show(this, "가져올 시스템을 선택하세요.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
