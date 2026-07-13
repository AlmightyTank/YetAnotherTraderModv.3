using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherTraderMod.src.Features.CustomConsumables;

public sealed class CustomConsumableDefinition
{
    [JsonPropertyName("cloneOrigin")]
    public string CloneOrigin { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// true = use cloneOrigin flea price.
    /// number <= 10 = multiplier against cloneOrigin flea price.
    /// number > 10 = exact rouble value.
    /// </summary>
    [JsonPropertyName("fleaPrice")]
    public JsonElement FleaPrice { get; set; }

    /// <summary>
    /// "asOriginal" = use cloneOrigin handbook price.
    /// number <= 10 = multiplier against cloneOrigin flea price.
    /// number > 10 = exact rouble value.
    /// </summary>
    [JsonPropertyName("handBookPrice")]
    public JsonElement HandBookPrice { get; set; }

    /// <summary>
    /// Optional legacy exact flea price field. Prefer fleaPrice for new files.
    /// Used only when fleaPrice is omitted.
    /// </summary>
    [JsonPropertyName("fleaPriceRoubles")]
    public double? FleaPriceRoubles { get; set; }

    /// <summary>
    /// Optional legacy exact handbook price field. Prefer handBookPrice for new files.
    /// Used only when handBookPrice is omitted.
    /// </summary>
    [JsonPropertyName("handbookPriceRoubles")]
    public double? HandbookPriceRoubles { get; set; }

    [JsonPropertyName("includeInSameQuestsAsOrigin")]
    public bool IncludeInSameQuestsAsOrigin { get; set; }

    [JsonPropertyName("addSpawnsInSamePlacesAsOrigin")]
    public bool AddSpawnsInSamePlacesAsOrigin { get; set; }

    [JsonPropertyName("spawnWeightComparedToOrigin")]
    public double SpawnWeightComparedToOrigin { get; set; } = 1;

    /// <summary>
    /// true by default. This copies the origin stim buff list first, then appends Buffs.
    /// For Volkov Hemostat, this means cloning Zagustin keeps Zagustin bleed control,
    /// then we append the Propital-style regen/crash effects.
    /// </summary>
    [JsonPropertyName("inheritOriginBuffs")]
    public bool InheritOriginBuffs { get; set; } = true;

    /// <summary>
    /// Controls whether the loader creates a custom StimulatorBuffs set for this item.
    /// null (default): create one only when Buffs contains entries.
    /// true: always create one, optionally inheriting the origin set.
    /// false: clear StimulatorBuffs and do not register a custom set.
    /// </summary>
    [JsonPropertyName("useStimulatorBuffs")]
    public bool? UseStimulatorBuffs { get; set; }

    [JsonPropertyName("Buffs")]
    public List<JsonElement>? Buffs { get; set; }

    [JsonPropertyName("locales")]
    public Dictionary<string, CustomConsumableLocale> Locales { get; set; } = new();

    [JsonPropertyName("trader")]
    public CustomConsumableTrader? Trader { get; set; }

    /// <summary>
    /// Optional multiple trader offers for the same custom consumable.
    /// Keep using trader for the common one-off case.
    /// </summary>
    [JsonPropertyName("traders")]
    public List<CustomConsumableTrader>? Traders { get; set; }

    [JsonPropertyName("craft")]
    public JsonElement? Craft { get; set; }

    /// <summary>
    /// Optional multiple hideout recipes for the same custom consumable.
    /// Keep using craft for the common one-off case.
    /// </summary>
    [JsonPropertyName("crafts")]
    public List<JsonElement>? Crafts { get; set; }

    /// <summary>
    /// Optional explicit item property overrides. Keys should match TemplateItemProperties names, usually PascalCase.
    /// Example: { "CanSellOnRagfair": true, "BackgroundColor": "red", "medUseTime": 2 }.
    /// </summary>
    [JsonPropertyName("overrideProperties")]
    public Dictionary<string, JsonElement>? OverrideProperties { get; set; }

    /// <summary>
    /// Safety escape hatch for a deliberate patch of an item template that another loader already created.
    /// False by default so an ID collision cannot silently mutate another mod's item.
    /// </summary>
    [JsonPropertyName("allowExistingItem")]
    public bool AllowExistingItem { get; set; }

    /// <summary>
    /// Retained for compatibility with older ConsumablesGalore-style files.
    /// The current YATM loader does not add templates to a global item blacklist.
    /// </summary>
    [JsonPropertyName("addtoItemBlacklist")]
    public bool? AddToItemBlacklist { get; set; }

    /// <summary>
    /// Backward-compatible top-level item property overrides. Prefer overrideProperties for new files.
    /// Supported common aliases are also handled in the loader:
    /// BackgroundColor, effects_health, effects_damage, MaxResource, medUseTime, Prefab, UsePrefab, ItemSound.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraProperties { get; set; }
}

public sealed class CustomConsumableLocale
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class CustomConsumableTrader
{
    [JsonPropertyName("traderId")]
    public string TraderId { get; set; } = string.Empty;

    [JsonPropertyName("loyaltyReq")]
    public int LoyaltyReq { get; set; } = 1;

    [JsonPropertyName("price")]
    public double Price { get; set; } = 1;

    [JsonPropertyName("currencyTpl")]
    public string CurrencyTpl { get; set; } = "5449016a4bdc2d6f028b456f";

    [JsonPropertyName("amountForSale")]
    public int AmountForSale { get; set; } = 1;

    [JsonPropertyName("unlimitedCount")]
    public bool UnlimitedCount { get; set; }

    [JsonPropertyName("buyRestrictionMax")]
    public int? BuyRestrictionMax { get; set; }

    /// <summary>
    /// Optional trader offer id. If omitted, the loader uses the custom item id.
    /// Use this when you want the assort offer id to differ from the item tpl id.
    /// </summary>
    [JsonPropertyName("assortmentId")]
    public string? AssortmentId { get; set; }

    /// <summary>
    /// Optional direct barter scheme. If present, this overrides price/currencyTpl.
    /// Shape matches SPT assort barter_scheme value: [[{ "count": 1, "_tpl": "..." }]]
    /// </summary>
    [JsonPropertyName("barterScheme")]
    public JsonElement? BarterScheme { get; set; }

    /// <summary>
    /// Same as barterScheme, but supports the vanilla assort JSON name.
    /// </summary>
    [JsonPropertyName("barter_scheme")]
    public JsonElement? BarterSchemeSnake { get; set; }

    /// <summary>
    /// Optional Tony runtime control. By default Tony CustomConsumables are rollable,
    /// so the cash/barter system may convert the offer and the stock system may zero it out.
    /// Set cashOnly true to opt out of barter rolling.
    /// </summary>
    [JsonPropertyName("cashOnly")]
    public bool? CashOnly { get; set; }

    /// <summary>
    /// Optional Tony runtime control. Set true to force this Tony offer to barter when a usable
    /// hard-written or generated barter scheme exists.
    /// </summary>
    [JsonPropertyName("alwaysBarter")]
    public bool? AlwaysBarter { get; set; }

    /// <summary>
    /// Optional Tony runtime control. Set true to keep this offer out of the random out-of-stock pool.
    /// </summary>
    [JsonPropertyName("alwaysInStock")]
    public bool? AlwaysInStock { get; set; }

}
