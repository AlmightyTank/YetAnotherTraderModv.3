using System.Text.Json.Serialization;

namespace YetAnotherTraderMod.src.Models;

/// <summary>
/// YATM-only extension data read from WTT custom-item JSON files.
/// Unknown WTT properties are intentionally ignored by System.Text.Json.
/// </summary>
public sealed class YATMItemModificationRequest
{
    [JsonIgnore]
    public string ItemId { get; set; } = string.Empty;

    [JsonIgnore]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("copySlot")]
    public bool CopySlot { get; set; }

    [JsonPropertyName("copySlotsInfo")]
    public List<CopySlotConfig> CopySlots { get; set; } = [];

    [JsonPropertyName("addToQuestAssorts")]
    public bool AddToQuestAssorts { get; set; }

    [JsonPropertyName("questAssorts")]
    public List<YATMQuestAssortConfig> QuestAssorts { get; set; } = [];

    [JsonPropertyName("addToQuestRewards")]
    public bool AddToQuestRewards { get; set; }

    [JsonPropertyName("questRewards")]
    public List<YATMQuestRewardConfig> QuestRewards { get; set; } = [];

    /// <summary>
    /// Existing WTT gate shared by all trader additions.
    /// </summary>
    [JsonPropertyName("addToTraders")]
    public bool AddToTraders { get; set; }

    /// <summary>
    /// Parsed so deferred quest processing can resolve a WTT preset offer whose
    /// final root id differs from the source assort id in the JSON.
    /// </summary>
    [JsonPropertyName("presetTraders")]
    public Dictionary<string, Dictionary<string, YATMPresetTraderConfig>> PresetTraders { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Full attached weapon offers that do not require registration as global presets.
    /// These entries are processed only when addToTraders is true.
    /// Each entry references a reusable build from db/CustomWeaponBuilds.
    /// </summary>
    [JsonPropertyName("weaponBuildTraders")]
    public Dictionary<string, Dictionary<string, YATMWeaponBuildTraderConfig>> WeaponBuildTraders { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

}

public sealed class CopySlotConfig
{
    /// <summary>
    /// Source template item containing the slot to copy.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Name the copied slot will have on the target item.
    /// When tgtSlotName is omitted, this is also the source slot name.
    /// </summary>
    [JsonPropertyName("newSlotName")]
    public string NewSlotName { get; set; } = string.Empty;

    /// <summary>
    /// Optional source slot name when it differs from newSlotName.
    /// </summary>
    [JsonPropertyName("tgtSlotName")]
    public string? TgtSlotName { get; set; }

    /// <summary>
    /// Optional item template ids to append to the copied slot's first filter.
    /// </summary>
    [JsonPropertyName("itemsAddToSlot")]
    public string[] ItemsAddToSlot { get; set; } = [];

    /// <summary>
    /// Optional override for the copied slot's Required value.
    /// </summary>
    [JsonPropertyName("required")]
    public bool? Required { get; set; }
}

public sealed class YATMQuestAssortConfig
{
    [JsonPropertyName("traderId")]
    public string? TraderId { get; set; }

    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = string.Empty;

    [JsonPropertyName("assortId")]
    public string? AssortId { get; set; }

    [JsonPropertyName("presetId")]
    public string? PresetId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class YATMQuestRewardConfig
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = string.Empty;

    [JsonPropertyName("rewardType")]
    public string RewardType { get; set; } = "Item";

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("findInRaid")]
    public bool FindInRaid { get; set; }

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; set; }

    [JsonPropertyName("currencyTpl")]
    public string? CurrencyTpl { get; set; }

    [JsonPropertyName("presetId")]
    public string? PresetId { get; set; }

    /// <summary>
    /// Reusable full weapon tree from db/CustomWeaponBuilds.
    /// For rewardType Weapon this is preferred over presetId.
    /// </summary>
    [JsonPropertyName("weaponBuildId")]
    public string? WeaponBuildId { get; set; }

    [JsonPropertyName("traderId")]
    public string? TraderId { get; set; }

    [JsonPropertyName("assortId")]
    public string? AssortId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class YATMWeaponBuildTraderConfig
{
    [JsonPropertyName("weaponBuildId")]
    public string WeaponBuildId { get; set; } = string.Empty;

    [JsonPropertyName("barterSettings")]
    public YATMPresetBarterSettings BarterSettings { get; set; } = new();

    [JsonPropertyName("barters")]
    public List<YATMBarterComponent> Barters { get; set; } = [];
}

public sealed class YATMPresetTraderConfig
{
    [JsonPropertyName("presetId")]
    public string PresetId { get; set; } = string.Empty;

    [JsonPropertyName("barterSettings")]
    public YATMPresetBarterSettings BarterSettings { get; set; } = new();

    [JsonPropertyName("barters")]
    public List<YATMBarterComponent> Barters { get; set; } = [];
}

public sealed class YATMPresetBarterSettings
{
    [JsonPropertyName("loyalLevel")]
    public int LoyalLevel { get; set; } = 1;

    [JsonPropertyName("unlimitedCount")]
    public bool UnlimitedCount { get; set; }

    [JsonPropertyName("stackObjectsCount")]
    public int StackObjectsCount { get; set; } = 1;

    [JsonPropertyName("buyRestrictionMax")]
    public int? BuyRestrictionMax { get; set; }
}

public sealed class YATMBarterComponent
{
    [JsonPropertyName("_tpl")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public double Count { get; set; } = 1;
}
