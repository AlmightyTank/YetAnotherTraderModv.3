using System.Text.Json.Nodes;
using YetAnotherTraderMod.config;

namespace YetAnotherTraderMod.src.GeneratedOffers;

/// <summary>
/// Current YATM addon offer schema.
/// Files in db/CustomTraderOffers/*.json/jsonc must be shaped as:
/// { "66a0f6b2c4d8e90123456789": { "items": [], "barter_scheme": {}, "loyal_level_items": {}, "yatm_settings": {} } }
/// </summary>
public sealed class YATMRawTraderOfferFile
{
    public string SourceFile { get; set; } = string.Empty;
    public JsonArray Items { get; set; } = [];
    public JsonObject BarterScheme { get; set; } = [];
    public JsonObject LoyalLevelItems { get; set; } = [];
    public JsonObject YatmSettings { get; set; } = [];
}

// Kept as an internal DTO only for older compiled addons that may still reference the type.
// The active loader no longer reads SchemaVersion + Offers[] files.
public sealed class YATMOfferDatabaseFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<YATMGeneratedOfferDefinition> Offers { get; set; } = [];
}

public sealed class YATMGeneratedOfferDefinition
{
    public bool Enabled { get; set; } = true;
    public string OfferKey { get; set; } = string.Empty;
    public string TraderId { get; set; } = "66a0f6b2c4d8e90123456789";
    public bool GenerateAssortOffer { get; set; } = true;
    public string? PresetId { get; set; }
    public string? TplId { get; set; }
    public string OfferId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int LoyaltyLevel { get; set; } = 1;
    public int StackObjectsCount { get; set; } = 1;
    public bool UnlimitedCount { get; set; } = false;
    public int BuyRestrictionMax { get; set; } = 0;
    public PriceConfigItem? PriceConfig { get; set; }
}
