using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

// 진단용 스모크 — 새 프로젝트 버튼의 가시 표면(Explorer 트리 + HasProject) 검증.
public sealed class NewProjectSmokeTest
{
    [Fact]
    public void NewProject_populates_explorer_tree()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);
            StaTestRunner.PumpPendingUi();   // RequestRebuildAll 의 Background BeginInvoke 소진

            Assert.True(vm.HasProject);
            Assert.NotEmpty(vm.ControlTreeRoots);
        });
    }
}
