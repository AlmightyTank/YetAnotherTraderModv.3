namespace YetAnotherTraderMod.config;

public static class YATMRuntimeConfig
{
    public static SettingsConfig Settings { get; private set; } = new();

    public static void Set(SettingsConfig settings)
    {
        Settings = settings ?? new SettingsConfig();
    }
}
