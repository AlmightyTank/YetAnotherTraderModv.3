using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Quests;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using YetAnotherTraderMod.Client.Models;
using YetAnotherTraderMod.Client.Services;

namespace YetAnotherTraderMod.Client.Patches
{
    internal class KillConditionDurabilityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(GClass3999<QuestClass>),
                nameof(GClass3999<QuestClass>.CheckKillConditionCounter)
            );
        }

        [PatchPrefix]
        private static bool Prefix(
            GClass3999<QuestClass> __instance,
            string target,
            string enemyProfileId,
            List<string> targetEquipment,
            Item weapon,
            EBodyPart bodyPart,
            string locationId,
            float distance,
            string role,
            int hour,
            HealthEffects enemyEffects,
            HealthEffects effects,
            List<string> zoneIds,
            string[] buffs)
        {
            var killData = __instance.method_2(
                target,
                targetEquipment,
                weapon,
                bodyPart,
                distance,
                role,
                hour,
                enemyEffects
            );

            if (killData == null)
            {
                return false;
            }

            var durability = WeaponDurabilityReader.TryReadDurability(weapon) ?? 999f;

            Plugin.LogSource.LogInfo(
                "[YATM Quest Conditions] Kill counter check. Weapon durability: " + durability
            );

            var normalChecks = new GStruct458[]
            {
                new GStruct458(new object[] { typeof(ConditionKills) }).Test(killData),
                new GStruct458(new object[] { typeof(ConditionLocation) }).Test(locationId),
                new GStruct458(new object[] { typeof(ConditionEquipment) }).Test(__instance.Profile.Inventory),
                new GStruct458(new object[] { typeof(ConditionHealthEffect) }).Test(effects),
                new GStruct458(new object[] { typeof(ConditionInZone) }).Test(zoneIds),
                new GStruct458(new object[] { typeof(ConditionTime) }).Test(GClass1891.PastTime),
                new GStruct458(new object[] { typeof(ConditionHealthBuff) }).Test(buffs)
            };

            var removedCounters = RemoveDurabilityGatedCounters(__instance);

            try
            {
                // Native EFT kill processing runs with durability-gated counters removed.
                // This prevents a full-dura kill from preloading the normal ConditionKills state.
                __instance.ConditionalBook.TestConditions(1, normalChecks);

                // Now manually process only counters that have a bound weaponDurability rule.
                foreach (var entry in removedCounters)
                {
                    var counter = entry.Counter;
                    var counterCreator = counter.Template as ConditionCounterCreator;

                    if (counterCreator == null)
                    {
                        continue;
                    }

                    if (!WeaponDurabilityRules.TryGetByCounterCreatorId(counterCreator.id, out var rule))
                    {
                        continue;
                    }

                    if (!WeaponDurabilityCompare.Passes(durability, rule.CompareMethod, rule.Value))
                    {
                        Plugin.LogSource.LogInfo(
                            "[YATM Quest Conditions] Rejected durability-gated counter " +
                            counterCreator.id +
                            ". Durability=" +
                            durability +
                            " rule=" +
                            rule.CompareMethod +
                            " " +
                            rule.Value
                        );

                        continue;
                    }

                    Plugin.LogSource.LogInfo(
                        "[YATM Quest Conditions] Allowing durability-gated counter " +
                        counterCreator.id +
                        ". Durability=" +
                        durability +
                        " rule=" +
                        rule.CompareMethod +
                        " " +
                        rule.Value
                    );

                    ConditionweaponDurability durabilityCondition;
                    var hadDurabilityCondition = TryGetDurabilityCondition(
                        counterCreator,
                        out durabilityCondition
                    );

                    if (hadDurabilityCondition)
                    {
                        RemoveCondition(counterCreator.Conditions, durabilityCondition);
                    }

                    try
                    {
                        ConditionCounterManager.smethod_0(1, counter, normalChecks);
                    }
                    finally
                    {
                        if (hadDurabilityCondition)
                        {
                            AddCondition(counterCreator.Conditions, durabilityCondition);
                        }
                    }
                }
            }
            finally
            {
                RestoreDurabilityGatedCounters(removedCounters);
            }

            return false;
        }

        private sealed class RemovedCounterEntry
        {
            public ConditionCounterManager Manager;
            public TaskConditionCounterClass Counter;
        }

        private static List<RemovedCounterEntry> RemoveDurabilityGatedCounters(
            GClass3999<QuestClass> controller)
        {
            var removed = new List<RemovedCounterEntry>();

            foreach (var conditional in controller.ConditionalBook)
            {
                if (conditional == null || conditional.ConditionCountersManager == null)
                {
                    continue;
                }

                var manager = conditional.ConditionCountersManager;

                foreach (var counter in manager.Counters.ToArray())
                {
                    var counterCreator = counter.Template as ConditionCounterCreator;

                    if (counterCreator == null)
                    {
                        continue;
                    }

                    WeaponDurabilityRule rule;
                    if (!WeaponDurabilityRules.TryGetByCounterCreatorId(counterCreator.id, out rule))
                    {
                        continue;
                    }

                    manager.Counters.Remove(counter);

                    removed.Add(new RemovedCounterEntry
                    {
                        Manager = manager,
                        Counter = counter
                    });

                    Plugin.LogSource.LogInfo(
                        "[YATM Quest Conditions] Removed durability-gated counter " +
                        counterCreator.id +
                        " from native kill processing. Rule=" +
                        rule.CompareMethod +
                        " " +
                        rule.Value
                    );
                }
            }

            return removed;
        }

        private static void RestoreDurabilityGatedCounters(List<RemovedCounterEntry> removed)
        {
            foreach (var entry in removed)
            {
                if (entry.Manager == null || entry.Counter == null)
                {
                    continue;
                }

                if (!entry.Manager.Counters.Contains(entry.Counter))
                {
                    entry.Manager.Counters.Add(entry.Counter);
                }
            }
        }

        private static bool TryGetDurabilityCondition(
            ConditionCounterCreator counterCreator,
            out ConditionweaponDurability durabilityCondition)
        {
            durabilityCondition = null;

            if (counterCreator == null || counterCreator.Conditions == null)
            {
                return false;
            }

            foreach (Condition condition in counterCreator.Conditions)
            {
                durabilityCondition = condition as ConditionweaponDurability;

                if (durabilityCondition != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveCondition(object conditions, Condition condition)
        {
            var list = conditions as IList<Condition>;

            if (list != null)
            {
                list.Remove(condition);
            }
        }

        private static void AddCondition(object conditions, Condition condition)
        {
            var list = conditions as IList<Condition>;

            if (list != null && !list.Contains(condition))
            {
                list.Add(condition);
            }
        }
    }
}