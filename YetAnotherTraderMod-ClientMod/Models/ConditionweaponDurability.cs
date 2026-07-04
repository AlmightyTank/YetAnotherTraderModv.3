using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.Quests;
using Newtonsoft.Json;

namespace YetAnotherTraderMod.Client.Models
{
    // The class name intentionally matches EFT's condition converter:
    // conditionType "weaponDurability" -> ConditionweaponDurability
    public sealed class ConditionweaponDurability : Condition
    {
        [JsonProperty("useCurrentDurability")]
        public bool useCurrentDurability = true;

        // Keep identity simple. Runtime sends:
        // new GStruct458(typeof(ConditionweaponDurability)).Test(currentDurability)
        // If id/value/compareMethod are added here, the identity will not match.
        public override List<object> IdentityFields()
        {
            return base.IdentityFields();
        }

        public override string FormattedDescription
        {
            get
            {
                return "Weapon durability " + GetCompareMethod() + " " + GetRequiredValue();
            }
        }

        public bool IsValid(float currentDurability)
        {
            return Services.WeaponDurabilityCompare.Passes(
                currentDurability,
                GetCompareMethod(),
                GetRequiredValue()
            );
        }

        public string GetCompareMethod()
        {
            return TryReadString(this, "compareMethod") ?? "<=";
        }

        public float GetRequiredValue()
        {
            return TryReadFloat(this, "value") ?? 60f;
        }

        private static string TryReadString(object obj, string name)
        {
            var value = TryReadObject(obj, name);
            return value == null ? null : value.ToString();
        }

        private static float? TryReadFloat(object obj, string name)
        {
            var value = TryReadObject(obj, name);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return null;
            }
        }

        private static object TryReadObject(object obj, string name)
        {
            if (obj == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = obj.GetType();

            while (type != null)
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null)
                {
                    return prop.GetValue(obj, null);
                }

                var field = type.GetField(name, flags);
                if (field != null)
                {
                    return field.GetValue(obj);
                }

                type = type.BaseType;
            }

            return null;
        }
    }
}
