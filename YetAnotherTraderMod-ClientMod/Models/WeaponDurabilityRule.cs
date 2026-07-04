namespace YetAnotherTraderMod.Client.Models
{
    public sealed class WeaponDurabilityRule
    {
        public bool Enabled { get; set; } = true;
        public string CompareMethod { get; set; } = "<=";
        public float Value { get; set; } = 60f;
        public bool UseCurrentDurability { get; set; } = true;
        public string SourceConditionId { get; set; }
        public string BoundKillConditionId { get; set; }
        public string BoundCounterCreatorId { get; set; }
    }
}
