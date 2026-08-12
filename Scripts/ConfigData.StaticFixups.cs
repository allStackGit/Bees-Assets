namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        static ConfigData()
        {
            // Keep the hotkey-name catalog one entry per strategy. A single combined
            // "Type V, Type W, Type X" value prevents those three configured hotkeys
            // from receiving actions in LevelInputManager.LoadHotKeySettings().
            ShootingStrategyNames.Remove("Type V, Type W, Type X");
            ShootingStrategyNames.Add("Type V");
            ShootingStrategyNames.Add("Type W");
            ShootingStrategyNames.Add("Type X");
        }
    }
}
