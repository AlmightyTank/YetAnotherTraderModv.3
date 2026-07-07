using System;
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
        _pricesPath = Path.Combine(_configDir, "items.json");
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
                    || json.Contains("GeneratedBarter", StringComparison.OrdinalIgnoreCase);

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

                // false = allow custom barter recipes in items.json.
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
                && !json.Contains("AutoPriceGeneratedOffers", StringComparison.OrdinalIgnoreCase))
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
        Settings.GeneratedBarterComponentPriceSource = GeneratedTrade.GeneratedBarterComponentPriceSource;
        Settings.GeneratedBarterMaxItemCount = GeneratedTrade.GeneratedBarterMaxItemCount;
        Settings.GeneratedBarterMinItemPrice = GeneratedTrade.GeneratedBarterMinItemPrice;
        Settings.GeneratedBarterUseWhitelist = GeneratedTrade.GeneratedBarterUseWhitelist;
        Settings.GeneratedBarterWhitelistPath = GeneratedTrade.GeneratedBarterWhitelistPath;
        Settings.GeneratedBarterOutputPath = GeneratedTrade.GeneratedBarterOutputPath;
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
        if (File.Exists(_pricesPath))
        {
            try
            {
                var json = File.ReadAllText(_pricesPath);

                Prices = JsonSerializer.Deserialize<List<PriceConfigItem>>(json, CachedReadOptions) ?? new List<PriceConfigItem>();
                YATMLogger.LogDebug($"Loaded {Prices.Count} custom price entries from items.json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tony] Error loading items.json: {ex.Message}");
                YATMLogger.Log($"Error loading items.json: {ex.Message}");
            }
        }
        else
        {
            YATMLogger.LogDebug("items.json not found. Generating default prices from assort...");

            Prices = new List<PriceConfigItem>();
            var locales = _databaseServer.GetTables().Locales.Global["en"];
            int generatedCount = 0;

            foreach (var item in assortJson.Items)
            {
                if (item.ParentId != "hideout")
                {
                    continue;
                }

                if (!assortJson.BarterScheme.ContainsKey(item.Id))
                {
                    continue;
                }

                var schemeList = assortJson.BarterScheme[item.Id];
                if (schemeList == null || schemeList.Count == 0)
                {
                    continue;
                }

                var tpl = GetTemplateId(item);
                if (string.IsNullOrEmpty(tpl))
                {
                    continue;
                }

                var itemName = tpl;
                if (locales.Value != null && locales.Value.TryGetValue($"{tpl} Name", out var nameVal))
                {
                    itemName = nameVal?.ToString() ?? tpl;
                }

                var copiedBarterScheme = new List<List<PaymentConfigItem>>();

                foreach (var schemeOption in schemeList)
                {
                    var copiedOption = new List<PaymentConfigItem>();

                    foreach (var component in schemeOption)
                    {
                        var componentTpl = component.Template.ToString();
                        var componentName = componentTpl;

                        if (locales.Value != null && locales.Value.TryGetValue($"{componentTpl} Name", out var componentNameVal))
                        {
                            componentName = componentNameVal?.ToString() ?? componentTpl;
                        }

                        copiedOption.Add(new PaymentConfigItem
                        {
                            TplId = componentTpl,
                            ItemName = componentName,
                            Count = component.Count ?? 0
                        });
                    }

                    copiedBarterScheme.Add(copiedOption);
                }

                var firstPayment = copiedBarterScheme.FirstOrDefault()?.FirstOrDefault();
                var firstPaymentIsCash = firstPayment != null && IsCurrencyTemplate(firstPayment.TplId);

                var priceEntry = new PriceConfigItem
                {
                    OfferId = item.Id,
                    TplId = tpl,
                    ItemName = itemName,

                    Price = firstPaymentIsCash ? firstPayment!.Count : 0,
                    Currency = firstPaymentIsCash ? TemplateToCurrency(firstPayment!.TplId) : "RUB",

                    CashOnly = firstPaymentIsCash,
                    BarterScheme = copiedBarterScheme
                };

                Prices.Add(priceEntry);

                generatedCount++;
            }

            Prices = Prices
                .OrderBy(x => x.ItemName)
                .ThenBy(x => x.OfferId)
                .ToList();

            SaveJson(_pricesPath, Prices);
            YATMLogger.Log($"Generated items.json with {generatedCount} entries.");
        }
    }
    public void SavePrices()
    {
        if (!Directory.Exists(_configDir))
        {
            Directory.CreateDirectory(_configDir);
        }

        SaveJson(_pricesPath, Prices);
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

            // ManualBarters=false means items.json and addon price rows are metadata/rules only.
            // Keep identity, stock flags, force-barter flags, prices, and paired ammo pack metadata,
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
            YATMLogger.LogDebug($"[GeneratedBarters] ManualBarters=false: ignored {stripped} hard-written BarterScheme row(s) from items.json/addon price rules. Generated barter recipes will be used instead.");
        }
    }

    public void SaveGeneratedBarters()
    {
        if (Settings.ManualBarters)
        {
            // Keep manual mode clean: generated barter cache is not written.
            return;
        }

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
        YATMLogger.Log($"[GeneratedBarters] Saved {generatedBarterRows.Count} auto-generated barter row(s) to {Settings.GeneratedBarterOutputPath}. items.json was not overwritten.");
    }

    public void LoadGeneratedBartersAndMerge()
    {
        if (Settings.ManualBarters)
        {
            // ManualBarters=true means use only hard-written BarterScheme rows from
            // items.json/addon price files. Do not merge generated barter cache rows.
            YATMLogger.LogDebug("[GeneratedBarters] ManualBarters=true: generated barter cache merge skipped.");
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

    private static void MergeGeneratedBarterRow(PriceConfigItem target, PriceConfigItem generatedRow)
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

        // Preserve old items.json as the source of truth for paired ammo metadata when present.
        if (string.IsNullOrWhiteSpace(target.PackOfferId))
        {
            target.PackOfferId = generatedRow.PackOfferId;
        }

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
    }

    private string ResolveGeneratedBarterOutputPath()
    {
        var relativePath = string.IsNullOrWhiteSpace(Settings.GeneratedBarterOutputPath)
            ? "db/Generated/generated_barters.jsonc"
            : Settings.GeneratedBarterOutputPath;

        return Path.Combine(_modPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
