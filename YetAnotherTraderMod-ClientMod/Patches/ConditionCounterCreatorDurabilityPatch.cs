using EFT.Quests;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using YetAnotherTraderMod.Client;
using YetAnotherTraderMod.Client.Models;
using YetAnotherTraderMod.Client.Services;

namespace YetAnotherTraderMod.Client.Patches
{
    internal class ConditionCounterCreatorDurabilityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(ConditionCounterCreator),
                nameof(ConditionCounterCreator.OnDeserializedMethod)
            );
        }

        [PatchPostfix]
        private static void Postfix(ConditionCounterCreator __instance)
        {
            if (__instance == null || __instance.Conditions == null)
            {
                return;
            }

            ConditionKills killCondition = null;
            ConditionweaponDurability durabilityCondition = null;

            foreach (Condition condition in __instance.Conditions)
            {
                if (killCondition == null)
                {
                    killCondition = condition as ConditionKills;
                }

                if (durabilityCondition == null)
                {
                    durabilityCondition = condition as ConditionweaponDurability;
                }
            }

            if (killCondition == null || durabilityCondition == null)
            {
                return;
            }

            var rule = new WeaponDurabilityRule
            {
                Enabled = true,
                BoundCounterCreatorId = __instance.id,
                BoundKillConditionId = killCondition.id,
                SourceConditionId = durabilityCondition.id,
                CompareMethod = durabilityCondition.GetCompareMethod(),
                Value = durabilityCondition.GetRequiredValue(),
                UseCurrentDurability = durabilityCondition.useCurrentDurability
            };

            WeaponDurabilityRules.AddOrUpdateCounterRule(__instance.id, rule);

            Plugin.LogSource.LogInfo(
                "[YATM Quest Conditions] Bound weaponDurability " +
                durabilityCondition.id +
                " to CounterCreator " +
                __instance.id +
                " / Kills " +
                killCondition.id +
                " rule=" +
                rule.CompareMethod +
                " " +
                rule.Value
            );
        }
    }
}