using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YetAnotherTraderMod.config;

public class SettingsConfig
{
    public int MinLevel { get; set; } = 1;
    public bool UnlockedByDefault { get; set; } = false;

    public int TraderRefreshMin { get; set; } = 1800;
    public int TraderRefreshMax { get; set; } = 3600;

    public bool RerollAssortOnRestock { get; set; } = true;

    public bool AddTraderToFleaMarket { get; set; } = true;
    public bool EnableCustomQuests { get; set; } = true;
    public bool EnableCustomSideQuests { get; set; } = true;
    public int InsurancePriceCoef { get; set; } = 25;
    public double RepairQuality { get; set; } = 0.8;

    public bool RandomizeStockAvailable { get; set; } = true;
    public int OutOfStockChance { get; set; } = 15;

    public bool PreventBarterOffersOutOfStock { get; set; } = true;
    public bool UnlimitedStock { get; set; } = false;
    public double PriceMultiplier { get; set; } = 1.0;

    public bool CashOffersOnly { get; set; } = false;
    public bool RandomizeCashBarterOffers { get; set; } = true;
    public int CashOfferPercent { get; set; } = 85;

    // If false, Tony ignores hard-written BarterScheme rows from manual_offers.jsonc/addon price rules
    // and uses generated barter recipes instead. Paired ammo metadata is still kept.
    // If true, Tony uses hard-written BarterScheme rows from manual_offers.jsonc / offer files.
    public bool ManualBarters { get; set; } = false;

    // Generated/addon offer pricing. If an addon offer has no hard-written PriceConfig
    // or PriceConfig.Price is 0, Tony can price it from the game database.
    [JsonIgnore]
    public bool AutoPriceGeneratedOffers { get; set; } = true;

    // Caps the generated offer root StackObjectsCount when the offer definition does not
    // intentionally set its own lower amount. 0 = do not cap.
    [JsonIgnore]
    public int GeneratedOfferMaxStackObjectsCount { get; set; } = 30;

    // Caps the generated offer BuyRestrictionMax when the offer definition does not
    // intentionally set its own lower amount. 0 = do not cap.
    [JsonIgnore]
    public int GeneratedOfferMaxBuyRestrictionMax { get; set; } = 5;

    // Generated prices can be slightly under or over the database value.
    // Accepted values: "Exact", "Under", "Over".
    [JsonIgnore]
    public string GeneratedOfferPriceMode { get; set; } = "Exact";

    // Percent used by GeneratedOfferPriceMode. Example: 10 means 10% under/over.
    [JsonIgnore]
    public int GeneratedOfferPriceOffsetPercent { get; set; } = 0;

    // If true, generated pricing and generated barters can read external flea price JSON files
    // such as DrakiaXYZ LiveFleaPrices config/prices-regular.json or prices-pve.json.
    [JsonIgnore]
    public bool GeneratedPriceUseExternalFleaPriceFiles { get; set; } = true;

    // regular = prices-regular.json, pve = prices-pve.json.
    [JsonIgnore]
    public string GeneratedPriceExternalFleaGameMode { get; set; } = "regular";

    // If true, Tony scans sibling mod config folders for prices-regular.json/prices-pve.json.
    [JsonIgnore]
    public bool GeneratedPriceScanSiblingModsForFleaPriceFiles { get; set; } = true;

    // Optional explicit paths relative to Tony's mod folder, or absolute paths.
    [JsonIgnore]
    public List<string> GeneratedPriceExternalFleaPriceFilePaths { get; set; } = [];

    // If true, external flea price files win over SPT Templates.Prices / handbook prices.
    [JsonIgnore]
    public bool GeneratedPricePreferExternalFleaPrices { get; set; } = true;

    // If true, log where external flea prices were loaded from.
    [JsonIgnore]
    public bool GeneratedPriceLogExternalFleaPriceSource { get; set; } = false;


