using System;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using YetAnotherTraderMod.Client.Models;

namespace YetAnotherTraderMod.Client.Patches
{
    internal class ConditionTypeToKeyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GClass1871), nameof(GClass1871.TypeToKey));
        }

        [PatchPrefix]
        private static bool Prefix(Type serializedType, ref string __result)
        {
            if (serializedType == typeof(ConditionweaponDurability))
            {
                __result = "weaponDurability";
                return false;
            }

            return true;
        }
    }
}
