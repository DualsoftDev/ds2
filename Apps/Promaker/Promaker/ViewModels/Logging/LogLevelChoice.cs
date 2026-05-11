namespace Promaker.ViewModels.Logging;

/// <summary>
/// Log tab 필터 ComboBox 의 선택 후보. ERROR/FATAL 은 선택과 무관하게 항상 표시되므로 후보에서 제외.
/// </summary>
public enum LogLevelChoice
{
    Debug,
    Info,
    Warn,
}