    // Generated barter value mode. Accepted values: "Exact", "Under", "Over".
    // Exact/Under never exceed the target value. Over is the only mode allowed to exceed it.
    [JsonIgnore]
    public string GeneratedBarterPriceMode { get; set; } = "Exact";

    // Controls whether generated barter item picks are stable or rerolled.
    // Accepted values: "Stable" and "Random".
    // Stable = same offer/value tends to generate the same barter recipe.
    // Random = generated barter recipes are rerolled each startup/restock and then saved to GeneratedBarterOutputPath.
    [JsonIgnore]
    public string GeneratedBarterSelectionMode { get; set; } = "Stable";

    // Percent used by GeneratedBarterPriceMode. Example: Under 10 = target 90%, Over 10 = target 110%.
    [JsonIgnore]
    public int GeneratedBarterPriceOffsetPercent { get; set; } = 0;

    // Fallback maximum number of different barter item types in one generated barter option.
    // Category-specific limits below override this when the sold item can be classified.
    [JsonIgnore]
    public int GeneratedBarterMaxDifferentItems { get; set; } = 3;

    // Category-specific max different item types for generated barters.
    // Ammo here means paired ammo pack barter offers, not loose cash ammo.
    [JsonIgnore]
    public int GeneratedBarterAmmoPackMaxDifferentItems { get; set; } = 1;
    [JsonIgnore]
    public int GeneratedBarterWeaponMaxDifferentItems { get; set; } = 2;
    [JsonIgnore]
    public int GeneratedBarterMedicalMaxDifferentItems { get; set; } = 2;
    [JsonIgnore]
    public int GeneratedBarterHeadwearMaxDifferentItems { get; set; } = 2;
    [JsonIgnore]
    public int GeneratedBarterArmorMaxDifferentItems { get; set; } = 3;
    [JsonIgnore]
    public int GeneratedBarterCaseValuableMaxDifferentItems { get; set; } = 4;
    [JsonIgnore]
    public int GeneratedBarterDefaultMaxDifferentItems { get; set; } = 3;

    // If true, generated barter recipes prefer the least-used whitelist items first.
    // This prevents one cheap/efficient barter item from being overused across many offers.
    [JsonIgnore]
    public bool GeneratedBarterBalanceItemUsage { get; set; } = true;

    // If true, generated barter components are picked from weighted category pools.
    // Example: an Electronics offer can mostly use Electronics barter items, with some Tools/Valuables mixed in.
    [JsonIgnore]
    public bool GeneratedBarterUseWeightedCategories { get; set; } = true;

    // Default weight for whitelist items that do not define their own Weight. Higher = more likely.
    [JsonIgnore]
    public double GeneratedBarterDefaultItemWeight { get; set; } = 10;

    // Default hard cap per generated-barter run/restock. 0 = unlimited.
    [JsonIgnore]
    public int GeneratedBarterDefaultItemMaxUsesPerRestock { get; set; } = 0;

    // Soft anti-spam curve. Effective weight = weight / (1 + uses * penalty).
    [JsonIgnore]
    public double GeneratedBarterDefaultOverusePenalty { get; set; } = 0.45;

    // How Tony values barter component items. Default uses the middle ground between
    // flea-style price and the best trader sell price so generated barters are not inflated.
    // Accepted values: FleaTraderAverage, Flea, Trader.
    [JsonIgnore]
    public string GeneratedBarterComponentPriceSource { get; set; } = "FleaTraderAverage";

    // Maximum count for each generated barter component.
    [JsonIgnore]
    public int GeneratedBarterMaxItemCount { get; set; } = 5;

    // Minimum database/flea-style price for an item to be used as an auto-generated barter component.
    [JsonIgnore]
    public int GeneratedBarterMinItemPrice { get; set; } = 250;

    // If true, generated barters only use tpl IDs from generated_barter_whitelist.jsonc.
    [JsonIgnore]
    public bool GeneratedBarterUseWhitelist { get; set; } = true;

