using YetAnotherTraderMod.Client.Models;

namespace YetAnotherTraderMod.Client.Services
{
    public static class WeaponDurabilityConditionEvaluator
    {
        public static bool Passes(ConditionweaponDurability condition, float currentDurability)
        {
            if (condition == null)
            {
                return true;
            }

            var result = condition.IsValid(currentDurability);

            Plugin.LogSource.LogInfo(
                "[YATM Quest Conditions] Evaluated weaponDurability " +
                condition.id +
                ": current=" +
                currentDurability +
                " rule=" +
                condition.compareMethod +
                " " +
                condition.value +
                " result=" +
                result
            );

            return result;
        }
    }
}