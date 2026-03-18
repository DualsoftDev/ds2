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
        ["file"]                   = $"{BaseUrl}/res/01_basic_modeling.html",
        ["edit"]                   = $"{BaseUrl}/res/08_edit_menu.html",
        ["simulation"]             = $"{BaseUrl}/res/03_simulation_and_tools.html",
        ["process-control"]        = $"{BaseUrl}/res/03_simulation_and_tools.html",
        ["explorer"]               = $"{BaseUrl}/res/04_explorer_panel.html",
        ["history"]                = $"{BaseUrl}/res/04_explorer_panel.html",
        ["properties"]             = $"{BaseUrl}/res/06_properties.html",
        ["condition-auto-aux"]     = $"{BaseUrl}/res/02_conditions.html",
        ["condition-com-aux"]      = $"{BaseUrl}/res/02_conditions.html",
        ["condition-skip-unmatch"] = $"{BaseUrl}/res/02_conditions.html",
        ["condition"]              = $"{BaseUrl}/res/02_conditions.html",
        ["apicalls"]               = $"{BaseUrl}/res/07_apicalls.html",
        ["tools"]                  = $"{BaseUrl}/res/05_csv_import.html",
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
