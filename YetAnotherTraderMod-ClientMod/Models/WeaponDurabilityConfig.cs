using System.Collections.Generic;

namespace YetAnotherTraderMod.Client.Models
{
    public sealed class WeaponDurabilityConfig
    {
        public Dictionary<string, WeaponDurabilityRule> Rules { get; set; } = new Dictionary<string, WeaponDurabilityRule>();
    }
}