    // Path relative to the mod folder for the user-editable generated barter ingredient pool.
    [JsonIgnore]
    public string GeneratedBarterWhitelistPath { get; set; } = "config/generated_barter_whitelist.jsonc";

    // Path relative to the mod folder. Auto-generated barter recipes are saved here.
    // This keeps config/manual_offers.jsonc as the OG/manual YATM price file.
    [JsonIgnore]
    public string GeneratedBarterOutputPath { get; set; } = "db/Generated/generated_barters.jsonc";

    public bool DebugLogging { get; set; } = false;
    public bool RealDebugLogging { get; set; } = false;
}


public class GeneratedTradeConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = "Advanced settings for Tony generated addon offers and auto-generated barter recipes. Most users do not need to edit this.";

    public bool AutoPriceGeneratedOffers { get; set; } = true;
    public int GeneratedOfferMaxStackObjectsCount { get; set; } = 30;
    public int GeneratedOfferMaxBuyRestrictionMax { get; set; } = 5;
    public string GeneratedOfferPriceMode { get; set; } = "Exact";
    public int GeneratedOfferPriceOffsetPercent { get; set; } = 0;

    public bool GeneratedPriceUseExternalFleaPriceFiles { get; set; } = true;
    public string GeneratedPriceExternalFleaGameMode { get; set; } = "regular";
    public bool GeneratedPriceScanSiblingModsForFleaPriceFiles { get; set; } = true;
    public List<string> GeneratedPriceExternalFleaPriceFilePaths { get; set; } = [];
    public bool GeneratedPricePreferExternalFleaPrices { get; set; } = true;
    public bool GeneratedPriceLogExternalFleaPriceSource { get; set; } = false;


    public string GeneratedBarterPriceMode { get; set; } = "Exact";
    public string GeneratedBarterSelectionMode { get; set; } = "Stable";
    public int GeneratedBarterPriceOffsetPercent { get; set; } = 0;

    public int GeneratedBarterMaxDifferentItems { get; set; } = 3;
    public int GeneratedBarterAmmoPackMaxDifferentItems { get; set; } = 1;
    public int GeneratedBarterWeaponMaxDifferentItems { get; set; } = 2;
    public int GeneratedBarterMedicalMaxDifferentItems { get; set; } = 2;
    public int GeneratedBarterHeadwearMaxDifferentItems { get; set; } = 2;
    public int GeneratedBarterArmorMaxDifferentItems { get; set; } = 3;
    public int GeneratedBarterCaseValuableMaxDifferentItems { get; set; } = 4;
    public int GeneratedBarterDefaultMaxDifferentItems { get; set; } = 3;
    public bool GeneratedBarterBalanceItemUsage { get; set; } = true;
    public bool GeneratedBarterUseWeightedCategories { get; set; } = true;
    public double GeneratedBarterDefaultItemWeight { get; set; } = 10;
    public int GeneratedBarterDefaultItemMaxUsesPerRestock { get; set; } = 0;
    public double GeneratedBarterDefaultOverusePenalty { get; set; } = 0.45;
    public string GeneratedBarterComponentPriceSource { get; set; } = "FleaTraderAverage";

    public int GeneratedBarterMaxItemCount { get; set; } = 5;
    public int GeneratedBarterMinItemPrice { get; set; } = 250;

    public bool GeneratedBarterUseWhitelist { get; set; } = true;
    public string GeneratedBarterWhitelistPath { get; set; } = "config/generated_barter_whitelist.jsonc";
    public string GeneratedBarterOutputPath { get; set; } = "db/Generated/generated_barters.jsonc";
}

