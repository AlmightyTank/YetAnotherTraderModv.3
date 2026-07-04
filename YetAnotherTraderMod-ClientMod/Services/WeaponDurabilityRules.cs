using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using YetAnotherTraderMod.Client.Models;

namespace YetAnotherTraderMod.Client.Services
{
    public static class WeaponDurabilityRules
    {
        private const string FileName = "settings.json";
        private static readonly Dictionary<string, WeaponDurabilityRule> Rules = new Dictionary<string, WeaponDurabilityRule>();

        public static void Load(ManualLogSource logger)
        {
            var path = PluginPathService.GetPluginFilePath(FileName);

            if (!File.Exists(path))
            {
                var config = CreateDefaultConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonFileUtil.Serialize(config));
                logger.LogWarning("[YATM Quest Conditions] Created default config: " + path);
            }

            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonFileUtil.Deserialize<WeaponDurabilityConfig>(json) ?? new WeaponDurabilityConfig();

                Rules.Clear();

                if (loaded.Rules != null)
                {
                    foreach (var pair in loaded.Rules)
                    {
                        if (pair.Value != null && pair.Value.Enabled)
                        {
                            Rules[pair.Key] = pair.Value;
                        }
                    }
                }

                logger.LogInfo("[YATM Quest Conditions] Loaded " + Rules.Count + " configured weapon durability rule(s).");
                logger.LogInfo("[YATM Quest Conditions] Config path: " + path);
                logger.LogInfo("[YATM Quest Conditions] SPT root: " + PluginPathService.TryGetSptRoot());
            }
            catch (System.Exception ex)
            {
                logger.LogError("[YATM Quest Conditions] Failed to load config: " + ex.Message);
            }
        }

        public static void AddOrUpdateRule(string killConditionId, WeaponDurabilityRule rule)
        {
            if (string.IsNullOrWhiteSpace(killConditionId) || rule == null || !rule.Enabled)
            {
                return;
            }

            Rules[killConditionId] = rule;
        }

        public static bool TryGet(string conditionId, out WeaponDurabilityRule rule)
        {
            if (string.IsNullOrWhiteSpace(conditionId))
            {
                rule = null;
                return false;
            }

            return Rules.TryGetValue(conditionId, out rule) && rule != null && rule.Enabled;
        }

        private static WeaponDurabilityConfig CreateDefaultConfig()
        {
            var config = new WeaponDurabilityConfig();
            config.Rules["6a41d30482d8dd83f87b20fb"] = new WeaponDurabilityRule
            {
                Enabled = true,
                CompareMethod = "<=",
                Value = 60f,
                UseCurrentDurability = true,
                SourceConditionId = "6a41d30482d8dd83f87b20fd",
                BoundKillConditionId = "6a41d30482d8dd83f87b20fb"
            };
            return config;
        }

        public static WeaponDurabilityRule GetFirstActiveRule()
        {
            foreach (var pair in Rules)
            {
                if (pair.Value != null && pair.Value.Enabled)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static readonly Dictionary<string, WeaponDurabilityRule> RulesByCounterCreatorId = [];

        public static void AddOrUpdateCounterRule(string counterCreatorId, WeaponDurabilityRule rule)
        {
            if (string.IsNullOrWhiteSpace(counterCreatorId) || rule == null)
            {
                return;
            }

            RulesByCounterCreatorId[counterCreatorId] = rule;
        }

        public static bool TryGetByCounterCreatorId(string counterCreatorId, out WeaponDurabilityRule rule)
        {
            if (string.IsNullOrWhiteSpace(counterCreatorId))
            {
                rule = null;
                return false;
            }

            return RulesByCounterCreatorId.TryGetValue(counterCreatorId, out rule)
                && rule != null
                && rule.Enabled;
        }
    }
}
