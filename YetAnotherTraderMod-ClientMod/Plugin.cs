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

            // Dynamic inventory layouts for configured YATM custom rigs.
            new Patches.TonyRigCreateGridsPatch().Enable();
            new Patches.TonyRigContainedGridsViewShowPatch().Enable();

            LogSource.LogInfo("[YATM Rig Grid] Dynamic custom-rig grid layouts enabled for 2 template IDs.");
            LogSource.LogInfo("[YATM ClientMod PreLoad] Loaded.");
        }
    }
}
