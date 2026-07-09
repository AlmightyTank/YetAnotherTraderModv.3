using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;
using YetAnotherTraderMod.src;
using YetAnotherTraderMod.src.Models;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.config;

public class YATMConfig
{
    private readonly string _modPath;
    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly string _pricesPath;
    private readonly string _generatedTradeSettingsPath;

    private readonly DatabaseServer _databaseServer;

    public SettingsConfig Settings { get; private set; } = new();
    public GeneratedTradeConfig GeneratedTrade { get; private set; } = new();
    public List<PriceConfigItem> Prices { get; private set; } = new();

    public YATMConfig(string modPath, DatabaseServer databaseServer)
    {
        _modPath = modPath;
        _databaseServer = databaseServer;
        _configDir = Path.Combine(_modPath, "config");
        _settingsPath = Path.Combine(_configDir, "settings.json");
        _pricesPath = Path.Combine(_configDir, "manual_offers.jsonc");
        _generatedTradeSettingsPath = Path.Combine(_configDir, "generated_trade_settings.jsonc");
    }

    // Helper to find Template ID robustly.
    public static string? GetTemplateId(object item)
    {
        try
        {
            var type = item.GetType();

            // Try properties.
            var props = new[] { "Template", "Tpl", "_tpl", "TemplateId" };
            foreach (var propName in props)
            {
                var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    return prop.GetValue(item)?.ToString();
                }
            }

            // Try fields.
            var fields = new[] { "Template", "Tpl", "_tpl", "TemplateId" };
            foreach (var fieldName in fields)
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    return field.GetValue(item)?.ToString();
                }
            }

            return ((dynamic)item).Id;
        }
        catch
        {
            return null;
        }
    }

    public static string CurrencyToTemplate(string? currency)
    {
        return (currency ?? "RUB").ToUpperInvariant() switch
        {
            "USD" => "5696686a4bdc2da3298b456a",
            "EUR" => "569668774bdc2da2298b4568",
            "RUB" => "5449016a4bdc2d6f028b456f",
            _ => "5449016a4bdc2d6f028b456f"
        };
    }

    public static string TemplateToCurrency(string? tpl)
    {
        return tpl switch
        {
            "5696686a4bdc2da3298b456a" => "USD",
            "569668774bdc2da2298b4568" => "EUR",
            "5449016a4bdc2d6f028b456f" => "RUB",
            _ => "OTHER"
        };
    }

    public static bool IsCurrencyTemplate(string? tpl)
    {
        return tpl == "5449016a4bdc2d6f028b456f"
            || tpl == "5696686a4bdc2da3298b456a"
            || tpl == "569668774bdc2da2298b4568";
    }

    public void LoadOrGenerate(TraderBase baseJson, TraderAssort assortJson)
    {
        LoadOrGenerateSettingsOnly(baseJson);
        LoadOrGeneratePricesOnly(assortJson);
    }

    public void LoadOrGenerateSettingsOnly(TraderBase baseJson)
    {
        if (!Directory.Exists(_configDir))
        {
            Directory.CreateDirectory(_configDir);
        }

        LoadOrGenerateSettings(baseJson);

        // Generated trade/barter tuning is user-facing again. Keep generated_trade_settings.jsonc
        // separate from manual_offers.jsonc so players can tune the generated system without
        // hand-maintaining every offer row.
        LoadOrGenerateGeneratedTradeSettings();
        ApplyGeneratedTradeSettingsToRuntimeSettings();
        YATMRuntimeConfig.Set(Settings);
    }

    public void LoadOrGeneratePricesOnly(TraderAssort assortJson)
    {
        if (!Directory.Exists(_configDir))
        {
            Directory.CreateDirectory(_configDir);
        }

        // config/manual_offers.jsonc is now manual-only. Runtime price rows are rebuilt from
        // Tony's live assort every startup/restock, then optional manual rows are merged on top.
        LoadOrGeneratePrices(assortJson);
    }

    private void LoadOrGenerateSettings(TraderBase baseJson)
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settingsFileMissingRerollAssortOnRestock =
                    !json.Contains("RerollAssortOnRestock", StringComparison.OrdinalIgnoreCase);
                var settingsFileMissingPreventBarterOffersOutOfStock =
                    !json.Contains("PreventBarterOffersOutOfStock", StringComparison.OrdinalIgnoreCase);
                var settingsFileHasOldGeneratedTradeSettings =
                    json.Contains("AutoPriceGeneratedOffers", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("GeneratedOffer", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("GeneratedBarter", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("MarketRepriceCashOffers", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("MarketCashPriceBlendMode", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("MarketWeaponCashPricePercent", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("MarketArmorCashPricePercent", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("MarketCashPriceCache", StringComparison.OrdinalIgnoreCase);

                if (settingsFileHasOldGeneratedTradeSettings && !File.Exists(_generatedTradeSettingsPath))
                {
                    try
                    {
                        var migratedGeneratedTrade = JsonSerializer.Deserialize<GeneratedTradeConfig>(json, CachedReadOptions);
                        if (migratedGeneratedTrade != null)
                        {
                            GeneratedTrade = migratedGeneratedTrade;
                            NormalizeGeneratedTradeSettings(true);
                        }
                    }
                    catch (Exception migrateEx)
                    {
                        YATMLogger.Log($"[Config] Failed to migrate generated trade settings out of settings.json: {migrateEx.Message}");
                    }
                }

                Settings = JsonSerializer.Deserialize<SettingsConfig>(json, CachedReadOptions) ?? new SettingsConfig();
                NormalizeSettings(settingsFileMissingRerollAssortOnRestock
                    || settingsFileMissingPreventBarterOffersOutOfStock
                    || settingsFileHasOldGeneratedTradeSettings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tony] Error loading settings.json: {ex.Message}");
                Settings = new SettingsConfig();
                NormalizeSettings();
            }
        }
        else
        {
            Settings = new SettingsConfig
            {
                MinLevel = baseJson.LoyaltyLevels?.FirstOrDefault()?.MinLevel ?? 1,
                UnlockedByDefault = baseJson.UnlockedByDefault ?? false,

                TraderRefreshMin = 1800,
                TraderRefreshMax = 3600,
                RerollAssortOnRestock = true,
                AddTraderToFleaMarket = true,
                InsurancePriceCoef = 25,
                RepairQuality = 0.8,

                RandomizeStockAvailable = true,
                OutOfStockChance = 15,
                PreventBarterOffersOutOfStock = true,
                UnlimitedStock = false,

                PriceMultiplier = 1.0,

                // false = allow custom barter recipes in manual_offers.jsonc.
                // true = force every configured offer to use Price + Currency only.
                CashOffersOnly = false,

                // If true, configured offers are randomly split between cash and barter.
                // CashOfferPercent = 85 means roughly 85% cash and 15% barter.
                RandomizeCashBarterOffers = true,
                CashOfferPercent = 85,

                ManualBarters = false,

                DebugLogging = false,
                RealDebugLogging = false
            };

            NormalizeSettings();
            SaveJson(_settingsPath, Settings);
        }
    }


    private void LoadOrGenerateGeneratedTradeSettings()
    {
        if (!Directory.Exists(_configDir))
        {
            Directory.CreateDirectory(_configDir);
        }

        if (File.Exists(_generatedTradeSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(_generatedTradeSettingsPath);
                GeneratedTrade = JsonSerializer.Deserialize<GeneratedTradeConfig>(json, CachedReadOptions) ?? new GeneratedTradeConfig();
                NormalizeGeneratedTradeSettings();
                return;
            }
            catch (Exception ex)
            {
                YATMLogger.Log($"[Config] Failed to load generated_trade_settings.jsonc: {ex.Message}. Regenerating defaults.");
            }
        }

        GeneratedTrade = TryMigrateGeneratedTradeSettingsFromOldSettingsFile() ?? new GeneratedTradeConfig();
        NormalizeGeneratedTradeSettings(true);
    }

    private GeneratedTradeConfig? TryMigrateGeneratedTradeSettingsFromOldSettingsFile()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            if (!json.Contains("GeneratedOffer", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("GeneratedBarter", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("AutoPriceGeneratedOffers", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("MarketRepriceCashOffers", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("MarketCashPriceBlendMode", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("MarketWeaponCashPricePercent", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("MarketArmorCashPricePercent", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("MarketCashPriceCache", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GeneratedTradeConfig>(json, CachedReadOptions);
        }
        catch
        {
            return null;
        }
    }

    private void NormalizeGeneratedTradeSettings(bool forceSave = false)
    {
        var changed = forceSave;

        GeneratedTrade.GeneratedOfferMaxStackObjectsCount = Math.Max(0, GeneratedTrade.GeneratedOfferMaxStackObjectsCount);
        GeneratedTrade.GeneratedOfferMaxBuyRestrictionMax = Math.Max(0, GeneratedTrade.GeneratedOfferMaxBuyRestrictionMax);
        GeneratedTrade.GeneratedOfferPriceOffsetPercent = Math.Clamp(GeneratedTrade.GeneratedOfferPriceOffsetPercent, 0, 100);

        var normalizedMarketCashPriceBlendMode = NormalizeMarketCashPriceBlendMode(GeneratedTrade.MarketCashPriceBlendMode);
        if (!string.Equals(GeneratedTrade.MarketCashPriceBlendMode, normalizedMarketCashPriceBlendMode, StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.MarketCashPriceBlendMode = normalizedMarketCashPriceBlendMode;
            changed = true;
        }

        GeneratedTrade.MarketWeaponCashPricePercent = Math.Clamp(GeneratedTrade.MarketWeaponCashPricePercent, 1, 100);
        GeneratedTrade.MarketArmorCashPricePercent = Math.Clamp(GeneratedTrade.MarketArmorCashPricePercent, 1, 100);

        if (string.IsNullOrWhiteSpace(GeneratedTrade.MarketCashPriceCachePath)
            || GeneratedTrade.MarketCashPriceCachePath.Equals("config/customAssort.json", StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.MarketCashPriceCachePath = "db/Generated/customAssort.json";
            changed = true;
        }

        GeneratedTrade.GeneratedPriceExternalFleaGameMode = NormalizeExternalFleaGameMode(GeneratedTrade.GeneratedPriceExternalFleaGameMode);
        GeneratedTrade.GeneratedPriceExternalFleaPriceFilePaths ??= [];
        GeneratedTrade.GeneratedBarterPriceOffsetPercent = Math.Clamp(GeneratedTrade.GeneratedBarterPriceOffsetPercent, 0, 100);
        GeneratedTrade.GeneratedBarterMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterAmmoPackMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterAmmoPackMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterWeaponMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterWeaponMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterMedicalMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterMedicalMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterHeadwearMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterHeadwearMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterArmorMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterArmorMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterCaseValuableMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterCaseValuableMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterDefaultMaxDifferentItems = Math.Clamp(GeneratedTrade.GeneratedBarterDefaultMaxDifferentItems, 1, 6);
        GeneratedTrade.GeneratedBarterDefaultItemWeight = Math.Max(0.01, GeneratedTrade.GeneratedBarterDefaultItemWeight);
        GeneratedTrade.GeneratedBarterDefaultItemMaxUsesPerRestock = Math.Max(0, GeneratedTrade.GeneratedBarterDefaultItemMaxUsesPerRestock);
        GeneratedTrade.GeneratedBarterDefaultOverusePenalty = Math.Clamp(GeneratedTrade.GeneratedBarterDefaultOverusePenalty, 0, 10);
        GeneratedTrade.GeneratedBarterMaxItemCount = Math.Clamp(GeneratedTrade.GeneratedBarterMaxItemCount, 1, 99);
        GeneratedTrade.GeneratedBarterMinItemPrice = Math.Max(1, GeneratedTrade.GeneratedBarterMinItemPrice);

        if (string.IsNullOrWhiteSpace(GeneratedTrade.GeneratedBarterWhitelistPath))
        {
            GeneratedTrade.GeneratedBarterWhitelistPath = "config/generated_barter_whitelist.jsonc";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(GeneratedTrade.GeneratedBarterOutputPath)
            || string.Equals(GeneratedTrade.GeneratedBarterOutputPath, "config/generated_barters.jsonc", StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.GeneratedBarterOutputPath = "db/Generated/generated_barters.jsonc";
            changed = true;
        }

        if (!string.Equals(GeneratedTrade.GeneratedOfferPriceMode, "Exact", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GeneratedTrade.GeneratedOfferPriceMode, "Under", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GeneratedTrade.GeneratedOfferPriceMode, "Over", StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.GeneratedOfferPriceMode = "Exact";
            changed = true;
        }

        if (!string.Equals(GeneratedTrade.GeneratedBarterPriceMode, "Exact", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GeneratedTrade.GeneratedBarterPriceMode, "Under", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GeneratedTrade.GeneratedBarterPriceMode, "Over", StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.GeneratedBarterPriceMode = "Exact";
            changed = true;
        }

        if (!string.Equals(GeneratedTrade.GeneratedBarterSelectionMode, "Stable", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GeneratedTrade.GeneratedBarterSelectionMode, "Random", StringComparison.OrdinalIgnoreCase))
        {
            GeneratedTrade.GeneratedBarterSelectionMode = "Stable";
            changed = true;
        }

        GeneratedTrade.GeneratedBarterComponentPriceSource = NormalizeGeneratedBarterComponentPriceSource(GeneratedTrade.GeneratedBarterComponentPriceSource);

        if (GeneratedTrade.GeneratedPriceExternalFleaPriceFilePaths == null)
        {
            GeneratedTrade.GeneratedPriceExternalFleaPriceFilePaths = [];
            changed = true;
        }

        if (changed)
        {
            SaveJson(_generatedTradeSettingsPath, GeneratedTrade);
        }
    }

    private void ApplyGeneratedTradeSettingsToRuntimeSettings()
    {
        Settings.AutoPriceGeneratedOffers = GeneratedTrade.AutoPriceGeneratedOffers;
        Settings.MarketRepriceCashOffers = GeneratedTrade.MarketRepriceCashOffers;
        Settings.MarketCashPriceBlendMode = GeneratedTrade.MarketCashPriceBlendMode;
        Settings.MarketWeaponCashPricePercent = GeneratedTrade.MarketWeaponCashPricePercent;
        Settings.MarketArmorCashPricePercent = GeneratedTrade.MarketArmorCashPricePercent;
        Settings.MarketCashPriceCacheEnabled = GeneratedTrade.MarketCashPriceCacheEnabled;
        Settings.MarketCashPriceCachePath = GeneratedTrade.MarketCashPriceCachePath;
        Settings.GeneratedOfferMaxStackObjectsCount = GeneratedTrade.GeneratedOfferMaxStackObjectsCount;
        Settings.GeneratedOfferMaxBuyRestrictionMax = GeneratedTrade.GeneratedOfferMaxBuyRestrictionMax;
        Settings.GeneratedOfferPriceMode = GeneratedTrade.GeneratedOfferPriceMode;
        Settings.GeneratedOfferPriceOffsetPercent = GeneratedTrade.GeneratedOfferPriceOffsetPercent;
        Settings.GeneratedPriceUseExternalFleaPriceFiles = GeneratedTrade.GeneratedPriceUseExternalFleaPriceFiles;
        Settings.GeneratedPriceExternalFleaGameMode = GeneratedTrade.GeneratedPriceExternalFleaGameMode;
        Settings.GeneratedPriceScanSiblingModsForFleaPriceFiles = GeneratedTrade.GeneratedPriceScanSiblingModsForFleaPriceFiles;
        Settings.GeneratedPriceExternalFleaPriceFilePaths = GeneratedTrade.GeneratedPriceExternalFleaPriceFilePaths;
        Settings.GeneratedPricePreferExternalFleaPrices = GeneratedTrade.GeneratedPricePreferExternalFleaPrices;
        Settings.GeneratedPriceLogExternalFleaPriceSource = GeneratedTrade.GeneratedPriceLogExternalFleaPriceSource;
        Settings.GeneratedBarterPriceMode = GeneratedTrade.GeneratedBarterPriceMode;
        Settings.GeneratedBarterSelectionMode = GeneratedTrade.GeneratedBarterSelectionMode;
        Settings.GeneratedBarterPriceOffsetPercent = GeneratedTrade.GeneratedBarterPriceOffsetPercent;
        Settings.GeneratedBarterMaxDifferentItems = GeneratedTrade.GeneratedBarterMaxDifferentItems;
        Settings.GeneratedBarterAmmoPackMaxDifferentItems = GeneratedTrade.GeneratedBarterAmmoPackMaxDifferentItems;
        Settings.GeneratedBarterWeaponMaxDifferentItems = GeneratedTrade.GeneratedBarterWeaponMaxDifferentItems;
        Settings.GeneratedBarterMedicalMaxDifferentItems = GeneratedTrade.GeneratedBarterMedicalMaxDifferentItems;
        Settings.GeneratedBarterHeadwearMaxDifferentItems = GeneratedTrade.GeneratedBarterHeadwearMaxDifferentItems;
        Settings.GeneratedBarterArmorMaxDifferentItems = GeneratedTrade.GeneratedBarterArmorMaxDifferentItems;
        Settings.GeneratedBarterCaseValuableMaxDifferentItems = GeneratedTrade.GeneratedBarterCaseValuableMaxDifferentItems;
        Settings.GeneratedBarterDefaultMaxDifferentItems = GeneratedTrade.GeneratedBarterDefaultMaxDifferentItems;
        Settings.GeneratedBarterBalanceItemUsage = GeneratedTrade.GeneratedBarterBalanceItemUsage;
        Settings.GeneratedBarterUseWeightedCategories = GeneratedTrade.GeneratedBarterUseWeightedCategories;
        Settings.GeneratedBarterDefaultItemWeight = GeneratedTrade.GeneratedBarterDefaultItemWeight;
        Settings.GeneratedBarterDefaultItemMaxUsesPerRestock = GeneratedTrade.GeneratedBarterDefaultItemMaxUsesPerRestock;
        Settings.GeneratedBarterDefaultOverusePenalty = GeneratedTrade.GeneratedBarterDefaultOverusePenalty;
        Settings.GeneratedBarterComponentPriceSource = GeneratedTrade.GeneratedBarterComponentPriceSource;
        Settings.GeneratedBarterMaxItemCount = GeneratedTrade.GeneratedBarterMaxItemCount;
        Settings.GeneratedBarterMinItemPrice = GeneratedTrade.GeneratedBarterMinItemPrice;
        Settings.GeneratedBarterUseWhitelist = GeneratedTrade.GeneratedBarterUseWhitelist;
        Settings.GeneratedBarterWhitelistPath = GeneratedTrade.GeneratedBarterWhitelistPath;
        Settings.GeneratedBarterOutputPath = GeneratedTrade.GeneratedBarterOutputPath;
    }

    private static string NormalizeMarketCashPriceBlendMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "EqualParts";
        }

        var normalized = mode.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "allflea" or "flea" or "fleaonly" => "AllFlea",
            "heavyflea" or "mostlyflea" or "fleabiased" => "HeavyFlea",
            "equalparts" or "equal" or "average" or "avg" or "halfandhalf" or "fiftyfifty" => "EqualParts",
            "alltbp" or "tbp" or "alltraderbestprice" or "traderbestprice" or "alltrader" or "trader" => "AllTBP",
            "heavytbp" or "mostlytbp" or "tbpbiased" or "heavytraderbestprice" or "heavytrader" => "HeavyTBP",
            _ => "EqualParts"
        };
    }

    private static string NormalizeGeneratedBarterComponentPriceSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "FleaTraderAverage";
        }

        var normalized = source.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "flea" or "fleaonly" => "Flea",
            "trader" or "traderonly" or "selltotrader" or "traderprice" => "Trader",
            "average" or "avg" or "middle" or "middleground" or "fleatraderaverage" or "fleaandtrader" => "FleaTraderAverage",
            _ => "FleaTraderAverage"
        };
    }

    private static string NormalizeExternalFleaGameMode(string? gameMode)
    {
        return string.Equals(gameMode, "pve", StringComparison.OrdinalIgnoreCase)
            ? "pve"
            : "regular";
    }

    private void NormalizeSettings(bool forceSave = false)
    {
        var changed = forceSave;

        Settings.CashOfferPercent = Math.Clamp(Settings.CashOfferPercent, 0, 100);
        Settings.OutOfStockChance = Math.Clamp(Settings.OutOfStockChance, 0, 100);
        Settings.RepairQuality = Math.Clamp(Settings.RepairQuality, 0.0, 1.0);
        if (Settings.CashOffersOnly && Settings.RandomizeCashBarterOffers)
        {
            YATMLogger.Log("[Config] CashOffersOnly and RandomizeCashBarterOffers cannot both be true. CashOffersOnly takes priority, so RandomizeCashBarterOffers was disabled.");
            Settings.RandomizeCashBarterOffers = false;
            changed = true;
        }

        if (changed)
        {
            SaveJson(_settingsPath, Settings);
        }
    }

    private void LoadOrGeneratePrices(TraderAssort assortJson)
    {
        var manualRows = LoadManualOfferRows();
        Prices = BuildRuntimePriceConfigsFromAssort(assortJson);
        MergeManualOfferRows(manualRows);

        YATMLogger.LogDebug($"[Config] Built {Prices.Count} runtime price config row(s) from Tony assort; merged {manualRows.Count} manual offer row(s) from manual_offers.jsonc.");
    }

    private List<PriceConfigItem> LoadManualOfferRows()
    {
        if (!File.Exists(_pricesPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_pricesPath);
            return JsonSerializer.Deserialize<List<PriceConfigItem>>(json, CachedReadOptions) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tony] Error loading manual_offers.jsonc: {ex.Message}");
            YATMLogger.Log($"[Config] Error loading manual_offers.jsonc: {ex.Message}");
            return [];
        }
    }

    private List<PriceConfigItem> BuildRuntimePriceConfigsFromAssort(TraderAssort assortJson)
    {
        var rows = new List<PriceConfigItem>();

        foreach (var item in assortJson.Items)
        {
            if (item.ParentId != "hideout")
            {
                continue;
            }

            var offerId = item.Id;
            var tpl = GetTemplateId(item);

            if (string.IsNullOrWhiteSpace(offerId) || string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            if (!TryReadAssortCashPrice(assortJson, offerId, out var price, out var currency))
            {
                continue;
            }

            var row = new PriceConfigItem
            {
                OfferId = offerId,
                TplId = tpl,
                ItemName = GetLocaleName(tpl),
                Price = price,
                Currency = currency,
                CashOnly = true,
                AlwaysBarter = false,
                AlwaysInStock = false,
                BarterScheme = null,
                BarterSchemeValueBasis = "Unit",
                AutoGeneratedBarter = false
            };

            HydrateRuntimeAmmoPackMetadata(row);
            rows.Add(row);
        }

        return rows
            .OrderBy(x => x.ItemName)
            .ThenBy(x => x.OfferId)
            .ToList();
    }

    private void MergeManualOfferRows(IReadOnlyList<PriceConfigItem> manualRows)
    {
        if (manualRows.Count == 0)
        {
            return;
        }

        var byOfferId = Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
            .GroupBy(x => x.OfferId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var byTpl = Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.TplId))
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var merged = 0;

        foreach (var manual in manualRows.Where(x => x != null))
        {
            PriceConfigItem? target = null;

            if (!string.IsNullOrWhiteSpace(manual.OfferId))
            {
                byOfferId.TryGetValue(manual.OfferId!, out target);
            }

            if (target == null && !string.IsNullOrWhiteSpace(manual.TplId))
            {
                byTpl.TryGetValue(manual.TplId, out target);
            }

            if (target == null)
            {
                manual.AutoGeneratedBarter = false;
                HydrateRuntimeAmmoPackMetadata(manual);
                Prices.Add(manual);
                added++;
                continue;
            }

            MergeManualOfferRow(target, manual);
            merged++;
        }

        Prices = Prices
            .OrderBy(x => x.ItemName)
            .ThenBy(x => x.OfferId)
            .ToList();

        if (merged > 0 || added > 0)
        {
            YATMLogger.LogDebug($"[Config] manual_offers.jsonc rows applied: {merged} merged, {added} added.");
        }
    }

    private void MergeManualOfferRow(PriceConfigItem target, PriceConfigItem manual)
    {
        if (!string.IsNullOrWhiteSpace(manual.OfferId))
        {
            target.OfferId = manual.OfferId;
        }

        if (!string.IsNullOrWhiteSpace(manual.TplId))
        {
            target.TplId = manual.TplId;
        }

        if (!string.IsNullOrWhiteSpace(manual.ItemName))
        {
            target.ItemName = manual.ItemName;
        }

        if (manual.Price > 0)
        {
            target.Price = manual.Price;
        }

        if (!string.IsNullOrWhiteSpace(manual.Currency))
        {
            target.Currency = manual.Currency;
        }

        target.CashOnly = manual.CashOnly;
        target.AlwaysBarter = manual.AlwaysBarter;
        target.AlwaysInStock = manual.AlwaysInStock;

        if (manual.BarterScheme != null && manual.BarterScheme.Count > 0)
        {
            target.BarterScheme = manual.BarterScheme;
        }

        target.AutoGeneratedBarter = false;
        HydrateRuntimeAmmoPackMetadata(target);
    }

    private void HydrateRuntimeAmmoPackMetadata(PriceConfigItem priceConfig)
    {
        priceConfig.PackOfferId = null;

        // First try the live item database. This catches vanilla packs and custom packs
        // after WTT/custom-item loaders have added them to the DB.
        if (TryFindBestAmmoPackForLooseAmmo(priceConfig.TplId, out var packTpl, out var packSize, out var packName)
            || TryFindBuiltInAmmoPackForLooseAmmo(priceConfig.TplId, out packTpl, out packSize, out packName))
        {
            priceConfig.AmmoBarterPackTplId = packTpl;
            priceConfig.AmmoBarterPackSize = packSize;
            priceConfig.AmmoBarterPackItemName = packName;
            priceConfig.BarterSchemeValueBasis = "Pack";
            priceConfig.GeneratedBarterCategoryOverride = "AmmoPack";
            return;
        }

        priceConfig.AmmoBarterPackTplId = null;
        priceConfig.AmmoBarterPackSize = 0;
        priceConfig.AmmoBarterPackItemName = null;

        if (string.Equals(priceConfig.BarterSchemeValueBasis, "Pack", StringComparison.OrdinalIgnoreCase))
        {
            priceConfig.BarterSchemeValueBasis = "Unit";
        }

        if (string.Equals(priceConfig.GeneratedBarterCategoryOverride, "AmmoPack", StringComparison.OrdinalIgnoreCase))
        {
            priceConfig.GeneratedBarterCategoryOverride = null;
        }
    }

    private void SyncPriceConfigsFromAssort(TraderAssort assortJson)
    {
        // No-op by design. Price rows are rebuilt in memory from the live assort, and
        // config/manual_offers.jsonc is reserved only for intentional manual overrides.
        YATMLogger.LogDebug("[Config] SyncPriceConfigsFromAssort skipped; runtime prices are code-generated and manual_offers.jsonc is not auto-written.");
    }

    private bool TryReadAssortCashPrice(
        TraderAssort assortJson,
        string offerId,
        out double price,
        out string currency)
    {
        price = 0;
        currency = "RUB";

        if (!assortJson.BarterScheme.TryGetValue(offerId, out var schemeList)
            || schemeList == null
            || schemeList.Count == 0)
        {
            return false;
        }

        var firstPayment = schemeList.FirstOrDefault()?.FirstOrDefault();
        if (firstPayment == null)
        {
            return false;
        }

        var paymentTpl = firstPayment.Template.ToString();
        if (!IsCurrencyTemplate(paymentTpl))
        {
            return false;
        }

        price = firstPayment.Count ?? 0;
        currency = TemplateToCurrency(paymentTpl);

        return price > 0;
    }

    private string GetLocaleName(string tpl)
    {
        try
        {
            var locales = _databaseServer.GetTables().Locales.Global["en"];
            if (locales.Value != null && locales.Value.TryGetValue($"{tpl} Name", out var nameVal))
            {
                return nameVal?.ToString() ?? tpl;
            }
        }
        catch
        {
            // ignored; fall back to tpl
        }

        return tpl;
    }

    private static bool TryFindBuiltInAmmoPackForLooseAmmo(
        string looseAmmoTpl,
        out string packTpl,
        out int packSize,
        out string packName)
    {
        packTpl = string.Empty;
        packSize = 0;
        packName = string.Empty;

        if (string.IsNullOrWhiteSpace(looseAmmoTpl)
            || !BuiltInAmmoPackByLooseTpl.TryGetValue(looseAmmoTpl, out var knownPack))
        {
            return false;
        }

        packTpl = knownPack.PackTpl;
        packSize = knownPack.PackSize;
        packName = knownPack.PackName;
        return !string.IsNullOrWhiteSpace(packTpl) && packSize > 0;
    }

    private bool TryFindBestAmmoPackForLooseAmmo(
        string looseAmmoTpl,
        out string packTpl,
        out int packSize,
        out string packName)
    {
        packTpl = string.Empty;
        packSize = 0;
        packName = string.Empty;

        var tables = _databaseServer.GetTables();
        var templates = GetMemberValue(GetMemberValue(tables, "Templates"), "Items")
                        ?? GetMemberValue(GetMemberValue(tables, "Templates"), "items");

        if (templates is not IDictionary templateDict)
        {
            return false;
        }

        foreach (DictionaryEntry entry in templateDict)
        {
            var candidateTpl = entry.Key?.ToString();
            var candidateTemplate = entry.Value;

            if (string.IsNullOrWhiteSpace(candidateTpl)
                || candidateTemplate == null
                || string.Equals(candidateTpl, looseAmmoTpl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadAmmoPackFilter(candidateTemplate, looseAmmoTpl, out var candidatePackSize))
            {
                continue;
            }

            if (candidatePackSize > packSize)
            {
                packTpl = candidateTpl;
                packSize = candidatePackSize;
                packName = GetLocaleName(candidateTpl);
            }
        }

        return !string.IsNullOrWhiteSpace(packTpl) && packSize > 0;
    }

    private static bool TryReadAmmoPackFilter(
        object template,
        string looseAmmoTpl,
        out int packSize)
    {
        packSize = 0;

        var props = GetMemberValue(template, "_props")
                    ?? GetMemberValue(template, "Props")
                    ?? template;

        var stackSlots = GetMemberValue(props, "StackSlots")
                         ?? GetMemberValue(props, "stackSlots");

        if (stackSlots is not IEnumerable slots)
        {
            return false;
        }

        foreach (var slot in slots)
        {
            var maxCount = GetIntMember(slot, "_max_count", 0);
            if (maxCount <= 0)
            {
                maxCount = GetIntMember(slot, "MaxCount", 0);
            }

            var slotProps = GetMemberValue(slot, "_props")
                            ?? GetMemberValue(slot, "Props")
                            ?? slot;

            var filters = GetMemberValue(slotProps, "filters")
                          ?? GetMemberValue(slotProps, "Filters");

            if (filters is not IEnumerable filterList)
            {
                continue;
            }

            foreach (var filter in filterList)
            {
                var allowed = GetMemberValue(filter, "Filter")
                              ?? GetMemberValue(filter, "filter");

                if (allowed is not IEnumerable allowedList)
                {
                    continue;
                }

                foreach (var allowedTplObj in allowedList)
                {
                    var allowedTpl = allowedTplObj?.ToString();
                    if (string.Equals(allowedTpl, looseAmmoTpl, StringComparison.OrdinalIgnoreCase))
                    {
                        packSize = maxCount;
                        return packSize > 0;
                    }
                }
            }
        }

        return false;
    }

    private sealed record BuiltInAmmoPack(string PackTpl, int PackSize, string PackName);

    // Code fallback for ammo rows that were rebuilt from Tony's live assort before the
    // pack template can be discovered from the DB. Users do not maintain this in config.
    // generated_barter_whitelist.jsonc only controls barter ingredients, not ammo pack pairing.
    private static readonly Dictionary<string, BuiltInAmmoPack> BuiltInAmmoPackByLooseTpl = new(StringComparer.OrdinalIgnoreCase)
    {
        ["5f0596629e22f464da6bbdd9"] = new("657023f81419851aef03e6f1", 20, ".366 TKM AP-M ammo pack (20 pcs)"),
        ["59e655cb86f77411dc52a77b"] = new("657024011419851aef03e6f4", 20, ".366 TKM EKO ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649ac"] = new("6a4933e1fb1eff152bd649bb", 20, ".366 TKM AP-S ammo pack (20 pcs)"),
        ["560d5e524bdc2d25448b4571"] = new("657024361419851aef03e6fa", 25, "12/70 7mm buckshot ammo pack (25 pcs)"),
        ["5d6e68a8a4b9360b6c0d54e2"] = new("64898838d5b4df6140000a20", 25, "12/70 AP-20 ammo pack (25 pcs)"),
        ["5d6e6911a4b9361bd5780d52"] = new("65702474bfc87b3a34093226", 25, "12/70 flechette ammo pack (25 pcs)"),
        ["6a4933e1fb1eff152bd649aa"] = new("6a4933e1fb1eff152bd649b6", 25, "12/70 handmade slug ammo pack (25 pcs)"),
        ["56dfef82d2720bbd668b4567"] = new("5737292724597765e5728562", 120, "5.45x39mm BP gs ammo pack (120 pcs)"),
        ["56dff026d2720bb8668b4567"] = new("57372b832459776701014e41", 120, "5.45x39mm BS gs ammo pack (120 pcs)"),
        ["56dff061d2720bb5668b4567"] = new("57372c21245977670937c6c2", 120, "5.45x39mm BT gs ammo pack (120 pcs)"),
        ["56dff2ced2720bb4668b4567"] = new("57372d1b2459776862260581", 120, "5.45x39mm PP gs ammo pack (120 pcs)"),
        ["5c0d5e4486f77478390952fe"] = new("657025ebc5d7d4cb4d078588", 120, "5.45x39mm PPBS gs Igolnik ammo pack (120 pcs)"),
        ["56dff3afd2720bba668b4567"] = new("57372e73245977685d4159b4", 120, "5.45x39mm PS gs ammo pack (120 pcs)"),
        ["6a4933e1fb1eff152bd649ad"] = new("6a493587fb1eff152bd649be", 30, "5.45x39mm BP-M gs ammo pack (30 pcs)"),
        ["6a4933e1fb1eff152bd649ae"] = new("6a493587fb1eff152bd649c0", 30, "5.45x39mm BT-R gs ammo pack (30 pcs)"),
        ["6a4933e1fb1eff152bd649af"] = new("6a493587fb1eff152bd649c2", 30, "5.45x39mm PP-M gs ammo pack (30 pcs)"),
        ["6a4933e1fb1eff152bd649b0"] = new("6a493587fb1eff152bd649c5", 30, "5.45x39mm PS-R gs ammo pack (30 pcs)"),
        ["59e0d99486f7744a32234762"] = new("64acea16c4eda9354b0226b0", 20, "7.62x39mm BP gzh ammo pack (20 pcs)"),
        ["601aa3d2b2bcb34913271e6d"] = new("6489851fc827d4637f01791b", 20, "7.62x39mm MAI AP ammo pack (20 pcs)"),
        ["64b7af434b75259c590fa893"] = new("64ace9f9c4eda9354b0226aa", 20, "7.62x39mm PP gzh ammo pack (20 pcs)"),
        ["5656d7c34bdc2d9d198b4587"] = new("5649ed104bdc2d3d1c8b458b", 20, "7.62x39mm PS gzh ammo pack (20 pcs)"),
        ["5887431f2459777e1612938f"] = new("65702577cfc010a0f5006a2c", 20, "7.62x54mm R LPS gzh ammo pack (20 pcs)"),
        ["5e023d48186a883be655e551"] = new("648984b8d5b4df6140000a1a", 20, "7.62x54mm R BS gs ammo pack (20 pcs)"),
        ["560d61e84bdc2da74d8b4571"] = new("560d75f54bdc2da74d8b4573", 20, "7.62x54mm R SNB gzh ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649b1"] = new("6a493587fb1eff152bd649c7", 20, "7.62x39mm PP+ gzh ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649b2"] = new("6a493587fb1eff152bd649c9", 20, "7.62x39mm PS-H gzh ammo pack (20 pcs)"),
        ["6a427a2e38a6d33bffe9829b"] = new("65702577cfc010a0f5006a2c", 20, "7.62x54mm R LPS gzh ammo pack (20 pcs)"),
        ["6a427a2e38a6d33bffe98292"] = new("648984b8d5b4df6140000a1a", 20, "7.62x54mm R BS gs ammo pack (20 pcs)"),
        ["6a427a2e38a6d33bffe9829d"] = new("560d75f54bdc2da74d8b4573", 20, "7.62x54mm R SNB gzh ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649b3"] = new("6a493587fb1eff152bd649cb", 20, "7.62x54mm R LPS-M ammo pack (20 pcs)"),
        ["57372140245977611f70ee91"] = new("657026341419851aef03e730", 50, "9x18mm PM SP7 gzh ammo pack (50 pcs)"),
        ["5c925fa22e221601da359b7b"] = new("65702591c5d7d4cb4d07857c", 50, "9x19mm AP 6.3 ammo pack (50 pcs)"),
        ["5efb0da7a29a85116f6ea05f"] = new("648987d673c462723909a151", 50, "9x19mm PBP ammo pack (50 pcs)"),
        ["56d59d3ad2720bdb418b4577"] = new("657025a81419851aef03e724", 50, "9x19mm Pst gzh ammo pack (50 pcs)"),
        ["5c0d56a986f774449d5de529"] = new("5c1127bdd174af44217ab8b9", 20, "9x19mm RIP ammo pack (20 pcs)"),
        ["5c0d688c86f77413ae3407b2"] = new("6489854673c462723909a14e", 20, "9x39mm BP ammo pack (20 pcs)"),
        ["61962d879bb3d20b0946d385"] = new("657025cfbfc87b3a34093253", 20, "9x39mm PAB-9 gs ammo pack (20 pcs)"),
        ["57a0dfb82459774d3078b56c"] = new("657025d4c5d7d4cb4d078585", 20, "9x39mm SP-5 gs ammo pack (20 pcs)"),
        ["57a0e5022459774d1673f889"] = new("657025dabfc87b3a34093256", 20, "9x39mm SP-6 gs ammo pack (20 pcs)"),
        ["5c0d668f86f7747ccb7f13b2"] = new("657025dfcfc010a0f5006a3b", 20, "9x39mm SPP gs ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649b4"] = new("6a493587fb1eff152bd649cd", 20, "9x39mm SP-5M gs ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649b5"] = new("6a493587fb1eff152bd649cf", 20, "9x39mm SP-6U gs ammo pack (20 pcs)"),
        ["6a4933e1fb1eff152bd649ab"] = new("6a4933e1fb1eff152bd649b9", 10, "12.7x55mm PS12V ammo pack (10 pcs)")
    };

    private static object? GetMemberValue(object? target, string memberName)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();

        var prop = type.GetProperty(
            memberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (prop != null)
        {
            return prop.GetValue(target);
        }

        var field = type.GetField(
            memberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field != null)
        {
            return field.GetValue(target);
        }

        if (!memberName.Equals("ExtensionData", StringComparison.OrdinalIgnoreCase))
        {
            var extensionData = type.GetProperty(
                    "ExtensionData",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetValue(target)
                ?? type.GetField(
                    "ExtensionData",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetValue(target);

            if (extensionData is IDictionary extensionDictionary)
            {
                foreach (DictionaryEntry entry in extensionDictionary)
                {
                    if (entry.Key?.ToString()?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return entry.Value;
                    }
                }
            }
        }

        if (target is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key?.ToString()?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return entry.Value;
                }
            }
        }

        return null;
    }

    private static int GetIntMember(object? target, string memberName, int defaultValue)
    {
        var value = GetMemberValue(target, memberName);
        if (value == null)
        {
            return defaultValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return int.TryParse(value.ToString(), out var parsed)
            ? parsed
            : defaultValue;
    }

    public void SavePrices()
    {
        // Runtime price rows are code/generated now. config/manual_offers.jsonc is manual-only,
        // so the mod must not overwrite it with synced assort rows.
        YATMLogger.LogDebug("[Config] SavePrices skipped; config/manual_offers.jsonc is manual-only.");
    }

    public void StripManualBarterSchemesForAutoGeneratedMode()
    {
        if (Settings.ManualBarters)
        {
            return;
        }

        var stripped = 0;

        foreach (var priceConfig in Prices)
        {
            if (priceConfig == null || priceConfig.AutoGeneratedBarter)
            {
                continue;
            }

            // ManualBarters=false means manual_offers.jsonc and addon price rows are metadata/rules only.
            // Keep identity, stock flags, force-barter flags, prices, and runtime ammo pack metadata,
            // but do not let any hard-written BarterScheme become a live payment recipe.
            if (priceConfig.BarterScheme != null && priceConfig.BarterScheme.Count > 0)
            {
                priceConfig.BarterScheme = null;
                stripped++;
            }

            priceConfig.AutoGeneratedBarter = false;
            priceConfig.CashOnly = true;

            if (string.IsNullOrWhiteSpace(priceConfig.BarterSchemeValueBasis))
            {
                priceConfig.BarterSchemeValueBasis = !string.IsNullOrWhiteSpace(priceConfig.AmmoBarterPackTplId)
                    ? "Pack"
                    : "Unit";
            }
        }

        if (stripped > 0)
        {
            YATMLogger.LogDebug($"[GeneratedBarters] ManualBarters=false: ignored {stripped} hard-written BarterScheme row(s) from manual_offers.jsonc/addon price rules. Generated barter recipes will be used instead.");
        }
    }

    public void SaveGeneratedBarters()
    {
        if (!Directory.Exists(_configDir))
        {
            Directory.CreateDirectory(_configDir);
        }

        var generatedBarterRows = Prices
            .Where(x => x.AutoGeneratedBarter && x.BarterScheme != null && x.BarterScheme.Count > 0)
            .OrderBy(x => x.ItemName)
            .ThenBy(x => x.OfferId)
            .ToList();

        var generatedBarterPath = ResolveGeneratedBarterOutputPath();
        var generatedBarterDir = Path.GetDirectoryName(generatedBarterPath);
        if (!string.IsNullOrWhiteSpace(generatedBarterDir) && !Directory.Exists(generatedBarterDir))
        {
            Directory.CreateDirectory(generatedBarterDir);
        }

        if (generatedBarterRows.Count == 0)
        {
            if (File.Exists(generatedBarterPath))
            {
                File.Delete(generatedBarterPath);
            }

            YATMLogger.LogDebug($"[GeneratedBarters] No generated barter rows to save. {Settings.GeneratedBarterOutputPath} was left empty/removed.");
            return;
        }

        SaveJson(generatedBarterPath, generatedBarterRows);
        YATMLogger.Log($"[GeneratedBarters] Saved {generatedBarterRows.Count} auto-generated barter row(s) to {Settings.GeneratedBarterOutputPath}. manual_offers.jsonc was not overwritten.");
    }

    public void LoadGeneratedBartersAndMerge()
    {
        if (Settings.ManualBarters)
        {
            // Keep manual recipe priority clean. Runtime fallback generation still runs
            // after the payment roll for selected offers that do not have manual recipes.
            YATMLogger.LogDebug("[GeneratedBarters] ManualBarters=true: generated barter cache pre-merge skipped; selected fallback rows can still be generated at runtime.");
            return;
        }

        var generatedBarterPath = ResolveGeneratedBarterOutputPath();
        if (!File.Exists(generatedBarterPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(generatedBarterPath);
            var generatedRows = JsonSerializer.Deserialize<List<PriceConfigItem>>(json, CachedReadOptions) ?? [];
            var merged = 0;
            var added = 0;

            var existingIndexByOfferId = Prices
                .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
                .Select((x, index) => new { x.OfferId, index })
                .ToDictionary(x => x.OfferId!, x => x.index, StringComparer.OrdinalIgnoreCase);

            foreach (var generatedRow in generatedRows)
            {
                if (generatedRow == null || string.IsNullOrWhiteSpace(generatedRow.OfferId))
                {
                    continue;
                }

                generatedRow.AutoGeneratedBarter = true;
                HydrateRuntimeAmmoPackMetadata(generatedRow);

                if (existingIndexByOfferId.TryGetValue(generatedRow.OfferId, out var existingIndex))
                {
                    MergeGeneratedBarterRow(Prices[existingIndex], generatedRow);
                    merged++;
                    continue;
                }

                Prices.Add(generatedRow);
                existingIndexByOfferId[generatedRow.OfferId] = Prices.Count - 1;
                added++;
            }

            if (merged > 0 || added > 0)
            {
                YATMLogger.Log($"[GeneratedBarters] Loaded {generatedRows.Count} generated barter row(s) from {Settings.GeneratedBarterOutputPath}: {merged} merged, {added} added.");
            }
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedBarters] Failed to load {Settings.GeneratedBarterOutputPath}: {ex.Message}");
        }
    }

    private void MergeGeneratedBarterRow(PriceConfigItem target, PriceConfigItem generatedRow)
    {
        if (generatedRow.Price > 0)
        {
            target.Price = generatedRow.Price;
        }

        if (!string.IsNullOrWhiteSpace(generatedRow.Currency))
        {
            target.Currency = generatedRow.Currency;
        }

        if (generatedRow.BarterScheme != null && generatedRow.BarterScheme.Count > 0)
        {
            target.BarterScheme = generatedRow.BarterScheme;
            target.CashOnly = generatedRow.CashOnly;
            target.BarterSchemeValueBasis = generatedRow.BarterSchemeValueBasis;
            target.AutoGeneratedBarter = true;
        }

        // New ammo barter mode keeps the loose ammo OfferId and swaps _tpl to the pack when barter wins.
        // Do not merge old PackOfferId metadata from generated barter cache rows.
        target.PackOfferId = null;

        if (string.IsNullOrWhiteSpace(target.AmmoBarterPackTplId))
        {
            target.AmmoBarterPackTplId = generatedRow.AmmoBarterPackTplId;
        }

        if (string.IsNullOrWhiteSpace(target.AmmoBarterPackItemName))
        {
            target.AmmoBarterPackItemName = generatedRow.AmmoBarterPackItemName;
        }

        if (target.AmmoBarterPackSize <= 0)
        {
            target.AmmoBarterPackSize = generatedRow.AmmoBarterPackSize;
        }

        HydrateRuntimeAmmoPackMetadata(target);
    }

    public string ResolvePathFromModRoot(string? relativeOrAbsolutePath, string fallbackRelativePath)
    {
        var path = string.IsNullOrWhiteSpace(relativeOrAbsolutePath)
            ? fallbackRelativePath
            : relativeOrAbsolutePath.Trim();

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(_modPath, path.Replace('/', Path.DirectorySeparatorChar));
    }

    private string ResolveGeneratedBarterOutputPath()
    {
        return ResolvePathFromModRoot(Settings.GeneratedBarterOutputPath, "db/Generated/generated_barters.jsonc");
    }

    private static void SaveJson<T>(string path, T data)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, CachedWriteOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tony] Failed to save config: {ex.Message}");
        }
    }

    // Add these static readonly fields to cache JsonSerializerOptions instances
    private static readonly JsonSerializerOptions CachedReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CachedWriteOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
