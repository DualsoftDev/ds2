using Promaker.Presentation;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private static string SplitDeviceAasxSettingsPath => Promaker.Services.SettingsPaths.SplitDeviceAasx;

    public bool SplitDeviceAasx { get; private set; }

    private void LoadSplitDeviceAasxSetting()
    {
        SplitDeviceAasx = AppSettingStore.LoadBoolOrDefault(SplitDeviceAasxSettingsPath, false);
    }

    public void SetSplitDeviceAasx(bool value)
    {
        if (SplitDeviceAasx == value)
            return;

        SplitDeviceAasx = value;
        AppSettingStore.SaveBool(SplitDeviceAasxSettingsPath, value);
    }

    private static string CreateDefaultEntitiesSettingsPath => Promaker.Services.SettingsPaths.CreateDefaultEntitiesOnEmptyAasx;

    public bool CreateDefaultEntitiesOnEmptyAasx { get; private set; }

    private void LoadCreateDefaultEntitiesSetting()
    {
        CreateDefaultEntitiesOnEmptyAasx = AppSettingStore.LoadBoolOrDefault(CreateDefaultEntitiesSettingsPath, false);
    }

    public void SetCreateDefaultEntitiesOnEmptyAasx(bool value)
    {
        if (CreateDefaultEntitiesOnEmptyAasx == value)
            return;

        CreateDefaultEntitiesOnEmptyAasx = value;
        AppSettingStore.SaveBool(CreateDefaultEntitiesSettingsPath, value);
    }

    private static string IriPrefixSettingsPath => Promaker.Services.SettingsPaths.IriPrefix;

    public string IriPrefix { get; private set; } = "https://dualsoft.com/";

    private void LoadIriPrefixSetting()
    {
        IriPrefix = AppSettingStore.LoadStringOrDefault(IriPrefixSettingsPath, "https://dualsoft.com/");
    }

    public void SetIriPrefix(string value)
    {
        if (IriPrefix == value)
            return;

        IriPrefix = value;
        AppSettingStore.SaveString(IriPrefixSettingsPath, value);
    }
}
