using System;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using YetAnotherTraderMod.Client.Models;

namespace YetAnotherTraderMod.Client.Patches
{
    internal class ConditionTypeResolverPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass1871), nameof(GClass1871.KeyToType));
        }

        [PatchPrefix]
        private static bool Prefix(string serializedType, ref Type __result)
        {
            if (string.Equals(serializedType, "weaponDurability", StringComparison.OrdinalIgnoreCase))
            {
                __result = typeof(ConditionweaponDurability);
                return false;
            }

            return true;
        }
    }
}
