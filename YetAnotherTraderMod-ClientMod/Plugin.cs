using BepInEx;
using BepInEx.Logging;
using YetAnotherTraderMod.Client.Services;

namespace YetAnotherTraderMod.Client
{
    [BepInPlugin("com.almightytank.yatmclient", "YATM ClientMod", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource LogSource;

        private void Awake()
        {
            LogSource = Logger;

            WeaponDurabilityRules.Load(LogSource);

            new Patches.ConditionTypeResolverPatch().Enable();
            new Patches.ConditionTypeToKeyPatch().Enable();
            new Patches.ConditionCounterCreatorDurabilityPatch().Enable();
            new Patches.KillConditionDurabilityPatch().Enable();

            LogSource.LogInfo("[YATM ClientMod PreLoad] Loaded.");
        }
    }
}
