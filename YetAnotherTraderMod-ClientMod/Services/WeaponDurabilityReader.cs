using System;
using System.Reflection;
using EFT.InventoryLogic;

namespace YetAnotherTraderMod.Client.Services
{
    public static class WeaponDurabilityReader
    {
        public static float? TryReadDurability(Item item)
        {
            if (item == null)
            {
                return null;
            }

            // Some versions expose Repairable directly.
            var direct = TryReadNestedFloat(item, "Repairable", "Durability")
                ?? TryReadNestedFloat(item, "Repairable", "CurrentDurability")
                ?? TryReadNestedFloat(item, "repairable", "Durability")
                ?? TryReadNestedFloat(item, "repairable", "CurrentDurability");

            if (direct.HasValue)
            {
                return direct.Value;
            }

            // Some versions store durability on a RepairableComponent.
            var repairableComponent = TryGetRepairableComponent(item);
            if (repairableComponent != null)
            {
                var componentDurability = TryReadFloat(repairableComponent, "Durability")
                    ?? TryReadFloat(repairableComponent, "CurrentDurability")
                    ?? TryReadFloat(repairableComponent, "Value");

                if (componentDurability.HasValue)
                {
                    return componentDurability.Value;
                }
            }

            return null;
        }

        private static object TryGetRepairableComponent(Item item)
        {
            try
            {
                var methods = item.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var method in methods)
                {
                    if (!method.IsGenericMethodDefinition || method.Name != "GetItemComponent")
                    {
                        continue;
                    }

                    var repairableType = FindType("EFT.InventoryLogic.RepairableComponent");
                    if (repairableType == null)
                    {
                        return null;
                    }

                    var generic = method.MakeGenericMethod(repairableType);
                    return generic.Invoke(item, null);
                }
            }
            catch
            {
            }

            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static float? TryReadNestedFloat(object obj, string objectName, string floatName)
        {
            var nested = ReflectionValueReader.TryReadObject(obj, objectName);
            if (nested == null)
            {
                return null;
            }

            return TryReadFloat(nested, floatName);
        }

        private static float? TryReadFloat(object obj, string name)
        {
            return ReflectionValueReader.TryReadFloat(obj, name);
        }
    }
}
