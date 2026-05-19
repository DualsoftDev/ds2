using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// AppData 디스크에 저장되는 사용자 설정 값들 (AASX export 동작 + IRI prefix). MainViewModel 의 partial 에서 분리.
/// 다이얼로그(ProjectPropertiesDialog/ApplicationSettingsDialog)는 자체 AppSettingStore 를 직접 쓰지만
/// 다이얼로그 결과를 MainViewModel 메모리에 반영하는 책임은 이 collaborator 가 갖는다 (Set* 메서드).
/// 외부 호출자: MainViewModel ctor (LoadAll), FileCommands.OpenProjectPropertiesDialog (Set*), Save.SaveCurrentToAasx / SaveAs (read).
/// </summary>
public sealed class MainAppSettings
{
    private static string SplitDeviceAasxPath        => SettingsPaths.SplitDeviceAasx;
    private static string CreateDefaultEntitiesPath  => SettingsPaths.CreateDefaultEntitiesOnEmptyAasx;
    private static string IriPrefixPath              => SettingsPaths.IriPrefix;

    private const string DefaultIriPrefix = "https://dualsoft.com/";

    public bool   SplitDeviceAasx                 { get; private set; }
    public bool   CreateDefaultEntitiesOnEmptyAasx{ get; private set; }
    public string IriPrefix                       { get; private set; } = DefaultIriPrefix;

    /// <summary>MainViewModel ctor 진입 시 한 번 호출 — 디스크에서 3 설정값 모두 로드.</summary>
    public void LoadAll()
    {
        SplitDeviceAasx                  = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxPath, false);
        CreateDefaultEntitiesOnEmptyAasx = AppSettingStore.LoadBoolOrDefault(CreateDefaultEntitiesPath, false);
        IriPrefix                        = AppSettingStore.LoadStringOrDefault(IriPrefixPath, DefaultIriPrefix);
    }

    public void SetSplitDeviceAasx(bool value)
    {
        if (SplitDeviceAasx == value) return;
        SplitDeviceAasx = value;
        AppSettingStore.SaveBool(SplitDeviceAasxPath, value);
    }

    public void SetCreateDefaultEntitiesOnEmptyAasx(bool value)
    {
        if (CreateDefaultEntitiesOnEmptyAasx == value) return;
        CreateDefaultEntitiesOnEmptyAasx = value;
        AppSettingStore.SaveBool(CreateDefaultEntitiesPath, value);
    }

    public void SetIriPrefix(string value)
    {
        if (IriPrefix == value) return;
        IriPrefix = value;
        AppSettingStore.SaveString(IriPrefixPath, value);
    }
}