public class PriceConfigItem
{
    public string? OfferId { get; set; }
    [JsonIgnore]
    public string? PackOfferId { get; set; }
    public string TplId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public double Price { get; set; }
    public string Currency { get; set; } = "RUB";
    public bool CashOnly { get; set; } = true;
    public bool AlwaysBarter { get; set; } = false;
    public bool AlwaysInStock { get; set; } = false;
    public List<List<PaymentConfigItem>>? BarterScheme { get; set; }
    [JsonIgnore]
    public string? AmmoBarterPackTplId { get; set; }
    [JsonIgnore]
    public string? AmmoBarterPackItemName { get; set; }
    [JsonIgnore]
    public int AmmoBarterPackSize { get; set; } = 0;
    [JsonIgnore]
    public string BarterSchemeValueBasis { get; set; } = "Unit";


    // Optional per-row override for generated barter offer category/profile.
    // Accepted values include AmmoPack, Weapon, Medical, Headwear, Armor, CaseOrValuable,
    // Electronics, Tools, FoodDrink, WeaponParts, Valuables, Junk, and Default.
    // Leave empty to let Tony infer the category from the sold item template/name.
    [JsonIgnore]
    public string? GeneratedBarterCategoryOverride { get; set; }

    // True when this row's barter recipe came from the auto-barter generator.
    // Auto-generated rows are saved to db/Generated/generated_barters.jsonc, not config/manual_offers.jsonc.
    [JsonIgnore]
    public bool AutoGeneratedBarter { get; set; } = false;
}

public class PaymentConfigItem
{
    public string TplId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public double Count { get; set; } = 1;
}

public class GeneratedBarterWhitelistConfig
{
    public int SchemaVersion { get; set; } = 2;
    public string Description { get; set; } = "Only these tpl IDs can be used by Tony auto-generated barters when GeneratedBarterUseWhitelist is true.";

    // Optional ingredient category settings. These control hard caps and anti-spam per restock.
    // Keys are normalized names like Electronics, Tools, FoodDrink, Medical, Valuables, Junk.
    public Dictionary<string, GeneratedBarterCategoryConfig> Categories { get; set; } = [];

    // Optional sold-offer category/profile settings. Example: Electronics offers can pull mostly
    // Electronics ingredients, with a smaller chance of Tools or Valuables.
    public Dictionary<string, GeneratedBarterOfferCategoryProfile> OfferCategoryProfiles { get; set; } = [];

    public List<GeneratedBarterWhitelistItem> Items { get; set; } = [];
}

public class GeneratedBarterCategoryConfig
{
    public bool Enabled { get; set; } = true;

    // 0 = unlimited. Counts total component quantity used across generated barter recipes this run/restock.
    public int MaxUsesPerRestock { get; set; } = 0;

    // Soft anti-spam curve for items in this category. Effective weight = weight / (1 + uses * penalty).
    public double OverusePenalty { get; set; } = 0.45;
}

public class GeneratedBarterOfferCategoryProfile
{
    public bool Enabled { get; set; } = true;

    // Category weights for barter components used when this offer category becomes barter.
    // Example: { "Electronics": 70, "Tools": 20, "Valuables": 10 }
    public Dictionary<string, double> IngredientCategoryWeights { get; set; } = [];

    // Optional override for max different barter item types. 0/null = use generated_trade_settings.jsonc category limit.
    public int? MaxDifferentItems { get; set; }
}

public class GeneratedBarterWhitelistItem
{
    public bool Enabled { get; set; } = true;
    public string TplId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;

    // Ingredient category used by the weighted category system. If empty/Default, Tony infers it from Notes/name.
    public string Category { get; set; } = "Default";

    // Higher = picked more often inside the category. Use caps/penalties to prevent spam.
    public double Weight { get; set; } = 10;

    // 0 = use generated_trade_settings.jsonc default. Counts total component quantity used this run/restock.
    public int MaxUsesPerRestock { get; set; } = 0;

    // Quantity clamps for this component when it is selected. MaxQty 0 = use global GeneratedBarterMaxItemCount.
    public int MinQty { get; set; } = 1;
    public int MaxQty { get; set; } = 0;

    public string Notes { get; set; } = string.Empty;
}
