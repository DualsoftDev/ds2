using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;

namespace Promaker.Help;

public static class HelpNavigator
{
    private const string BaseUrl = "http://dualsoft.co.kr/ds2_manual";

    private static readonly Dictionary<string, string> TopicUrls = new()
    {
        ["general"]                = $"{BaseUrl}/index.html",
        ["file"]                   = $"{BaseUrl}/res/01_BasicModeling.html",
        ["edit"]                   = $"{BaseUrl}/res/01_BasicModeling.html",
        ["simulation"]             = $"{BaseUrl}/res/04_Simulation.html",
        ["process-control"]        = $"{BaseUrl}/res/04_Simulation.html",
        ["explorer"]               = $"{BaseUrl}/res/06_Explorer.html",
        ["history"]                = $"{BaseUrl}/res/06_Explorer.html",
        ["properties"]             = $"{BaseUrl}/res/01_BasicModeling.html",
        ["condition-auto-aux"]     = $"{BaseUrl}/res/02_AuxSettings.html",
        ["condition-com-aux"]      = $"{BaseUrl}/res/02_AuxSettings.html",
        ["condition-skip-unmatch"] = $"{BaseUrl}/res/02_AuxSettings.html",
        ["condition"]              = $"{BaseUrl}/res/02_AuxSettings.html",
        ["apicalls"]               = $"{BaseUrl}/res/03_IOBatchSettings.html",
        ["tools"]                  = $"{BaseUrl}/res/05_CsvImport.html",
    };

    public static ICommand NavigateCommand { get; } = new RelayCommand<string?>(Navigate);

    public static void Navigate(string? topic)
    {
        var url = topic is not null && TopicUrls.TryGetValue(topic, out var found)
            ? found
            : $"{BaseUrl}/index.html";

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
