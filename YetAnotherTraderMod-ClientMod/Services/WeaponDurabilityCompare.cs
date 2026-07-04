using System;

namespace YetAnotherTraderMod.Client.Services
{
    public static class WeaponDurabilityCompare
    {
        public static bool Passes(float actual, string compareMethod, float required)
        {
            switch (compareMethod)
            {
                case "<":
                    return actual < required;
                case "<=":
                    return actual <= required;
                case ">":
                    return actual > required;
                case ">=":
                    return actual >= required;
                case "==":
                case "=":
                    return Math.Abs(actual - required) < 0.001f;
                case "!=":
                    return Math.Abs(actual - required) >= 0.001f;
                default:
                    return actual <= required;
            }
        }
    }
}
