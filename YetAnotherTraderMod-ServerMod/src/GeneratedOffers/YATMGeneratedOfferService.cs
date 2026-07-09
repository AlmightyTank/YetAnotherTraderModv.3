using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using YetAnotherTraderMod.config;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.GeneratedOffers;

public sealed record MarketCashPriceResult(
    string Mode,
    double FleaPrice,
    double TraderBestPrice,
    double FinalPrice);

public sealed record MarketCashPriceComponent(
    string TplId,
    double Count);

/// <summary>
/// Runtime-facing service for generated/addon Tony offers.
///
/// Responsibilities:
/// - Build generated assort rows from current YATM raw offer files.
/// - Derive price configs from each raw root offer and its yatm_settings metadata.
/// - Save/load the generated cache for restocks.
/// - Merge generated offers and price rules into Tony's live pipeline.
///
/// Auto-generated barter recipes are created from EFT/SPT price data only for
/// selected barter offers. When ManualBarters is true, hard-written manual
/// BarterScheme rows are preferred for matching selected offers; selected offers
/// without a manual recipe can still receive an auto-generated recipe.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMGeneratedOfferService(
    ModHelper modHelper,
    DatabaseServer databaseServer,
    TraderHelper traderHelper,
    JsonUtil jsonUtil)
{
    private const string TonyTraderId = "66a0f6b2c4d8e90123456789";

    private const string GeneratedDirRelativePath = "db/Generated";
    private const string GeneratedAssortFileName = "generated_addon_assort.json";
    private const string GeneratedPricesFileName = "generated_addon_price_rules.json";
    private const string LegacyGeneratedAssortFileName = "generated_tony_assort.json";
    private const string LegacyGeneratedPricesFileName = "generated_tony_prices.json";
    private const string DefaultGeneratedBarterWhitelistRelativePath = "db/Generated/generated_barter_whitelist.default.jsonc";
    private const string GeneratedBarterSkipReportFileName = "generated_barter_skips.jsonc";

    private readonly List<PriceConfigItem> _generatedPriceConfigs = [];
    private static readonly Random AutoBarterRandom = new();
    private List<BarterCandidate>? _autoBarterCandidates;
    private GeneratedBarterWhitelistConfig? _generatedBarterWhitelistConfig;
    private Dictionary<string, double>? _externalFleaPrices;


    public IReadOnlyList<PriceConfigItem> GeneratedPriceConfigs => _generatedPriceConfigs;

    public async Task<TraderAssort> BuildOrLoadGeneratedAssort(
        Assembly assembly,
        IReadOnlyList<YATMRawTraderOfferFile> rawOfferFiles,
        SettingsConfig settings)
    {
        var result = Build(rawOfferFiles, settings);

        _generatedPriceConfigs.Clear();
        _generatedPriceConfigs.AddRange(result.PriceConfigs);

        await SaveAsync(assembly, result.GeneratedAssort, _generatedPriceConfigs);

        if (result.GeneratedAssort.Items.Count > 0 || _generatedPriceConfigs.Count > 0)
        {
            YATMLogger.Log($"[GeneratedOffers] Saved generated offer cache: {result.GeneratedAssort.Items.Count} item rows, {_generatedPriceConfigs.Count} price rows.");
        }
        else
        {
            YATMLogger.LogDebug("[GeneratedOffers] No generated offer cache needed; base Tony files remain the source of truth.");
        }

        return result.GeneratedAssort;
    }

    public TraderAssort LoadGeneratedAssortFromCache(Assembly assembly)
    {
        var generatedAssortPath = Path.Combine(GetGeneratedDir(assembly), GeneratedAssortFileName);

        if (!File.Exists(generatedAssortPath))
        {
            return CreateEmptyTraderAssort();
        }

        try
        {
            var json = File.ReadAllText(generatedAssortPath);
            return jsonUtil.Deserialize<TraderAssort>(json)
                   ?? CreateEmptyTraderAssort();
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedOffers] Failed to load generated assort cache: {ex.Message}");
            return CreateEmptyTraderAssort();
        }
    }

    public void LoadGeneratedPriceConfigsFromCache(Assembly assembly)
    {
        _generatedPriceConfigs.Clear();
        _generatedPriceConfigs.AddRange(LoadGeneratedPriceConfigs(assembly));
    }

    public void MergeGeneratedAssortIntoTonyAssort(TraderAssort targetAssort, TraderAssort generatedAssort)
    {
        if (generatedAssort.Items.Count == 0)
        {
            return;
        }

        var existingItemIds = targetAssort.Items
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();

        var addedItems = 0;
        foreach (var item in generatedAssort.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            if (existingItemIds.Add(item.Id))
            {
                targetAssort.Items.Add(item);
                addedItems++;
            }
        }

        foreach (var scheme in generatedAssort.BarterScheme)
        {
            targetAssort.BarterScheme[scheme.Key] = scheme.Value;
        }

        foreach (var levelItem in generatedAssort.LoyalLevelItems)
        {
            targetAssort.LoyalLevelItems[levelItem.Key] = levelItem.Value;
        }

        YATMLogger.Log($"[GeneratedOffers] Merged {addedItems} generated item rows into Tony assort.");
    }

    public void MergeGeneratedPriceConfigs(YATMConfig config)
    {
        if (_generatedPriceConfigs.Count == 0)
        {
            return;
        }

        var existingIndexByOfferId = config.Prices
            .Select((priceConfig, index) => new { priceConfig, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.priceConfig.OfferId))
            .GroupBy(x => x.priceConfig.OfferId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var replaced = 0;

        foreach (var priceConfig in _generatedPriceConfigs)
        {
            if (string.IsNullOrWhiteSpace(priceConfig.OfferId))
            {
                continue;
            }

            if (existingIndexByOfferId.TryGetValue(priceConfig.OfferId, out var existingIndex))
            {
                MergePriceRuleIntoExistingConfig(config.Prices[existingIndex], priceConfig);
                replaced++;
                continue;
            }

            config.Prices.Add(priceConfig);
            existingIndexByOfferId[priceConfig.OfferId] = config.Prices.Count - 1;
            added++;
        }

        YATMLogger.Log($"[GeneratedOffers] Merged generated/rule-only price configs into YATM config: {added} added, {replaced} replaced.");
    }

    private static void MergePriceRuleIntoExistingConfig(PriceConfigItem target, PriceConfigItem source)
    {
        if (!string.IsNullOrWhiteSpace(source.TplId))
        {
            target.TplId = source.TplId;
        }

        if (!string.IsNullOrWhiteSpace(source.ItemName))
        {
            target.ItemName = source.ItemName;
        }

        if (source.Price > 0)
        {
            target.Price = source.Price;
        }

        if (!string.IsNullOrWhiteSpace(source.Currency))
        {
            target.Currency = source.Currency;
        }

        target.CashOnly = source.CashOnly;
        target.AlwaysBarter = source.AlwaysBarter;
        target.AlwaysInStock = source.AlwaysInStock;

        if (source.BarterScheme != null && source.BarterScheme.Count > 0)
        {
            target.BarterScheme = source.BarterScheme;
        }

        // New ammo barter mode does not use a separate PackOfferId.
        // The loose ammo offer keeps its OfferId and swaps _tpl to the pack when barter wins.
        target.PackOfferId = null;

        if (string.IsNullOrWhiteSpace(target.AmmoBarterPackTplId))
        {
            target.AmmoBarterPackTplId = source.AmmoBarterPackTplId;
        }

        if (string.IsNullOrWhiteSpace(target.AmmoBarterPackItemName))
        {
            target.AmmoBarterPackItemName = source.AmmoBarterPackItemName;
        }

        if (target.AmmoBarterPackSize <= 0)
        {
            target.AmmoBarterPackSize = source.AmmoBarterPackSize;
        }

        if (!string.IsNullOrWhiteSpace(source.BarterSchemeValueBasis))
        {
            target.BarterSchemeValueBasis = source.BarterSchemeValueBasis;
        }

        target.GeneratedBarterCategoryOverride = source.GeneratedBarterCategoryOverride ?? target.GeneratedBarterCategoryOverride;
        target.AutoGeneratedBarter = source.AutoGeneratedBarter;
    }

    public bool ApplyAutoGeneratedBartersToConfig(YATMConfig config, TraderAssort? currentAssort = null)
    {
        // Compatibility helper: generate for every eligible price config.
        // The runtime pipeline normally calls ApplyAutoGeneratedBartersToSelectedConfig after the payment roll
        // has picked which offers actually become barter.
        return ApplyAutoGeneratedBartersToSelectedConfig(config, currentAssort, config.Prices);
    }

    public bool ApplyAutoGeneratedBartersToSelectedConfig(
        YATMConfig config,
        TraderAssort? currentAssort,
        IEnumerable<PriceConfigItem> selectedPriceConfigs)
    {
        return ApplyAutoGeneratedBartersToSelectedConfigWithResult(
            config,
            currentAssort,
            selectedPriceConfigs,
            maxSuccessfulBarters: 0).Changed;
    }

    public GeneratedBarterApplyResult ApplyAutoGeneratedBartersToSelectedConfigWithResult(
        YATMConfig config,
        TraderAssort? currentAssort,
        IEnumerable<PriceConfigItem> selectedPriceConfigs,
        int maxSuccessfulBarters = 0)
    {
        if (config.Settings.CashOffersOnly)
        {
            YATMLogger.LogDebug("[GeneratedBarters] CashOffersOnly enabled; generated barters skipped.");
            return GeneratedBarterApplyResult.Empty;
        }

        // Rebuild the auto-barter candidate pool each run so whitelist and external price changes
        // are picked up on startup/restock without stale cached candidates.
        _autoBarterCandidates = null;
        _generatedBarterWhitelistConfig = null;
        _externalFleaPrices = null;

        var selectedConfigs = selectedPriceConfigs
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TplId))
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.OfferId) ? x.OfferId! : x.TplId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (selectedConfigs.Count == 0)
        {
            return GeneratedBarterApplyResult.Empty;
        }

        var changed = false;
        var generatedCount = 0;
        var successfulCount = 0;
        var skippedCount = 0;
        var result = new GeneratedBarterApplyResult
        {
            SelectedCount = selectedConfigs.Count
        };
        var skippedDetails = new List<GeneratedBarterSkipDetail>();
        var barterItemUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var barterCategoryUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var pairedAmmoPackOfferIds = config.Prices
            .Where(IsPairedAmmoLooseConfig)
            .Select(x => x.PackOfferId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pairedAmmoPackTplIds = config.Prices
            .Where(IsPairedAmmoLooseConfig)
            .Select(x => x.AmmoBarterPackTplId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var priceConfig in selectedConfigs)
        {
            var attemptOfferId = string.IsNullOrWhiteSpace(priceConfig.OfferId) ? priceConfig.TplId : priceConfig.OfferId!;
            if (!string.IsNullOrWhiteSpace(attemptOfferId))
            {
                result.AttemptedOfferIds.Add(attemptOfferId);
            }

            if (YATMConfig.IsCurrencyTemplate(priceConfig.TplId))
            {
                skippedCount++;
                if (!string.IsNullOrWhiteSpace(attemptOfferId))
                {
                    result.SkippedOfferIds.Add(attemptOfferId);
                }
                AddGeneratedBarterSkipDetail(skippedDetails, priceConfig, config.Settings, currentAssort, "currency offers are not valid barter targets");
                continue;
            }

            // Static ammo-pack offers are helper offers for the paired ammo system.
            // They should never get their own generated barter row. The loose ammo row
            // carries the generated barter, and the roll service moves that barter onto
            // the pack offer when barter wins.
            if (IsPairedAmmoPackHelperConfig(priceConfig, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
            {
                skippedCount++;
                if (!string.IsNullOrWhiteSpace(attemptOfferId))
                {
                    result.SkippedOfferIds.Add(attemptOfferId);
                }
                AddGeneratedBarterSkipDetail(skippedDetails, priceConfig, config.Settings, currentAssort, "ammo-pack helper offer; loose ammo row owns the paired pack barter");
                continue;
            }

            if (priceConfig.Price <= 0)
            {
                priceConfig.Price = ResolveGeneratedPrice(priceConfig.TplId, config.Settings);
                if (priceConfig.Price <= 0)
                {
                    priceConfig.Price = 1;
                }

                changed = true;
            }

            if (string.IsNullOrWhiteSpace(priceConfig.Currency))
            {
                priceConfig.Currency = "RUB";
                changed = true;
            }

            if (config.Settings.ManualBarters
                && HasUsableBarterScheme(priceConfig)
                && !priceConfig.AutoGeneratedBarter)
            {
                successfulCount++;
                if (!string.IsNullOrWhiteSpace(attemptOfferId))
                {
                    result.SuccessfulOfferIds.Add(attemptOfferId);
                }

                if (priceConfig.CashOnly)
                {
                    priceConfig.CashOnly = false;
                    changed = true;
                }

                YATMLogger.LogRealDebug($"[GeneratedBarters] Manual recipe kept for selected barter: {priceConfig.ItemName} ({priceConfig.TplId}) | Offer {attemptOfferId}");

                if (maxSuccessfulBarters > 0 && successfulCount >= maxSuccessfulBarters)
                {
                    break;
                }

                continue;
            }

            var generatedScheme = GenerateAutoBarterScheme(
                priceConfig,
                config.Settings,
                currentAssort,
                barterItemUsage,
                barterCategoryUsage,
                out var skipReason);

            if (generatedScheme.Count == 0)
            {
                skippedCount++;
                if (!string.IsNullOrWhiteSpace(attemptOfferId))
                {
                    result.SkippedOfferIds.Add(attemptOfferId);
                }
                AddGeneratedBarterSkipDetail(skippedDetails, priceConfig, config.Settings, currentAssort, skipReason);
                continue;
            }

            successfulCount++;
            if (!string.IsNullOrWhiteSpace(attemptOfferId))
            {
                result.SuccessfulOfferIds.Add(attemptOfferId);
            }

            if (!priceConfig.AutoGeneratedBarter)
            {
                priceConfig.AutoGeneratedBarter = true;
                changed = true;
            }

            if (IsLooseAmmoOfferConfig(priceConfig)
                && !string.Equals(priceConfig.BarterSchemeValueBasis, "Pack", StringComparison.OrdinalIgnoreCase))
            {
                priceConfig.BarterSchemeValueBasis = "Pack";
                changed = true;
            }

            if (!PaymentSchemesEqual(priceConfig.BarterScheme, generatedScheme))
            {
                priceConfig.BarterScheme = generatedScheme;
                changed = true;
                generatedCount++;
            }

            if (priceConfig.CashOnly)
            {
                priceConfig.CashOnly = false;
                changed = true;
            }

            if (maxSuccessfulBarters > 0 && successfulCount >= maxSuccessfulBarters)
            {
                break;
            }
        }

        result.Changed = changed;
        result.GeneratedOrUpdatedCount = generatedCount;
        result.SuccessfulCount = successfulCount;
        result.SkippedCount = skippedCount;

        if (successfulCount > 0 || generatedCount > 0 || skippedCount > 0)
        {
            var targetText = maxSuccessfulBarters > 0 ? $" target {maxSuccessfulBarters}." : string.Empty;
            YATMLogger.Log($"[GeneratedBarters] Attempted {result.AttemptedOfferIds.Count} barter candidate(s){targetText} Successful {successfulCount}; generated/updated {generatedCount} barter scheme(s); skipped {skippedCount}.");
        }

        if (skippedDetails.Count > 0)
        {
            foreach (var skip in skippedDetails)
            {
                YATMLogger.Log($"[GeneratedBarters] Skipped {skip.ItemName} ({skip.TplId}) | Offer {skip.OfferId} | Category {skip.Category} | UnitPrice {skip.Price:0.##} {skip.Currency} | Target {skip.TargetPrice:0.##} {skip.Currency} ({skip.TargetValueBasis}) | Reason: {skip.Reason}");
            }
        }

        SaveGeneratedBarterSkipReport(skippedDetails);

        return result;
    }

    private void AddGeneratedBarterSkipDetail(
        List<GeneratedBarterSkipDetail> skippedDetails,
        PriceConfigItem priceConfig,
        SettingsConfig settings,
        TraderAssort? currentAssort,
        string? reason)
    {
        var category = ResolveGeneratedBarterCategory(priceConfig, settings);
        var targetBaseValue = ResolveAutoBarterTargetValue(priceConfig, settings, currentAssort);
        var targetValue = ApplyGeneratedValueMode(
            targetBaseValue,
            settings.GeneratedBarterPriceMode,
            settings.GeneratedBarterPriceOffsetPercent);

        skippedDetails.Add(new GeneratedBarterSkipDetail
        {
            OfferId = string.IsNullOrWhiteSpace(priceConfig.OfferId) ? "<missing>" : priceConfig.OfferId!,
            TplId = string.IsNullOrWhiteSpace(priceConfig.TplId) ? "<missing>" : priceConfig.TplId,
            ItemName = string.IsNullOrWhiteSpace(priceConfig.ItemName) ? ResolveItemName(priceConfig.TplId) : priceConfig.ItemName,
            Category = category,
            Price = priceConfig.Price,
            TargetPrice = Math.Max(1, targetValue),
            TargetValueBasis = ResolveGeneratedBarterTargetBasis(priceConfig, settings, currentAssort),
            Currency = string.IsNullOrWhiteSpace(priceConfig.Currency) ? "RUB" : priceConfig.Currency,
            Reason = string.IsNullOrWhiteSpace(reason) ? "generated barter returned no valid recipe" : reason!
        });
    }

    private void SaveGeneratedBarterSkipReport(IReadOnlyList<GeneratedBarterSkipDetail> skippedDetails)
    {
        try
        {
            var generatedDir = GetGeneratedDir(Assembly.GetExecutingAssembly());
            Directory.CreateDirectory(generatedDir);
            var path = Path.Combine(generatedDir, GeneratedBarterSkipReportFileName);

            if (skippedDetails.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(skippedDetails, JsonOptions));
            YATMLogger.Log($"[GeneratedBarters] Saved skipped barter report: {GeneratedDirRelativePath}/{GeneratedBarterSkipReportFileName}");
        }
        catch (Exception ex)
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Failed to save skipped barter report: {ex.Message}");
        }
    }

    private GeneratedOfferBuildResult Build(
        IReadOnlyList<YATMRawTraderOfferFile> rawOfferFiles,
        SettingsConfig settings)
    {
        var generatedJson = CreateEmptyAssortJson();
        var priceConfigs = new List<PriceConfigItem>();

        foreach (var rawOfferFile in rawOfferFiles)
        {
            AddRawYatmOfferFile(generatedJson, rawOfferFile, priceConfigs, settings);
        }

        RemoveStandalonePairedAmmoPackPriceConfigs(priceConfigs);

        var generatedAssort = jsonUtil.Deserialize<TraderAssort>(generatedJson.ToJsonString(JsonOptions))
                              ?? CreateEmptyTraderAssort();

        YATMLogger.Log($"[GeneratedOffers] Built {generatedAssort.Items.Count} raw YATM assort item rows and {priceConfigs.Count} price rule rows.");

        return new GeneratedOfferBuildResult(generatedAssort, priceConfigs);
    }

    private void AddRawYatmOfferFile(
        JsonObject generatedAssortJson,
        YATMRawTraderOfferFile rawOfferFile,
        List<PriceConfigItem> priceConfigs,
        SettingsConfig settings)
    {
        foreach (var item in rawOfferFile.Items)
        {
            if (item != null)
            {
                GetItemsArray(generatedAssortJson).Add(item.DeepClone());
            }
        }

        var targetBarterScheme = GetObject(generatedAssortJson, "barter_scheme");
        foreach (var scheme in rawOfferFile.BarterScheme)
        {
            targetBarterScheme[scheme.Key] = scheme.Value?.DeepClone();
        }

        var targetLoyalLevelItems = GetObject(generatedAssortJson, "loyal_level_items");
        foreach (var levelItem in rawOfferFile.LoyalLevelItems)
        {
            targetLoyalLevelItems[levelItem.Key] = levelItem.Value?.DeepClone();
        }

        AddPriceConfigsFromRawYatmOfferFile(rawOfferFile, priceConfigs, settings);
        YATMLogger.LogDebug($"[GeneratedOffers] Added raw YATM offer file {rawOfferFile.SourceFile}: {rawOfferFile.Items.Count} item row(s).");
    }

    private void AddPriceConfigsFromRawYatmOfferFile(
        YATMRawTraderOfferFile rawOfferFile,
        List<PriceConfigItem> priceConfigs,
        SettingsConfig settings)
    {
        foreach (var itemNode in rawOfferFile.Items)
        {
            if (itemNode is not JsonObject item)
            {
                continue;
            }

            var offerId = ReadJsonString(item, "_id");
            var tpl = ReadJsonString(item, "_tpl");
            var parentId = ReadJsonString(item, "parentId");
            var slotId = ReadJsonString(item, "slotId");

            if (!string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(slotId, "hideout", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(offerId)
                || string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            var copiedBarterScheme = CopyPaymentScheme(rawOfferFile.BarterScheme, offerId);
            var firstPayment = copiedBarterScheme.FirstOrDefault()?.FirstOrDefault();
            var firstPaymentIsCash = firstPayment != null && YATMConfig.IsCurrencyTemplate(firstPayment.TplId);
            var yatmSettings = GetYatmSettingsForOffer(rawOfferFile.YatmSettings, offerId);

            var hasHardWrittenBarter = HasNonCurrencyPayment(copiedBarterScheme);

            var price = firstPaymentIsCash
                ? firstPayment!.Count
                : ResolveGeneratedPrice(tpl, settings);

            var priceConfig = new PriceConfigItem
            {
                OfferId = offerId,
                TplId = tpl,
                ItemName = ResolveItemName(tpl),
                Price = price <= 0 ? 1 : price,
                Currency = firstPaymentIsCash ? YATMConfig.TemplateToCurrency(firstPayment!.TplId) : "RUB",
                CashOnly = firstPaymentIsCash || !hasHardWrittenBarter,
                AlwaysBarter = false,
                AlwaysInStock = false,
                BarterScheme = copiedBarterScheme
            };

            ApplyYatmSettings(priceConfig, yatmSettings);

            priceConfigs.Add(priceConfig);
        }
    }

    private List<List<PaymentConfigItem>> CopyPaymentScheme(JsonObject barterScheme, string offerId)
    {
        var copiedBarterScheme = new List<List<PaymentConfigItem>>();

        if (!barterScheme.TryGetPropertyValue(offerId, out var schemeNode) || schemeNode is not JsonArray schemeList)
        {
            return copiedBarterScheme;
        }

        foreach (var schemeOptionNode in schemeList)
        {
            if (schemeOptionNode is not JsonArray schemeOptionArray)
            {
                continue;
            }

            var copiedOption = new List<PaymentConfigItem>();
            foreach (var componentNode in schemeOptionArray)
            {
                if (componentNode is not JsonObject component)
                {
                    continue;
                }

                var componentTpl = ReadJsonString(component, "_tpl") ?? ReadJsonString(component, "TplId");
                if (string.IsNullOrWhiteSpace(componentTpl))
                {
                    continue;
                }

                copiedOption.Add(new PaymentConfigItem
                {
                    TplId = componentTpl,
                    ItemName = ResolveItemName(componentTpl),
                    Count = ReadJsonDouble(component, "count") ?? ReadJsonDouble(component, "Count") ?? 1
                });
            }

            if (copiedOption.Count > 0)
            {
                copiedBarterScheme.Add(copiedOption);
            }
        }

        return copiedBarterScheme;
    }

    private static JsonObject? GetYatmSettingsForOffer(JsonObject yatmSettings, string offerId)
    {
        if (!yatmSettings.TryGetPropertyValue(offerId, out var settingsNode) || settingsNode == null)
        {
            return null;
        }

        if (settingsNode is JsonObject directObject)
        {
            return directObject;
        }

        // Supports the same nested shape as barter_scheme:
        // "yatm_settings": { "offerId": [[ { "CashOnly": true } ]] }
        if (settingsNode is JsonArray outerArray)
        {
            foreach (var outer in outerArray)
            {
                if (outer is JsonObject outerObject)
                {
                    return outerObject;
                }

                if (outer is not JsonArray innerArray)
                {
                    continue;
                }

                foreach (var inner in innerArray)
                {
                    if (inner is JsonObject innerObject)
                    {
                        return innerObject;
                    }
                }
            }
        }

        return null;
    }

    private static void ApplyYatmSettings(PriceConfigItem priceConfig, JsonObject? yatmSettings)
    {
        if (yatmSettings == null)
        {
            return;
        }

        priceConfig.CashOnly = ReadJsonBool(yatmSettings, "CashOnly") ?? priceConfig.CashOnly;
        priceConfig.AlwaysBarter = ReadJsonBool(yatmSettings, "AlwaysBarter") ?? priceConfig.AlwaysBarter;
        priceConfig.AlwaysInStock = ReadJsonBool(yatmSettings, "AlwaysInStock") ?? priceConfig.AlwaysInStock;

        // Generated barter category, ammo-pack target, pack size, and pack tpl are resolved in code. Ingredient pools stay user-editable in generated_barter_whitelist.jsonc.
        // Old addon/manual fields are intentionally ignored so users do not have to maintain them.
        priceConfig.PackOfferId = null;
    }

    private static bool ShouldProcessOffer(YATMGeneratedOfferDefinition offer)
    {
        if (offer == null || !offer.Enabled)
        {
            return false;
        }

        if (!string.Equals(offer.TraderId, TonyTraderId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(offer.OfferKey))
        {
            YATMLogger.Log("[GeneratedOffers] Skipped offer with missing OfferKey.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(offer.OfferId))
        {
            YATMLogger.Log($"[GeneratedOffers] Skipped {offer.OfferKey}: missing OfferId.");
            return false;
        }

        return true;
    }

    private PriceConfigItem? NormalizePriceConfig(YATMGeneratedOfferDefinition offer, SettingsConfig settings)
    {
        var targetTpl = ResolveOfferTpl(offer);
        if (string.IsNullOrWhiteSpace(targetTpl))
        {
            targetTpl = offer.TplId ?? string.Empty;
        }

        var priceConfig = offer.PriceConfig ?? new PriceConfigItem
        {
            TplId = targetTpl,
            OfferId = offer.OfferId,
            ItemName = offer.ItemName,
            Currency = "RUB",
            CashOnly = true,
            AlwaysBarter = false,
            AlwaysInStock = false,
            BarterScheme = []
        };

        priceConfig.OfferId = offer.OfferId;

        if (string.IsNullOrWhiteSpace(priceConfig.TplId))
        {
            priceConfig.TplId = targetTpl;
        }

        if (string.IsNullOrWhiteSpace(priceConfig.ItemName))
        {
            priceConfig.ItemName = !string.IsNullOrWhiteSpace(offer.ItemName)
                ? offer.ItemName
                : ResolveItemName(priceConfig.TplId);
        }

        var hasHardWrittenBarter = HasUsableBarterScheme(priceConfig);
        if (!hasHardWrittenBarter && priceConfig.BarterScheme == null)
        {
            priceConfig.BarterScheme = [];
        }

        if (settings.AutoPriceGeneratedOffers && priceConfig.Price <= 0)
        {
            priceConfig.Price = ResolveGeneratedPrice(priceConfig.TplId, settings);
            priceConfig.Currency = string.IsNullOrWhiteSpace(priceConfig.Currency)
                ? "RUB"
                : priceConfig.Currency;

            YATMLogger.LogDebug($"[GeneratedOffers] Auto-priced {priceConfig.ItemName} ({priceConfig.TplId}) at {priceConfig.Price} {priceConfig.Currency}.");
        }

        if (priceConfig.Price <= 0)
        {
            priceConfig.Price = 1;
        }

        return priceConfig;
    }

    private void AddPresetOffer(
        JsonObject generatedAssortJson,
        YATMGeneratedOfferDefinition offer,
        SettingsConfig settings,
        PriceConfigItem? priceConfig)
    {
        var presets = databaseServer.GetTables().Globals.ItemPresets;

        if (string.IsNullOrWhiteSpace(offer.PresetId) || !presets.TryGetValue(offer.PresetId, out var preset))
        {
            YATMLogger.Log($"[GeneratedOffers] Preset not found for {offer.OfferKey}: {offer.PresetId}");
            return;
        }

        if (preset.Items == null || preset.Items.Count == 0)
        {
            YATMLogger.Log($"[GeneratedOffers] Preset has no items for {offer.OfferKey}: {offer.PresetId}");
            return;
        }

        var oldRootId = ReadObjectString(preset, "Parent")
                        ?? ReadObjectString(preset, "_parent")
                        ?? ReadObjectString(preset.Items[0], "Id")
                        ?? ReadObjectString(preset.Items[0], "_id");

        if (string.IsNullOrWhiteSpace(oldRootId))
        {
            YATMLogger.Log($"[GeneratedOffers] Could not resolve preset root for {offer.OfferKey}: {offer.PresetId}");
            return;
        }

        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [oldRootId] = offer.OfferId
        };

        foreach (var presetItem in preset.Items)
        {
            var oldId = ReadObjectString(presetItem, "Id") ?? ReadObjectString(presetItem, "_id");
            if (string.IsNullOrWhiteSpace(oldId))
            {
                continue;
            }

            if (!idMap.ContainsKey(oldId))
            {
                idMap[oldId] = MakeDeterministicMongoId($"{offer.OfferKey}:{oldId}");
            }
        }

        foreach (var presetItem in preset.Items)
        {
            var oldId = ReadObjectString(presetItem, "Id") ?? ReadObjectString(presetItem, "_id");
            if (string.IsNullOrWhiteSpace(oldId) || !idMap.TryGetValue(oldId, out var newId))
            {
                continue;
            }

            var itemJson = JsonSerializer.SerializeToNode(presetItem, JsonOptions) as JsonObject ?? new JsonObject();
            itemJson["_id"] = newId;

            if (string.Equals(oldId, oldRootId, StringComparison.OrdinalIgnoreCase))
            {
                itemJson["parentId"] = "hideout";
                itemJson["slotId"] = "hideout";
                EnsureRootUpd(itemJson, offer, settings);
            }
            else
            {
                var oldParentId = ReadJsonString(itemJson, "parentId")
                                  ?? ReadJsonString(itemJson, "ParentId");

                if (!string.IsNullOrWhiteSpace(oldParentId) && idMap.TryGetValue(oldParentId, out var newParentId))
                {
                    itemJson["parentId"] = newParentId;
                }
            }

            GetItemsArray(generatedAssortJson).Add(itemJson);
        }

        AddDefaultCashSchemeAndLoyalty(generatedAssortJson, offer, priceConfig);
        YATMLogger.LogDebug($"[GeneratedOffers] Generated preset offer: {offer.ItemName} ({offer.OfferId})");
    }

    private void AddRawAssort(
        JsonObject generatedAssortJson,
        TraderAssort rawAssort,
        List<PriceConfigItem> priceConfigs,
        SettingsConfig settings)
    {
        var rawNode = JsonSerializer.SerializeToNode(rawAssort, JsonOptions) as JsonObject;
        if (rawNode == null)
        {
            return;
        }

        var rawItems = GetArrayByAnyName(rawNode, "items", "Items");
        if (rawItems != null)
        {
            foreach (var item in rawItems)
            {
                if (item != null)
                {
                    GetItemsArray(generatedAssortJson).Add(item.DeepClone());
                }
            }
        }

        var rawBarterScheme = GetObjectByAnyName(rawNode, "barter_scheme", "BarterScheme");
        if (rawBarterScheme != null)
        {
            var targetBarterScheme = GetObject(generatedAssortJson, "barter_scheme");
            foreach (var scheme in rawBarterScheme)
            {
                targetBarterScheme[scheme.Key] = scheme.Value?.DeepClone();
            }
        }

        var rawLoyalLevelItems = GetObjectByAnyName(rawNode, "loyal_level_items", "LoyalLevelItems");
        if (rawLoyalLevelItems != null)
        {
            var targetLoyalLevelItems = GetObject(generatedAssortJson, "loyal_level_items");
            foreach (var levelItem in rawLoyalLevelItems)
            {
                targetLoyalLevelItems[levelItem.Key] = levelItem.Value?.DeepClone();
            }
        }

        AddPriceConfigsFromRawAssort(rawAssort, priceConfigs, settings);
        YATMLogger.LogDebug($"[GeneratedOffers] Added old-style raw assort rows: {rawItems?.Count ?? 0} item rows.");
    }

    private void AddPriceConfigsFromRawAssort(
        TraderAssort rawAssort,
        List<PriceConfigItem> priceConfigs,
        SettingsConfig settings)
    {
        foreach (var item in rawAssort.Items)
        {
            if (item.ParentId != "hideout" || string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            var tpl = YATMConfig.GetTemplateId(item);
            if (string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            var copiedBarterScheme = CopyPaymentScheme(rawAssort, item.Id);
            var firstPayment = copiedBarterScheme.FirstOrDefault()?.FirstOrDefault();
            var firstPaymentIsCash = firstPayment != null && YATMConfig.IsCurrencyTemplate(firstPayment.TplId);
            var hasHardWrittenBarter = HasNonCurrencyPayment(copiedBarterScheme);

            var price = firstPaymentIsCash
                ? firstPayment!.Count
                : ResolveGeneratedPrice(tpl, settings);

            priceConfigs.Add(new PriceConfigItem
            {
                OfferId = item.Id,
                TplId = tpl,
                ItemName = ResolveItemName(tpl),
                Price = price <= 0 ? 1 : price,
                Currency = firstPaymentIsCash ? YATMConfig.TemplateToCurrency(firstPayment!.TplId) : "RUB",
                CashOnly = !hasHardWrittenBarter,
                AlwaysBarter = false,
                AlwaysInStock = false,
                BarterScheme = copiedBarterScheme
            });
        }
    }

    private List<List<PaymentConfigItem>> CopyPaymentScheme(TraderAssort rawAssort, string offerId)
    {
        var copiedBarterScheme = new List<List<PaymentConfigItem>>();

        if (!rawAssort.BarterScheme.TryGetValue(offerId, out var schemeList) || schemeList == null)
        {
            return copiedBarterScheme;
        }

        foreach (var schemeOption in schemeList)
        {
            var copiedOption = new List<PaymentConfigItem>();

            foreach (var component in schemeOption)
            {
                var componentTpl = component.Template.ToString();

                copiedOption.Add(new PaymentConfigItem
                {
                    TplId = componentTpl,
                    ItemName = ResolveItemName(componentTpl),
                    Count = component.Count ?? 0
                });
            }

            copiedBarterScheme.Add(copiedOption);
        }

        return copiedBarterScheme;
    }

    private static void AddSimpleItemOffer(
        JsonObject generatedAssortJson,
        YATMGeneratedOfferDefinition offer,
        SettingsConfig settings,
        PriceConfigItem? priceConfig)
    {
        if (string.IsNullOrWhiteSpace(offer.TplId))
        {
            YATMLogger.Log($"[GeneratedOffers] Skipped simple item {offer.OfferKey}: missing TplId.");
            return;
        }

        var itemJson = new JsonObject
        {
            ["_id"] = offer.OfferId,
            ["_tpl"] = offer.TplId,
            ["parentId"] = "hideout",
            ["slotId"] = "hideout"
        };

        EnsureRootUpd(itemJson, offer, settings);
        GetItemsArray(generatedAssortJson).Add(itemJson);

        AddDefaultCashSchemeAndLoyalty(generatedAssortJson, offer, priceConfig);
        YATMLogger.LogDebug($"[GeneratedOffers] Generated simple item offer: {offer.ItemName} ({offer.OfferId})");
    }

    private static void EnsureRootUpd(JsonObject itemJson, YATMGeneratedOfferDefinition offer, SettingsConfig settings)
    {
        var upd = itemJson["upd"] as JsonObject ?? new JsonObject();
        upd["StackObjectsCount"] = ResolveGeneratedStackCount(offer, settings);
        upd["UnlimitedCount"] = offer.UnlimitedCount;
        upd["BuyRestrictionCurrent"] = 0;

        var buyRestrictionMax = ResolveGeneratedBuyRestrictionMax(offer, settings);
        if (buyRestrictionMax > 0)
        {
            upd["BuyRestrictionMax"] = buyRestrictionMax;
        }

        itemJson["upd"] = upd;
    }

    private static int ResolveGeneratedStackCount(YATMGeneratedOfferDefinition offer, SettingsConfig settings)
    {
        var requested = offer.StackObjectsCount > 0 ? offer.StackObjectsCount : 1;
        return settings.GeneratedOfferMaxStackObjectsCount > 0
            ? Math.Min(requested, settings.GeneratedOfferMaxStackObjectsCount)
            : requested;
    }

    private static int ResolveGeneratedBuyRestrictionMax(YATMGeneratedOfferDefinition offer, SettingsConfig settings)
    {
        var requested = offer.BuyRestrictionMax;
        if (requested <= 0)
        {
            return 0;
        }

        return settings.GeneratedOfferMaxBuyRestrictionMax > 0
            ? Math.Min(requested, settings.GeneratedOfferMaxBuyRestrictionMax)
            : requested;
    }

    private static void AddDefaultCashSchemeAndLoyalty(
        JsonObject generatedAssortJson,
        YATMGeneratedOfferDefinition offer,
        PriceConfigItem? priceConfig)
    {
        var price = priceConfig?.Price ?? 1;
        if (price <= 0)
        {
            price = 1;
        }

        var currencyTpl = YATMConfig.CurrencyToTemplate(priceConfig?.Currency ?? "RUB");

        var barterScheme = GetObject(generatedAssortJson, "barter_scheme");
        barterScheme[offer.OfferId] = new JsonArray
        {
            new JsonArray
            {
                new JsonObject
                {
                    ["_tpl"] = currencyTpl,
                    ["count"] = price
                }
            }
        };

        var loyalLevelItems = GetObject(generatedAssortJson, "loyal_level_items");
        loyalLevelItems[offer.OfferId] = Math.Clamp(offer.LoyaltyLevel, 1, 4);
    }

    private string ResolveOfferTpl(YATMGeneratedOfferDefinition offer)
    {
        if (!string.IsNullOrWhiteSpace(offer.TplId))
        {
            return offer.TplId!;
        }

        if (string.IsNullOrWhiteSpace(offer.PresetId))
        {
            return string.Empty;
        }

        var presets = databaseServer.GetTables().Globals.ItemPresets;
        if (!presets.TryGetValue(offer.PresetId, out var preset) || preset.Items == null || preset.Items.Count == 0)
        {
            return string.Empty;
        }

        var rootId = ReadObjectString(preset, "Parent")
                     ?? ReadObjectString(preset, "_parent")
                     ?? ReadObjectString(preset.Items[0], "Id")
                     ?? ReadObjectString(preset.Items[0], "_id");

        var root = preset.Items.FirstOrDefault(x =>
        {
            var id = ReadObjectString(x, "Id") ?? ReadObjectString(x, "_id");
            return !string.IsNullOrWhiteSpace(id) && string.Equals(id, rootId, StringComparison.OrdinalIgnoreCase);
        }) ?? preset.Items[0];

        return YATMConfig.GetTemplateId(root) ?? string.Empty;
    }

    private static bool IsPairedAmmoLooseConfig(PriceConfigItem? priceConfig)
    {
        return priceConfig != null
            && !string.IsNullOrWhiteSpace(priceConfig.AmmoBarterPackTplId);
    }

    private bool IsLooseAmmoOfferConfig(PriceConfigItem? priceConfig)
    {
        if (priceConfig == null)
        {
            return false;
        }

        if (IsPairedAmmoLooseConfig(priceConfig))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(priceConfig.TplId)
            && (IsTplInTemplateTree(priceConfig.TplId, AmmoBaseClassIds)
                || GetKnownAmmoPackSizeForLooseAmmoTpl(priceConfig.TplId) > 1))
        {
            return true;
        }

        return LooksLikeLooseAmmoItem(priceConfig.ItemName);
    }

    private static bool IsPairedAmmoPackHelperConfig(
        PriceConfigItem priceConfig,
        HashSet<string> pairedAmmoPackOfferIds,
        HashSet<string> pairedAmmoPackTplIds)
    {
        if (IsPairedAmmoLooseConfig(priceConfig))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(priceConfig.OfferId)
            && pairedAmmoPackOfferIds.Contains(priceConfig.OfferId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(priceConfig.TplId)
            && pairedAmmoPackTplIds.Contains(priceConfig.TplId);
    }

    private static void RemoveStandalonePairedAmmoPackPriceConfigs(List<PriceConfigItem> priceConfigs)
    {
        var pairedAmmoPackOfferIds = priceConfigs
            .Where(IsPairedAmmoLooseConfig)
            .Select(x => x.PackOfferId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pairedAmmoPackTplIds = priceConfigs
            .Where(IsPairedAmmoLooseConfig)
            .Select(x => x.AmmoBarterPackTplId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (pairedAmmoPackOfferIds.Count == 0 && pairedAmmoPackTplIds.Count == 0)
        {
            return;
        }

        var removed = priceConfigs.RemoveAll(x => IsPairedAmmoPackHelperConfig(x, pairedAmmoPackOfferIds, pairedAmmoPackTplIds));
        if (removed > 0)
        {
            YATMLogger.LogDebug($"[GeneratedOffers] Removed {removed} standalone ammo-pack helper price row(s); paired ammo barter is controlled by the loose ammo row.");
        }
    }

    private double ResolveAutoBarterTargetValue(PriceConfigItem priceConfig, SettingsConfig settings, TraderAssort? currentAssort)
    {
        var looseUnitValue = priceConfig.Price > 0
            ? priceConfig.Price
            : ResolveGeneratedPrice(priceConfig.TplId, settings);

        looseUnitValue = Math.Max(1, looseUnitValue);

        var targetCategory = ResolveGeneratedBarterCategory(priceConfig, settings);
        var usesPackBasis = IsLooseAmmoOfferConfig(priceConfig)
            || string.Equals(priceConfig.BarterSchemeValueBasis, "Pack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetCategory, "AmmoPack", StringComparison.OrdinalIgnoreCase);

        if (!usesPackBasis)
        {
            return looseUnitValue;
        }

        var packSize = ResolveAmmoPackSizeForGeneratedBarter(priceConfig, currentAssort);
        var loosePackValue = looseUnitValue * Math.Max(1, packSize);
        var packTplValue = !string.IsNullOrWhiteSpace(priceConfig.AmmoBarterPackTplId)
            ? ResolveGeneratedPrice(priceConfig.AmmoBarterPackTplId, settings)
            : 0;

        var targetValue = Math.Max(loosePackValue, packTplValue);
        targetValue = Math.Max(1, targetValue);

        YATMLogger.LogDebug(
            $"[GeneratedBarters] Ammo barter value uses pack basis: {priceConfig.ItemName} | " +
            $"LooseUnit {looseUnitValue} | PackSize {packSize} | Target {targetValue}");

        return targetValue;
    }

    private string ResolveGeneratedBarterTargetBasis(PriceConfigItem priceConfig, SettingsConfig settings, TraderAssort? currentAssort)
    {
        var targetCategory = ResolveGeneratedBarterCategory(priceConfig, settings);
        var usesPackBasis = IsLooseAmmoOfferConfig(priceConfig)
            || string.Equals(priceConfig.BarterSchemeValueBasis, "Pack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetCategory, "AmmoPack", StringComparison.OrdinalIgnoreCase);

        if (!usesPackBasis)
        {
            return "Unit";
        }

        var packSize = ResolveAmmoPackSizeForGeneratedBarter(priceConfig, currentAssort);
        return $"Pack x{Math.Max(1, packSize)}";
    }

    private int ResolveAmmoPackSizeForGeneratedBarter(PriceConfigItem priceConfig, TraderAssort? currentAssort)
    {
        if (priceConfig.AmmoBarterPackSize > 0)
        {
            return priceConfig.AmmoBarterPackSize;
        }

        if (currentAssort != null)
        {
            var packOffer = currentAssort.Items.FirstOrDefault(x =>
                x.ParentId == "hideout"
                && !string.IsNullOrWhiteSpace(priceConfig.PackOfferId)
                && string.Equals(x.Id, priceConfig.PackOfferId, StringComparison.OrdinalIgnoreCase));

            if (packOffer == null && !string.IsNullOrWhiteSpace(priceConfig.AmmoBarterPackTplId))
            {
                packOffer = currentAssort.Items.FirstOrDefault(x =>
                    x.ParentId == "hideout"
                    && string.Equals(YATMConfig.GetTemplateId(x), priceConfig.AmmoBarterPackTplId, StringComparison.OrdinalIgnoreCase));
            }

            var stackObjectsCount = packOffer?.Upd?.StackObjectsCount;
            if (stackObjectsCount > 0)
            {
                return Math.Max(1, (int)Math.Round(stackObjectsCount.Value, MidpointRounding.AwayFromZero));
            }
        }

        var sizeFromPackTpl = GetKnownAmmoPackSizeForGeneratedBarter(priceConfig.AmmoBarterPackTplId ?? string.Empty);
        if (sizeFromPackTpl > 1)
        {
            return sizeFromPackTpl;
        }

        var sizeFromLooseTpl = GetKnownAmmoPackSizeForLooseAmmoTpl(priceConfig.TplId);
        if (sizeFromLooseTpl > 1)
        {
            return sizeFromLooseTpl;
        }

        var sizeFromName = TryParseAmmoPackSize(priceConfig.AmmoBarterPackItemName)
            ?? TryParseAmmoPackSize(priceConfig.ItemName);
        if (sizeFromName.HasValue && sizeFromName.Value > 1)
        {
            return sizeFromName.Value;
        }

        var sizeFromCaliberName = InferAmmoPackSizeFromLooseAmmoName(priceConfig.ItemName);
        if (sizeFromCaliberName > 1)
        {
            return sizeFromCaliberName;
        }

        return Math.Max(1, sizeFromPackTpl);
    }

    private static int InferAmmoPackSizeFromLooseAmmoName(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return 1;
        }

        var name = itemName.Trim().ToLowerInvariant();

        if (name.StartsWith("12.7x55mm", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        if (name.StartsWith("12/70", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("20/70", StringComparison.OrdinalIgnoreCase))
        {
            return 25;
        }

        if (name.StartsWith("5.45x39mm", StringComparison.OrdinalIgnoreCase))
        {
            return 120;
        }

        if (name.StartsWith("9x18mm", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (name.StartsWith("9x19mm", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("rip") ? 20 : 50;
        }

        if (name.StartsWith(".366", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("7.62x39mm", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("7.62x54mm", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("9x39mm", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        return 1;
    }

    private static int GetKnownAmmoPackSizeForGeneratedBarter(string packTpl)
    {
        if (string.IsNullOrWhiteSpace(packTpl))
        {
            return 1;
        }

        if (IsTplMatch(packTpl,
            "648983d6b5a2df1c815a04ec",
            "6a4933e1fb1eff152bd649b9"))
        {
            return 10;
        }

        if (IsTplMatch(packTpl,
            "6a493587fb1eff152bd649be",
            "6a493587fb1eff152bd649c0",
            "6a493587fb1eff152bd649c2",
            "6a493587fb1eff152bd649c5"))
        {
            return 30;
        }

        if (IsTplMatch(packTpl,
            "657023f81419851aef03e6f1",
            "657024011419851aef03e6f4",
            "6a4933e1fb1eff152bd649bb",
            "6489851fc827d4637f01791b",
            "64acea16c4eda9354b0226b0",
            "64ace9f9c4eda9354b0226aa",
            "5c1127bdd174af44217ab8b9",
            "65702577cfc010a0f5006a2c",
            "648984b8d5b4df6140000a1a",
            "560d75f54bdc2da74d8b4573",
            "6489854673c462723909a14e",
            "657025dabfc87b3a34093256",
            "657025dfcfc010a0f5006a3b",
            "657025cfbfc87b3a34093253",
            "6a4933e1fb1eff152bd649bb",
            "6a493587fb1eff152bd649c7",
            "6a493587fb1eff152bd649c9",
            "6a493587fb1eff152bd649cb",
            "6a493587fb1eff152bd649cd",
            "6a493587fb1eff152bd649cf"))
        {
            return 20;
        }

        if (IsTplMatch(packTpl,
            "64898838d5b4df6140000a20",
            "65702474bfc87b3a34093226",
            "657024361419851aef03e6fa",
            "6a4933e1fb1eff152bd649b6"))
        {
            return 25;
        }

        if (IsTplMatch(packTpl,
            "648987d673c462723909a151",
            "65702591c5d7d4cb4d07857c",
            "657026341419851aef03e730"))
        {
            return 50;
        }

        if (IsTplMatch(packTpl,
            "657025ebc5d7d4cb4d078588",
            "57372b832459776701014e41",
            "5737292724597765e5728562",
            "57372c21245977670937c6c2",
            "57372d1b2459776862260581"))
        {
            return 120;
        }

        return 1;
    }

    private static int GetKnownAmmoPackSizeForLooseAmmoTpl(string ammoTpl)
    {
        if (string.IsNullOrWhiteSpace(ammoTpl))
        {
            return 1;
        }

        if (IsTplMatch(ammoTpl,
            "6a4933e1fb1eff152bd649ab",
            "6a4933e1fb1eff152bd649a9"))
        {
            return 10;
        }

        if (IsTplMatch(ammoTpl,
            "5f0596629e22f464da6bbdd9",
            "59e655cb86f77411dc52a77b",
            "6a4933e1fb1eff152bd649ac",
            "59e0d99486f7744a32234762",
            "601aa3d2b2bcb34913271e6d",
            "64b7af434b75259c590fa893",
            "5887431f2459777e1612938f",
            "5e023d48186a883be655e551",
            "560d61e84bdc2da74d8b4571",
            "5c0d56a986f774449d5de529",
            "5656d7c34bdc2d9d198b4587",
            "6a4933e1fb1eff152bd649b1",
            "6a4933e1fb1eff152bd649b3",
            "6a427a2e38a6d33bffe9829b",
            "6a427a2e38a6d33bffe98292",
            "6a427a2e38a6d33bffe9829d",
            "6a4933e1fb1eff152bd649b5",
            "6a4933e1fb1eff152bd649b7"))
        {
            return 20;
        }

        if (IsTplMatch(ammoTpl,
            "6a4933e1fb1eff152bd649ad",
            "6a4933e1fb1eff152bd649ae",
            "6a4933e1fb1eff152bd649af",
            "6a4933e1fb1eff152bd649b0"))
        {
            return 30;
        }

        if (IsTplMatch(ammoTpl,
            "57372140245977611f70ee91",
            "5c925fa22e221601da359b7b",
            "5efb0da7a29a85116f6ea05f",
            "56d59d3ad2720bdb418b4577"))
        {
            return 50;
        }

        if (IsTplMatch(ammoTpl,
            "56dfef82d2720bbd668b4567",
            "56dff026d2720bb8668b4567",
            "56dff061d2720bb5668b4567",
            "56dff2ced2720bb4668b4567",
            "5c0d5e4486f77478390952fe",
            "56dff3afd2720bba668b4567"))
        {
            return 120;
        }

        if (IsTplMatch(ammoTpl,
            "560d5e524bdc2d25448b4571",
            "6a4933e1fb1eff152bd649aa"))
        {
            return 25;
        }

        return 1;
    }

    private static int? TryParseAmmoPackSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<count>\d+)\s*(?:pcs|rounds|cartridges)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["count"].Value, out var count) && count > 0)
        {
            return count;
        }

        return null;
    }

    private static bool IsTplMatch(string tpl, params string[] tplIds)
    {
        foreach (var tplId in tplIds)
        {
            if (tpl.Equals(tplId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private List<List<PaymentConfigItem>> GenerateAutoBarterScheme(
        PriceConfigItem priceConfig,
        SettingsConfig settings,
        TraderAssort? currentAssort,
        Dictionary<string, int> barterItemUsage,
        Dictionary<string, int> barterCategoryUsage,
        out string skipReason)
    {
        skipReason = string.Empty;
        var targetTpl = priceConfig.TplId;
        if (string.IsNullOrWhiteSpace(targetTpl))
        {
            skipReason = "missing target tpl id";
            return [];
        }

        var baseValue = ResolveAutoBarterTargetValue(priceConfig, settings, currentAssort);

        var targetValue = ApplyGeneratedValueMode(
            baseValue,
            settings.GeneratedBarterPriceMode,
            settings.GeneratedBarterPriceOffsetPercent);

        targetValue = Math.Max(1, targetValue);

        var targetCategory = ResolveGeneratedBarterCategory(priceConfig, settings);
        var maxDifferentItems = ResolveGeneratedBarterMaxDifferentItems(priceConfig, settings);
        var maxItemCount = Math.Clamp(settings.GeneratedBarterMaxItemCount, 1, 99);
        var minItemPrice = Math.Max(1, settings.GeneratedBarterMinItemPrice);

        var excludedTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            targetTpl
        };

        if (!string.IsNullOrWhiteSpace(priceConfig.AmmoBarterPackTplId))
        {
            excludedTpls.Add(priceConfig.AmmoBarterPackTplId);
        }

        var allCandidates = GetAutoBarterCandidates(settings)
            .Where(x => !excludedTpls.Contains(x.TplId))
            .Where(x => !YATMConfig.IsCurrencyTemplate(x.TplId))
            .ToList();

        if (allCandidates.Count == 0)
        {
            skipReason = "no priced barter candidates loaded after excluding currency and the sold item; check generated_barter_whitelist.jsonc and price tables";
            return [];
        }

        var categoryCandidates = FilterCandidatesForOfferCategory(allCandidates, targetCategory, settings)
            .Where(x => x.Price > 0)
            .ToList();

        var candidates = categoryCandidates
            .Where(x => x.Price >= minItemPrice)
            .Where(x => x.Price <= targetValue * 1.5 || x.Price <= baseValue * 1.5)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = categoryCandidates;
        }

        if (candidates.Count == 0 && !string.Equals(targetCategory, "Default", StringComparison.OrdinalIgnoreCase))
        {
            // Safety fallback: if the matching category profile is too narrow, fall back to the full whitelist
            // rather than failing the barter conversion for that offer.
            candidates = allCandidates
                .Where(x => x.Price >= minItemPrice)
                .Where(x => x.Price <= targetValue * 1.5 || x.Price <= baseValue * 1.5)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            var cheapest = allCandidates.Where(x => x.Price > 0).OrderBy(x => x.Price).FirstOrDefault();
            skipReason = cheapest == null
                ? $"no priced barter candidates available for category {targetCategory}"
                : $"no legal candidates for category {targetCategory}; target {targetValue:0.##} RUB, min component price {minItemPrice} RUB, cheapest loaded candidate is {cheapest.ItemName} at {cheapest.Price:0.##} RUB";
            return [];
        }

        var randomizeSelection = string.Equals(
            settings.GeneratedBarterSelectionMode,
            "Random",
            StringComparison.OrdinalIgnoreCase);

        var seed = !string.IsNullOrWhiteSpace(priceConfig.OfferId)
            ? priceConfig.OfferId!
            : targetTpl;

        if (randomizeSelection)
        {
            seed = $"{seed}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";
        }

        var selected = new List<PaymentConfigItem>();
        var usedTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = targetValue;
        var allowOverTarget = IsGeneratedBarterOverMode(settings);
        var lastFailureReason = string.Empty;

        for (var slot = 0; slot < maxDifferentItems && remaining > 0; slot++)
        {
            if (!allowOverTarget && !HasAffordableCandidate(candidates, usedTpls, remaining))
            {
                lastFailureReason = BuildNoAffordableCandidateReason(candidates, usedTpls, remaining, targetCategory, targetValue, settings);
                break;
            }

            // MaxDifferentItems is a cap, not a target. Prefer using one good barter item
            // with a stack count of 2/3/etc. before spending extra item-type slots.
            var idealValue = Math.Max(1, remaining);

            var pick = PickAutoBarterCandidate(
                candidates,
                usedTpls,
                barterItemUsage,
                idealValue,
                remaining,
                maxItemCount,
                seed,
                slot,
                randomizeSelection,
                settings.GeneratedBarterBalanceItemUsage,
                settings,
                barterCategoryUsage,
                allowOverTarget);

            if (pick == null)
            {
                lastFailureReason = BuildNoSelectableCandidateReason(
                    candidates,
                    usedTpls,
                    barterItemUsage,
                    barterCategoryUsage,
                    settings,
                    idealValue,
                    remaining,
                    maxItemCount,
                    allowOverTarget);
                break;
            }

            var count = CalculateAutoBarterCount(pick, idealValue, remaining, maxItemCount, allowOverTarget);
            if (count <= 0)
            {
                lastFailureReason = $"picked {pick.ItemName}, but quantity resolved to 0 for remaining {remaining:0.##} RUB in {settings.GeneratedBarterPriceMode} mode";
                usedTpls.Add(pick.TplId);
                continue;
            }

            var componentValue = pick.Price * count;

            selected.Add(new PaymentConfigItem
            {
                TplId = pick.TplId,
                ItemName = pick.ItemName,
                Count = count
            });

            usedTpls.Add(pick.TplId);

            if (settings.GeneratedBarterBalanceItemUsage)
            {
                AddGeneratedBarterUsage(barterItemUsage, barterCategoryUsage, pick, count);
            }

            remaining -= componentValue;

            if (allowOverTarget && GetSelectedBarterValue(selected, candidates) >= targetValue)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            skipReason = string.IsNullOrWhiteSpace(lastFailureReason)
                ? $"no barter ingredients selected for category {targetCategory}; target value {targetValue:0.##} RUB"
                : lastFailureReason;
            return [];
        }

        if (allowOverTarget)
        {
            TryRaiseGeneratedBarterToAtLeastTarget(
                selected,
                candidates,
                usedTpls,
                barterItemUsage,
                targetValue,
                maxDifferentItems,
                maxItemCount,
                settings.GeneratedBarterBalanceItemUsage,
                settings,
                barterCategoryUsage);
        }

        return [selected];
    }

    private string BuildNoAffordableCandidateReason(
        IReadOnlyList<BarterCandidate> candidates,
        ISet<string> usedTpls,
        double remainingValue,
        string targetCategory,
        double targetValue,
        SettingsConfig settings)
    {
        var unused = candidates.Where(x => !usedTpls.Contains(x.TplId) && x.Price > 0).ToList();
        var cheapest = unused.OrderBy(x => x.Price).FirstOrDefault();
        if (cheapest == null)
        {
            return $"no unused candidates left for category {targetCategory}; target {targetValue:0.##} RUB";
        }

        return $"no affordable candidate left in {settings.GeneratedBarterPriceMode} mode for remaining {remainingValue:0.##} RUB; cheapest unused candidate is {cheapest.ItemName} ({cheapest.TplId}) at {cheapest.Price:0.##} RUB";
    }

    private string BuildNoSelectableCandidateReason(
        IReadOnlyList<BarterCandidate> candidates,
        ISet<string> usedTpls,
        IReadOnlyDictionary<string, int> barterItemUsage,
        IReadOnlyDictionary<string, int> barterCategoryUsage,
        SettingsConfig settings,
        double idealValue,
        double remainingValue,
        int maxItemCount,
        bool allowOverTarget)
    {
        var unused = candidates.Where(x => !usedTpls.Contains(x.TplId)).ToList();
        if (unused.Count == 0)
        {
            return "all candidate tpl IDs were already used in this barter recipe";
        }

        var quantityEligible = unused
            .Select(x => new
            {
                Candidate = x,
                Count = CalculateAutoBarterCount(x, idealValue, remainingValue, maxItemCount, allowOverTarget)
            })
            .Where(x => x.Count > 0)
            .ToList();

        if (quantityEligible.Count == 0)
        {
            var cheapest = unused.Where(x => x.Price > 0).OrderBy(x => x.Price).FirstOrDefault();
            return cheapest == null
                ? "no unused candidates had a valid price"
                : $"no candidate quantity fit remaining {remainingValue:0.##} RUB in {settings.GeneratedBarterPriceMode} mode; cheapest unused candidate is {cheapest.ItemName} ({cheapest.TplId}) at {cheapest.Price:0.##} RUB";
        }

        var capEligible = quantityEligible
            .Where(x => IsBarterCandidateWithinUsageCaps(x.Candidate, barterItemUsage, barterCategoryUsage, settings, x.Count))
            .ToList();

        if (capEligible.Count == 0)
        {
            var highestUsage = quantityEligible
                .Select(x =>
                {
                    barterItemUsage.TryGetValue(x.Candidate.TplId, out var itemUsage);
                    barterCategoryUsage.TryGetValue(NormalizeBarterPoolCategoryName(x.Candidate.Category), out var categoryUsage);
                    return new { x.Candidate, ItemUsage = itemUsage, CategoryUsage = categoryUsage };
                })
                .OrderByDescending(x => x.ItemUsage + x.CategoryUsage)
                .First();

            return $"all quantity-valid candidates hit item/category use caps; highest used candidate is {highestUsage.Candidate.ItemName} ({highestUsage.Candidate.TplId}) item uses {highestUsage.ItemUsage}, category {NormalizeBarterPoolCategoryName(highestUsage.Candidate.Category)} uses {highestUsage.CategoryUsage}";
        }

        var weightEligible = capEligible
            .Where(x =>
            {
                barterItemUsage.TryGetValue(x.Candidate.TplId, out var itemUsage);
                barterCategoryUsage.TryGetValue(NormalizeBarterPoolCategoryName(x.Candidate.Category), out var categoryUsage);
                return CalculateEffectiveBarterCandidateWeight(x.Candidate, settings, itemUsage, categoryUsage, settings.GeneratedBarterBalanceItemUsage) > 0;
            })
            .ToList();

        if (weightEligible.Count == 0)
        {
            return "all cap-valid candidates resolved to zero effective weight";
        }

        return $"no selectable candidate found after scoring; unused {unused.Count}, quantity-valid {quantityEligible.Count}, cap-valid {capEligible.Count}, weight-valid {weightEligible.Count}";
    }

    private static bool IsGeneratedBarterOverMode(SettingsConfig settings)
    {
        return string.Equals(settings.GeneratedBarterPriceMode, "Over", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAffordableCandidate(
        IEnumerable<BarterCandidate> candidates,
        ISet<string> usedTpls,
        double remainingValue)
    {
        return candidates.Any(x => !usedTpls.Contains(x.TplId)
            && CalculateAutoBarterCount(x, remainingValue, remainingValue, 99, false) > 0);
    }

    private static int CalculateAutoBarterCount(
        BarterCandidate candidate,
        double idealValue,
        double remainingValue,
        int globalMaxItemCount,
        bool allowOverTarget)
    {
        if (candidate.Price <= 0)
        {
            return 0;
        }

        var minCount = Math.Max(1, candidate.MinQty);
        var maxCount = ResolveCandidateMaxItemCount(candidate, globalMaxItemCount);
        if (maxCount < minCount)
        {
            maxCount = minCount;
        }

        var count = (int)Math.Round(idealValue / candidate.Price, MidpointRounding.AwayFromZero);
        count = Math.Clamp(count, minCount, maxCount);

        if (allowOverTarget)
        {
            return count;
        }

        var maxAffordableCount = (int)Math.Floor(remainingValue / candidate.Price);
        if (maxAffordableCount < minCount)
        {
            return 0;
        }

        return Math.Clamp(count, minCount, Math.Min(maxCount, maxAffordableCount));
    }

    private static int ResolveCandidateMaxItemCount(BarterCandidate candidate, int globalMaxItemCount)
    {
        globalMaxItemCount = Math.Clamp(globalMaxItemCount, 1, 99);

        if (candidate.MaxQty > 0)
        {
            return Math.Clamp(candidate.MaxQty, 1, globalMaxItemCount);
        }

        return globalMaxItemCount;
    }

    private static double GetSelectedBarterValue(
        IEnumerable<PaymentConfigItem> selected,
        IReadOnlyList<BarterCandidate> candidates)
    {
        var pricesByTpl = candidates
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Price, StringComparer.OrdinalIgnoreCase);

        double total = 0;
        foreach (var item in selected)
        {
            if (pricesByTpl.TryGetValue(item.TplId, out var price))
            {
                total += price * Math.Max(1, item.Count);
            }
        }

        return total;
    }

    private static void AddGeneratedBarterUsage(
        Dictionary<string, int> barterItemUsage,
        Dictionary<string, int> barterCategoryUsage,
        BarterCandidate candidate,
        double count)
    {
        var usageToAdd = Math.Max(1, (int)Math.Ceiling(count));

        barterItemUsage.TryGetValue(candidate.TplId, out var currentItemUsage);
        // Count larger component stacks as heavier usage so one item does not
        // become the answer for every generated barter recipe.
        barterItemUsage[candidate.TplId] = currentItemUsage + usageToAdd;

        var category = NormalizeBarterPoolCategoryName(candidate.Category);
        barterCategoryUsage.TryGetValue(category, out var currentCategoryUsage);
        barterCategoryUsage[category] = currentCategoryUsage + usageToAdd;
    }

    private void TryRaiseGeneratedBarterToAtLeastTarget(
        List<PaymentConfigItem> selected,
        IReadOnlyList<BarterCandidate> candidates,
        HashSet<string> usedTpls,
        Dictionary<string, int> barterItemUsage,
        double targetValue,
        int maxDifferentItems,
        int maxItemCount,
        bool balanceItemUsage,
        SettingsConfig settings,
        Dictionary<string, int> barterCategoryUsage)
    {
        var pricesByTpl = candidates
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var currentValue = GetSelectedBarterValue(selected, candidates);
        if (currentValue >= targetValue)
        {
            return;
        }

        while (currentValue < targetValue)
        {
            PaymentConfigItem? existingToIncrement = null;
            BarterCandidate? existingCandidate = null;
            BarterCandidate? newCandidate = null;
            double bestNewTotal = double.MaxValue;
            var bestUsage = int.MaxValue;

            foreach (var item in selected)
            {
                if (!pricesByTpl.TryGetValue(item.TplId, out var candidate))
                {
                    continue;
                }

                var maxForCandidate = ResolveCandidateMaxItemCount(candidate, maxItemCount);
                if (item.Count >= maxForCandidate)
                {
                    continue;
                }

                if (!IsBarterCandidateWithinUsageCaps(candidate, barterItemUsage, barterCategoryUsage, settings, 1))
                {
                    continue;
                }

                var newTotal = currentValue + candidate.Price;
                var usage = balanceItemUsage && barterItemUsage.TryGetValue(candidate.TplId, out var existingUsage)
                    ? existingUsage
                    : 0;

                if (newTotal >= targetValue && (newTotal < bestNewTotal || (Math.Abs(newTotal - bestNewTotal) < 0.01 && usage < bestUsage)))
                {
                    existingToIncrement = item;
                    existingCandidate = candidate;
                    newCandidate = null;
                    bestNewTotal = newTotal;
                    bestUsage = usage;
                }
            }

            if (selected.Count < maxDifferentItems)
            {
                foreach (var candidate in candidates.Where(x => !usedTpls.Contains(x.TplId) && x.Price > 0))
                {
                    var count = CalculateAutoBarterCount(candidate, targetValue - currentValue, targetValue - currentValue, maxItemCount, true);
                    if (count <= 0 || !IsBarterCandidateWithinUsageCaps(candidate, barterItemUsage, barterCategoryUsage, settings, count))
                    {
                        continue;
                    }

                    var newTotal = currentValue + candidate.Price * count;
                    var usage = balanceItemUsage && barterItemUsage.TryGetValue(candidate.TplId, out var existingUsage)
                        ? existingUsage
                        : 0;

                    if (newTotal >= targetValue && (newTotal < bestNewTotal || (Math.Abs(newTotal - bestNewTotal) < 0.01 && usage < bestUsage)))
                    {
                        existingToIncrement = null;
                        existingCandidate = null;
                        newCandidate = candidate;
                        bestNewTotal = newTotal;
                        bestUsage = usage;
                    }
                }
            }

            if (existingToIncrement != null && existingCandidate != null)
            {
                existingToIncrement.Count += 1;
                currentValue = bestNewTotal;
                if (balanceItemUsage)
                {
                    AddGeneratedBarterUsage(barterItemUsage, barterCategoryUsage, existingCandidate, 1);
                }
                continue;
            }

            if (newCandidate != null)
            {
                var count = CalculateAutoBarterCount(newCandidate, targetValue - currentValue, targetValue - currentValue, maxItemCount, true);
                selected.Add(new PaymentConfigItem
                {
                    TplId = newCandidate.TplId,
                    ItemName = newCandidate.ItemName,
                    Count = count
                });
                usedTpls.Add(newCandidate.TplId);
                currentValue += newCandidate.Price * count;
                if (balanceItemUsage)
                {
                    AddGeneratedBarterUsage(barterItemUsage, barterCategoryUsage, newCandidate, count);
                }
                continue;
            }

            // No legal way to reach or exceed the target with current category/count limits.
            return;
        }
    }

    private int ResolveGeneratedBarterMaxDifferentItems(PriceConfigItem priceConfig, SettingsConfig settings)
    {
        var category = ResolveGeneratedBarterCategory(priceConfig, settings);
        var profile = GetGeneratedBarterOfferCategoryProfile(category, settings);
        var profileMaxDifferentItems = profile?.MaxDifferentItems.GetValueOrDefault() ?? 0;
        var limit = profileMaxDifferentItems > 0
            ? profileMaxDifferentItems
            : category switch
            {
                "AmmoPack" => settings.GeneratedBarterAmmoPackMaxDifferentItems,
                "Weapon" => settings.GeneratedBarterWeaponMaxDifferentItems,
                "Medical" => settings.GeneratedBarterMedicalMaxDifferentItems,
                "Headwear" => settings.GeneratedBarterHeadwearMaxDifferentItems,
                "Armor" => settings.GeneratedBarterArmorMaxDifferentItems,
                "CaseOrValuable" => settings.GeneratedBarterCaseValuableMaxDifferentItems,
                _ => settings.GeneratedBarterDefaultMaxDifferentItems > 0
                    ? settings.GeneratedBarterDefaultMaxDifferentItems
                    : settings.GeneratedBarterMaxDifferentItems
            };

        limit = Math.Clamp(limit, 1, 6);
        YATMLogger.LogDebug($"[GeneratedBarters] Category limit: {priceConfig.ItemName} ({priceConfig.TplId}) => {category}, max different barter items {limit}.");
        return limit;
    }

    private string ResolveGeneratedBarterCategory(PriceConfigItem priceConfig, SettingsConfig settings)
    {
        var overrideCategory = NormalizeGeneratedBarterCategory(priceConfig.GeneratedBarterCategoryOverride);
        if (!string.IsNullOrWhiteSpace(overrideCategory))
        {
            return overrideCategory;
        }

        if (IsLooseAmmoOfferConfig(priceConfig))
        {
            return "AmmoPack";
        }

        var tpl = priceConfig.TplId;
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return "Default";
        }

        if (IsTplInTemplateTree(tpl, WeaponBaseClassIds))
        {
            return "Weapon";
        }

        if (IsTplInTemplateTree(tpl, MedicalBaseClassIds))
        {
            return "Medical";
        }

        if (IsTplInTemplateTree(tpl, HeadwearBaseClassIds)
            || LooksLikeHeadwear(priceConfig.ItemName))
        {
            return "Headwear";
        }

        if (IsTplInTemplateTree(tpl, ArmorBaseClassIds))
        {
            return "Armor";
        }

        if (LooksLikeMedicalItem(priceConfig.ItemName))
        {
            return "Medical";
        }

        if (LooksLikeElectronics(priceConfig.ItemName))
        {
            return "Electronics";
        }

        if (LooksLikeToolItem(priceConfig.ItemName))
        {
            return "Tools";
        }

        if (LooksLikeFoodDrink(priceConfig.ItemName))
        {
            return "FoodDrink";
        }

        if (IsTplInTemplateTree(tpl, CaseBaseClassIds)
            || IsTplInTemplateTree(tpl, ValuableBaseClassIds)
            || IsTplListedAsGeneratedBarterValuable(tpl, settings)
            || LooksLikeCaseOrValuable(priceConfig.ItemName))
        {
            return "CaseOrValuable";
        }

        return "Default";
    }

    private static string? NormalizeGeneratedBarterCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var normalized = category.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "ammopack" or "ammo" => "AmmoPack",
            "weapon" or "weapons" or "gun" or "guns" => "Weapon",
            "medical" or "med" or "meds" or "medicine" => "Medical",
            "headwear" or "headset" or "headsets" or "helmet" or "helmets" or "earpiece" or "earpieces" or "earpro" or "ears" => "Headwear",
            "armor" or "armour" or "gear" => "Armor",
            "electronics" or "electronic" or "tech" or "technical" => "Electronics",
            "tool" or "tools" => "Tools",
            "food" or "drink" or "fooddrink" or "foodanddrink" or "fooddrinks" or "ration" or "rations" => "FoodDrink",
            "weaponpart" or "weaponparts" or "attachment" or "attachments" or "gunpart" or "gunparts" => "WeaponParts",
            "junk" or "misc" or "miscellaneous" => "Junk",
            "case" or "cases" or "valuable" or "valuables" or "caseorvaluable" or "casesorvaluables" => "CaseOrValuable",
            "default" or "other" or "rest" => "Default",
            _ => null
        };
    }

    private bool IsTplListedAsGeneratedBarterValuable(string tpl, SettingsConfig settings)
    {
        if (string.IsNullOrWhiteSpace(tpl) || !settings.GeneratedBarterUseWhitelist)
        {
            return false;
        }

        return LoadOrCreateGeneratedBarterWhitelist(settings).Contains(tpl);
    }

    private List<BarterCandidate> FilterCandidatesForOfferCategory(
        IReadOnlyList<BarterCandidate> candidates,
        string offerCategory,
        SettingsConfig settings)
    {
        if (!settings.GeneratedBarterUseWeightedCategories || candidates.Count == 0)
        {
            return candidates.ToList();
        }

        var profile = GetGeneratedBarterOfferCategoryProfile(offerCategory, settings)
                      ?? GetGeneratedBarterOfferCategoryProfile("Default", settings);

        if (profile == null || !profile.Enabled || profile.IngredientCategoryWeights.Count == 0)
        {
            var exactCategory = NormalizeBarterPoolCategoryName(offerCategory);
            var exactMatches = candidates
                .Where(x => string.Equals(NormalizeBarterPoolCategoryName(x.Category), exactCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return exactMatches.Count > 0 ? exactMatches : candidates.ToList();
        }

        var categoryWeights = profile.IngredientCategoryWeights
            .Where(x => x.Value > 0)
            .ToDictionary(
                x => NormalizeBarterPoolCategoryName(x.Key),
                x => Math.Max(0.01, x.Value),
                StringComparer.OrdinalIgnoreCase);

        var filtered = candidates
            .Select(x =>
            {
                var category = NormalizeBarterPoolCategoryName(x.Category);
                if (!categoryWeights.TryGetValue(category, out var categoryWeight))
                {
                    return null;
                }

                var categoryConfig = GetGeneratedBarterCategoryConfig(category, settings);
                if (categoryConfig != null && !categoryConfig.Enabled)
                {
                    return null;
                }

                // Multiply item weight by the sold-offer profile category weight.
                // Example: Electronics offer -> Electronics components get 70x, Tools get 20x.
                return x with
                {
                    Category = category,
                    Weight = Math.Max(0.01, x.Weight) * categoryWeight
                };
            })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        return filtered.Count > 0 ? filtered : candidates.ToList();
    }

    private GeneratedBarterCategoryConfig? GetGeneratedBarterCategoryConfig(string? category, SettingsConfig settings)
    {
        var config = LoadOrCreateGeneratedBarterWhitelistConfig(settings);
        var categoryName = NormalizeBarterPoolCategoryName(category);
        return config.Categories.TryGetValue(categoryName, out var categoryConfig)
            ? categoryConfig
            : null;
    }

    private GeneratedBarterOfferCategoryProfile? GetGeneratedBarterOfferCategoryProfile(string? offerCategory, SettingsConfig settings)
    {
        var config = LoadOrCreateGeneratedBarterWhitelistConfig(settings);
        var normalizedOfferCategory = NormalizeGeneratedBarterCategory(offerCategory)
                                      ?? NormalizeBarterPoolCategoryName(offerCategory);

        if (config.OfferCategoryProfiles.TryGetValue(normalizedOfferCategory, out var profile))
        {
            return profile;
        }

        return config.OfferCategoryProfiles.TryGetValue("Default", out var defaultProfile)
            ? defaultProfile
            : null;
    }

    private static string ResolveBarterCandidateCategory(GeneratedBarterWhitelistItem? item, string itemName)
    {
        if (!string.IsNullOrWhiteSpace(item?.Category)
            && !string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeBarterPoolCategoryName(item.Category);
        }

        var haystack = $"{item?.ItemName} {item?.Notes} {itemName}";

        if (LooksLikeMedicalItem(haystack))
        {
            return "Medical";
        }

        if (LooksLikeElectronics(haystack))
        {
            return "Electronics";
        }

        if (LooksLikeToolItem(haystack))
        {
            return "Tools";
        }

        if (LooksLikeFoodDrink(haystack))
        {
            return "FoodDrink";
        }

        if (LooksLikeWeaponPartItem(haystack))
        {
            return "WeaponParts";
        }

        if (LooksLikeAmmoSupplyItem(haystack))
        {
            return "AmmoSupplies";
        }

        if (LooksLikeCaseOrValuable(haystack))
        {
            return "Valuables";
        }

        return "Junk";
    }

    private static string NormalizeBarterPoolCategoryName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Default";
        }

        var normalized = category.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "electronic" or "electronics" or "tech" or "technical" => "Electronics",
            "tool" or "tools" => "Tools",
            "medical" or "med" or "meds" or "medicine" => "Medical",
            "food" or "drink" or "fooddrink" or "foodanddrink" or "fooddrinks" or "ration" or "rations" => "FoodDrink",
            "valuable" or "valuables" or "case" or "cases" or "caseorvaluable" or "casesorvaluables" => "Valuables",
            "weaponpart" or "weaponparts" or "attachment" or "attachments" or "gunpart" or "gunparts" => "WeaponParts",
            "ammo" or "ammosupply" or "ammosupplies" or "gunpowder" or "powder" => "AmmoSupplies",
            "junk" or "misc" or "miscellaneous" => "Junk",
            "default" or "other" or "rest" => "Default",
            _ => category.Trim()
        };
    }

    private static bool LooksLikeCaseOrValuable(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("case")
            || name.Contains("container")
            || name.Contains("keytool")
            || name.Contains("docs")
            || name.Contains("document")
            || name.Contains("wallet")
            || name.Contains("bitcoin")
            || name.Contains("coin")
            || name.Contains("rolex")
            || name.Contains("roler")
            || name.Contains("gold")
            || name.Contains("prokill")
            || name.Contains("skull")
            || name.Contains("figurine")
            || name.Contains("lion")
            || name.Contains("rooster")
            || name.Contains("raven")
            || name.Contains("cat")
            || name.Contains("horse")
            || name.Contains("vase")
            || name.Contains("teapot")
            || name.Contains("diary")
            || name.Contains("intelligence")
            || name.Contains("graphics card")
            || name.Contains("virtex")
            || name.Contains("vpx")
            || name.Contains("cofdm");
    }

    private static bool LooksLikeHeadwear(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("headset")
            || name.Contains("headphones")
            || name.Contains("earpiece")
            || name.Contains("comtac")
            || name.Contains("m32")
            || name.Contains("rac")
            || name.Contains("gssh")
            || name.Contains("helmet")
            || name.Contains("altyn")
            || name.Contains("mask");
    }

    private static bool LooksLikeElectronics(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("electronics")
            || name.Contains("electronic")
            || name.Contains("tech")
            || name.Contains("circuit")
            || name.Contains("wires")
            || name.Contains("wire")
            || name.Contains("cable")
            || name.Contains("cord")
            || name.Contains("powercord")
            || name.Contains("cpu")
            || name.Contains("fan")
            || name.Contains("capacitor")
            || name.Contains("battery")
            || name.Contains("greenbat")
            || name.Contains("rechargeable")
            || name.Contains("graphics card")
            || name.Contains("gpu")
            || name.Contains("virtex")
            || name.Contains("vpx")
            || name.Contains("cofdm")
            || name.Contains("ssd")
            || name.Contains("sas drive")
            || name.Contains("flash drive")
            || name.Contains("flash storage")
            || name.Contains("magnetic tape")
            || name.Contains("military power filter")
            || name.Contains("tetriz");
    }

    private static bool LooksLikeToolItem(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("tool")
            || name.Contains("pliers")
            || name.Contains("screwdriver")
            || name.Contains("wrench")
            || name.Contains("ratchet")
            || name.Contains("pipe grip")
            || name.Contains("pressure gauge")
            || name.Contains("thermometer")
            || name.Contains("awl")
            || name.Contains("nails")
            || name.Contains("bolts")
            || name.Contains("screw nut")
            || name.Contains("screws")
            || name.Contains("hose")
            || name.Contains("tape")
            || name.Contains("duct tape")
            || name.Contains("xeno")
            || name.Contains("cleaner")
            || name.Contains("chlorine")
            || name.Contains("sodium bicarbonate")
            || name.Contains("alkaline")
            || name.Contains("corrugated hose");
    }

    private static bool LooksLikeMedicalItem(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("medical")
            || name.Contains("med")
            || name.Contains("first aid")
            || name.Contains("ifak")
            || name.Contains("afak")
            || name.Contains("salewa")
            || name.Contains("bandage")
            || name.Contains("splint")
            || name.Contains("cms")
            || name.Contains("surv12")
            || name.Contains("calok")
            || name.Contains("hemostat")
            || name.Contains("painkiller")
            || name.Contains("analgin")
            || name.Contains("injector")
            || name.Contains("stim")
            || name.Contains("syringe")
            || name.Contains("defibrillator");
    }

    private static bool LooksLikeFoodDrink(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("food")
            || name.Contains("drink")
            || name.Contains("ration")
            || name.Contains("iskra")
            || name.Contains("tushonka")
            || name.Contains("beef stew")
            || name.Contains("condensed milk")
            || name.Contains("herring")
            || name.Contains("saury")
            || name.Contains("sprats")
            || name.Contains("squash")
            || name.Contains("sugar")
            || name.Contains("water")
            || name.Contains("mineral")
            || name.Contains("milk")
            || name.Contains("cocoa")
            || name.Contains("cola")
            || name.Contains("tarcola")
            || name.Contains("ratcola")
            || name.Contains("energy")
            || name.Contains("hot rod")
            || name.Contains("max energy")
            || name.Contains("vodka")
            || name.Contains("whiskey")
            || name.Contains("moonshine")
            || name.Contains("beer")
            || name.Contains("pevko");
    }

    private static bool LooksLikeWeaponPartItem(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("weapon part")
            || name.Contains("attachment")
            || name.Contains("muzzle")
            || name.Contains("suppressor")
            || name.Contains("silencer")
            || name.Contains("stock")
            || name.Contains("handguard")
            || name.Contains("foregrip")
            || name.Contains("pistol grip")
            || name.Contains("magazine")
            || name.Contains("receiver")
            || name.Contains("dust cover")
            || name.Contains("mount")
            || name.Contains("rail")
            || name.Contains("scope")
            || name.Contains("optic");
    }

    private static bool LooksLikeLooseAmmoItem(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.Trim().ToLowerInvariant();

        // Caliber-start ammo names used by EFT/Tony: .366 TKM, 12/70, 5.45x39mm,
        // 7.62x54mm, 9x19mm, 12.7x55mm, etc. This intentionally does not match
        // names like "ammo case" or "gunpowder" because those do not start with a caliber.
        return System.Text.RegularExpressions.Regex.IsMatch(
            name,
            @"^(?:\.?\d+(?:\.\d+)?x\d+(?:\.\d+)?(?:mm)?|\d{1,2}/\d{2}|\.\d+(?:\.\d+)?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeAmmoSupplyItem(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var name = itemName.ToLowerInvariant();
        return name.Contains("ammo")
            || name.Contains("ammunition")
            || name.Contains("gunpowder")
            || name.Contains("powder")
            || name.Contains("eagle")
            || name.Contains("hawk")
            || name.Contains("kite")
            || name.Contains("matches")
            || name.Contains("classic matches")
            || name.Contains("malboro")
            || name.Contains("cigarette")
            || name.Contains("cigarettes");
    }

    public bool IsWeaponTemplate(string? tpl)
    {
        return !string.IsNullOrWhiteSpace(tpl)
               && IsTplInTemplateTree(tpl, WeaponBaseClassIds);
    }

    public bool IsArmorTemplate(string? tpl)
    {
        return !string.IsNullOrWhiteSpace(tpl)
               && IsTplInTemplateTree(tpl, ArmorBaseClassIds);
    }

    private bool IsTplInTemplateTree(string tpl, IReadOnlySet<string> baseClassIds)
    {
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return false;
        }

        var tables = databaseServer.GetTables();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentTpl = tpl;

        while (!string.IsNullOrWhiteSpace(currentTpl) && visited.Add(currentTpl))
        {
            if (baseClassIds.Contains(currentTpl))
            {
                return true;
            }

            var template = ResolveItemTemplate(tables, currentTpl);
            if (template == null)
            {
                return false;
            }

            var parent = GetMemberValue(template, "Parent")?.ToString()
                         ?? GetMemberValue(template, "_parent")?.ToString()
                         ?? GetMemberValue(template, "ParentId")?.ToString()
                         ?? GetMemberValue(template, "_parentId")?.ToString();

            if (string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            currentTpl = parent;
        }

        return false;
    }

    private BarterCandidate? PickAutoBarterCandidate(
        IReadOnlyList<BarterCandidate> candidates,
        HashSet<string> usedTpls,
        IReadOnlyDictionary<string, int> barterItemUsage,
        double idealValue,
        double remainingValue,
        int maxItemCount,
        string seed,
        int slot,
        bool randomizeSelection,
        bool balanceItemUsage,
        SettingsConfig settings,
        IReadOnlyDictionary<string, int> barterCategoryUsage,
        bool allowOverTarget)
    {
        var scoredCandidates = candidates
            .Where(x => !usedTpls.Contains(x.TplId))
            .Select(x =>
            {
                var count = CalculateAutoBarterCount(x, idealValue, remainingValue, maxItemCount, allowOverTarget);
                if (count <= 0 || !IsBarterCandidateWithinUsageCaps(x, barterItemUsage, barterCategoryUsage, settings, count))
                {
                    return null;
                }

                var value = x.Price * count;
                var score = Math.Abs(value - idealValue) / Math.Max(1, idealValue);

                if (allowOverTarget && value > remainingValue * 1.5)
                {
                    score += 2.0;
                }

                var itemUsage = 0;
                var categoryUsage = 0;
                if (balanceItemUsage)
                {
                    barterItemUsage.TryGetValue(x.TplId, out itemUsage);
                    barterCategoryUsage.TryGetValue(NormalizeBarterPoolCategoryName(x.Category), out categoryUsage);
                }

                var effectiveWeight = CalculateEffectiveBarterCandidateWeight(x, settings, itemUsage, categoryUsage, balanceItemUsage);
                if (effectiveWeight <= 0)
                {
                    return null;
                }

                // Weight decides preference inside a reasonable value-fit pool; score keeps the recipe near target price.
                var selectionWeight = effectiveWeight / (1 + score * 3.0);

                return new ScoredBarterCandidate(x, score, itemUsage + categoryUsage, selectionWeight);
            })
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => x.Score)
            .ThenBy(x => balanceItemUsage ? x.Usage : 0)
            .ToList();

        if (scoredCandidates.Count == 0)
        {
            return null;
        }

        if (!settings.GeneratedBarterUseWeightedCategories)
        {
            return scoredCandidates
                .Select(x => new
                {
                    x.Candidate,
                    x.Usage,
                    Score = x.Score + DeterministicJitter(seed, x.Candidate.TplId, slot) * 0.15
                })
                .OrderBy(x => balanceItemUsage ? x.Usage : 0)
                .ThenBy(x => x.Score)
                .FirstOrDefault()
                ?.Candidate;
        }

        // Do not let a very expensive/awkward item win just because it has a high weight.
        // Pick weighted inside the best-fitting candidates only.
        var weightedPool = scoredCandidates
            .Take(Math.Min(24, scoredCandidates.Count))
            .ToList();

        return PickWeightedScoredCandidate(weightedPool, seed, slot, randomizeSelection)?.Candidate;
    }

    private static ScoredBarterCandidate? PickWeightedScoredCandidate(
        IReadOnlyList<ScoredBarterCandidate> candidates,
        string seed,
        int slot,
        bool randomizeSelection)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var totalWeight = candidates.Sum(x => Math.Max(0, x.SelectionWeight));
        if (totalWeight <= 0)
        {
            return candidates.OrderBy(x => x.Score).FirstOrDefault();
        }

        double roll;
        if (randomizeSelection)
        {
            lock (AutoBarterRandom)
            {
                roll = AutoBarterRandom.NextDouble() * totalWeight;
            }
        }
        else
        {
            roll = DeterministicUnit(seed, $"weighted-barter:{slot}") * totalWeight;
        }

        foreach (var candidate in candidates)
        {
            roll -= Math.Max(0, candidate.SelectionWeight);
            if (roll <= 0)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private double CalculateEffectiveBarterCandidateWeight(
        BarterCandidate candidate,
        SettingsConfig settings,
        int itemUsage,
        int categoryUsage,
        bool balanceItemUsage)
    {
        var baseWeight = candidate.Weight > 0
            ? candidate.Weight
            : Math.Max(0.01, settings.GeneratedBarterDefaultItemWeight);

        if (!balanceItemUsage)
        {
            return baseWeight;
        }

        var category = GetGeneratedBarterCategoryConfig(candidate.Category, settings);
        var penalty = category?.OverusePenalty ?? settings.GeneratedBarterDefaultOverusePenalty;
        penalty = Math.Clamp(penalty, 0, 10);

        return baseWeight / (1 + (itemUsage + categoryUsage * 0.5) * penalty);
    }

    private bool IsBarterCandidateWithinUsageCaps(
        BarterCandidate candidate,
        IReadOnlyDictionary<string, int> barterItemUsage,
        IReadOnlyDictionary<string, int> barterCategoryUsage,
        SettingsConfig settings,
        double countToAdd)
    {
        var add = Math.Max(1, (int)Math.Ceiling(countToAdd));
        var itemMaxUses = candidate.MaxUsesPerRestock > 0
            ? candidate.MaxUsesPerRestock
            : settings.GeneratedBarterDefaultItemMaxUsesPerRestock;

        if (itemMaxUses > 0)
        {
            barterItemUsage.TryGetValue(candidate.TplId, out var currentItemUsage);
            if (currentItemUsage + add > itemMaxUses)
            {
                return false;
            }
        }

        var categoryName = NormalizeBarterPoolCategoryName(candidate.Category);
        var category = GetGeneratedBarterCategoryConfig(categoryName, settings);
        if (category != null && !category.Enabled)
        {
            return false;
        }

        var categoryMaxUses = category?.MaxUsesPerRestock ?? 0;
        if (categoryMaxUses > 0)
        {
            barterCategoryUsage.TryGetValue(categoryName, out var currentCategoryUsage);
            if (currentCategoryUsage + add > categoryMaxUses)
            {
                return false;
            }
        }

        return true;
    }

    private List<BarterCandidate> GetAutoBarterCandidates(SettingsConfig settings)
    {
        if (_autoBarterCandidates != null)
        {
            return _autoBarterCandidates;
        }

        var whitelistConfig = LoadOrCreateGeneratedBarterWhitelistConfig(settings);
        var whitelistItemsByTpl = whitelistConfig.Items
            .Where(x => x.Enabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.TplId))
            .Where(x => !YATMConfig.IsCurrencyTemplate(x.TplId))
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var tables = databaseServer.GetTables();
        var pricesByTpl = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        AddDictionaryPrices(GetMemberValue(GetMemberValue(tables, "Templates"), "Prices"), pricesByTpl);
        AddHandbookPrices(GetMemberValue(GetMemberValue(tables, "Templates"), "Handbook"), pricesByTpl);
        AddHandbookPrices(GetMemberValue(tables, "Handbook"), pricesByTpl);
        AddExternalFleaPrices(settings, pricesByTpl);

        var query = pricesByTpl
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Where(x => !YATMConfig.IsCurrencyTemplate(x.Key))
            .Where(x => x.Value > 0);

        if (settings.GeneratedBarterUseWhitelist)
        {
            if (whitelistItemsByTpl.Count == 0)
            {
                YATMLogger.Log("[GeneratedBarters] Whitelist is enabled but no enabled tpl IDs were found. Auto-barter candidates are empty.");
                _autoBarterCandidates = [];
                return _autoBarterCandidates;
            }

            query = query.Where(x => whitelistItemsByTpl.ContainsKey(x.Key));
        }

        _autoBarterCandidates = query
            .Select(x =>
            {
                whitelistItemsByTpl.TryGetValue(x.Key, out var whitelistItem);
                var itemName = !string.IsNullOrWhiteSpace(whitelistItem?.ItemName)
                    ? whitelistItem!.ItemName
                    : ResolveItemName(x.Key);

                var category = ResolveBarterCandidateCategory(whitelistItem, itemName);
                var categoryConfig = GetGeneratedBarterCategoryConfig(category, settings);
                if (categoryConfig != null && !categoryConfig.Enabled)
                {
                    return null;
                }

                var weight = whitelistItem?.Weight > 0
                    ? whitelistItem.Weight
                    : settings.GeneratedBarterDefaultItemWeight;

                var maxUses = whitelistItem?.MaxUsesPerRestock > 0
                    ? whitelistItem.MaxUsesPerRestock
                    : settings.GeneratedBarterDefaultItemMaxUsesPerRestock;

                var minQty = whitelistItem?.MinQty > 0
                    ? whitelistItem.MinQty
                    : 1;

                var maxQty = whitelistItem?.MaxQty > 0
                    ? whitelistItem.MaxQty
                    : 0;

                return new BarterCandidate(
                    x.Key,
                    itemName,
                    ResolveGeneratedBarterComponentPrice(x.Key, x.Value, settings),
                    category,
                    Math.Max(0.01, weight),
                    Math.Max(0, maxUses),
                    Math.Max(1, minQty),
                    Math.Max(0, maxQty));
            })
            .Where(x => x != null)
            .Select(x => x!)
            .Where(x => x.Price > 0)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.TplId)
            .ToList();

        if (settings.GeneratedBarterUseWhitelist)
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Built weighted whitelist candidate pool with {_autoBarterCandidates.Count} priced barter item(s).");
        }
        else
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Built weighted candidate pool with {_autoBarterCandidates.Count} priced item(s). Whitelist disabled.");
        }

        return _autoBarterCandidates;
    }

    private double ResolveGeneratedBarterComponentPrice(string tpl, double fleaPrice, SettingsConfig settings)
    {
        fleaPrice = Math.Max(0, fleaPrice);
        var traderPrice = ResolveHighestTraderSellPrice(tpl);

        var source = NormalizeGeneratedBarterComponentPriceSource(settings.GeneratedBarterComponentPriceSource);
        var result = source switch
        {
            "Trader" => traderPrice > 0 ? traderPrice : fleaPrice,
            "Flea" => fleaPrice > 0 ? fleaPrice : traderPrice,
            _ => fleaPrice > 0 && traderPrice > 0
                ? (fleaPrice + traderPrice) / 2.0
                : Math.Max(fleaPrice, traderPrice)
        };

        return Math.Max(1, Math.Round(result));
    }

    private double ResolveHighestTraderSellPrice(string tpl)
    {
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return 0;
        }

        try
        {
            var traderPrice = traderHelper.GetHighestSellToTraderPrice(tpl);
            return traderPrice > 0 ? traderPrice : 0;
        }
        catch (Exception ex)
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Failed to resolve trader sell price for {tpl}: {ex.Message}");
            return 0;
        }
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

    private HashSet<string> LoadOrCreateGeneratedBarterWhitelist(SettingsConfig settings)
    {
        if (!settings.GeneratedBarterUseWhitelist)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return LoadOrCreateGeneratedBarterWhitelistConfig(settings)
            .Items
            .Where(x => x.Enabled)
            .Select(x => x.TplId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !YATMConfig.IsCurrencyTemplate(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private GeneratedBarterWhitelistConfig LoadOrCreateGeneratedBarterWhitelistConfig(SettingsConfig settings)
    {
        if (_generatedBarterWhitelistConfig != null)
        {
            return _generatedBarterWhitelistConfig;
        }

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var relativePath = string.IsNullOrWhiteSpace(settings.GeneratedBarterWhitelistPath)
            ? "config/generated_barter_whitelist.jsonc"
            : settings.GeneratedBarterWhitelistPath;
        var whitelistPath = Path.Combine(pathToMod, relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(whitelistPath))
        {
            try
            {
                var json = File.ReadAllText(whitelistPath);
                var loaded = JsonSerializer.Deserialize<GeneratedBarterWhitelistConfig>(json, JsonOptions)
                             ?? CreateBuiltInGeneratedBarterWhitelist();

                NormalizeGeneratedBarterWhitelistConfig(loaded, settings);
                _generatedBarterWhitelistConfig = loaded;
                YATMLogger.LogDebug($"[GeneratedBarters] Loaded barter whitelist config: {relativePath} ({loaded.Items.Count} item row(s)).");
                return _generatedBarterWhitelistConfig;
            }
            catch (Exception ex)
            {
                YATMLogger.Log($"[GeneratedBarters] Failed to load barter whitelist config {relativePath}: {ex.Message}. Falling back to built-in defaults for this run.");
            }
        }

        var config = CreateBuiltInGeneratedBarterWhitelist();
        NormalizeGeneratedBarterWhitelistConfig(config, settings);

        try
        {
            var directory = Path.GetDirectoryName(whitelistPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(whitelistPath, JsonSerializer.Serialize(config, JsonOptions));
            YATMLogger.Log($"[GeneratedBarters] Created user-editable barter whitelist config: {relativePath}");
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedBarters] Failed to create barter whitelist config {relativePath}: {ex.Message}");
        }

        _generatedBarterWhitelistConfig = config;
        return _generatedBarterWhitelistConfig;
    }

    private void SeedGeneratedBarterWhitelistFromTemplate(string whitelistPath, string relativePath, string reason)
    {
        if (TryLoadDefaultGeneratedBarterWhitelist(out var defaults) && defaults.Items.Count > 0)
        {
            File.WriteAllText(whitelistPath, JsonSerializer.Serialize(defaults, JsonOptions));
            YATMLogger.Log($"[GeneratedBarters] Created barter whitelist from data template ({reason}): {relativePath}");
            return;
        }

        var empty = CreateEmptyGeneratedBarterWhitelist();
        File.WriteAllText(whitelistPath, JsonSerializer.Serialize(empty, JsonOptions));
        YATMLogger.Log($"[GeneratedBarters] Created empty barter whitelist skeleton: {relativePath}. No default whitelist template was found at {DefaultGeneratedBarterWhitelistRelativePath}.");
    }

    private bool TryLoadDefaultGeneratedBarterWhitelist(out GeneratedBarterWhitelistConfig config)
    {
        config = CreateEmptyGeneratedBarterWhitelist();

        try
        {
            var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            var defaultPath = Path.Combine(pathToMod, DefaultGeneratedBarterWhitelistRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(defaultPath))
            {
                return false;
            }

            var json = File.ReadAllText(defaultPath);
            config = JsonSerializer.Deserialize<GeneratedBarterWhitelistConfig>(json, JsonOptions)
                     ?? CreateEmptyGeneratedBarterWhitelist();

            return config.Items.Count > 0;
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedBarters] Failed to load default whitelist template: {ex.Message}");
            config = CreateEmptyGeneratedBarterWhitelist();
            return false;
        }
    }

    private static GeneratedBarterWhitelistConfig CreateEmptyGeneratedBarterWhitelist()
    {
        return new GeneratedBarterWhitelistConfig
        {
            SchemaVersion = 2,
            Description = "User-editable Tony generated barter ingredient pool. Add/remove/tune items here; manual offer config belongs in config/manual_offers.jsonc.",
            Categories = CreateDefaultGeneratedBarterCategories(),
            OfferCategoryProfiles = CreateDefaultGeneratedBarterOfferCategoryProfiles(),
            Items = []
        };
    }

    private static GeneratedBarterWhitelistConfig CreateBuiltInGeneratedBarterWhitelist()
    {
        return new GeneratedBarterWhitelistConfig
        {
            SchemaVersion = 2,
            Description = "User-editable Tony generated barter ingredient pool. Add/remove/tune items here; manual offers belong in config/manual_offers.jsonc.",
            Categories = CreateDefaultGeneratedBarterCategories(),
            OfferCategoryProfiles = CreateDefaultGeneratedBarterOfferCategoryProfiles(),
            Items =
            [
            new()
            {
                Enabled = true,
                TplId = "59faff1d86f7746c51718c9c",
                ItemName = "Physical Bitcoin",
                Category = "Valuables",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5e2aedd986f7746d404f3aa4",
                ItemName = "GreenBat lithium battery",
                Category = "Electronics",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "59faf7ca86f7740dbe19f6c2",
                ItemName = "Roler Submariner gold wrist watch",
                Category = "Valuables",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5bc9bc53d4351e00367fbcee",
                ItemName = "Golden rooster figurine",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5734758f24597738025ee253",
                ItemName = "Golden neck chain",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "573474f924597738002c6174",
                ItemName = "Chainlet",
                Category = "Valuables",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "573478bc24597738002c6175",
                ItemName = "Horse figurine",
                Category = "Valuables",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "59e3658a86f7741776641ac4",
                ItemName = "Cat figurine",
                Category = "Valuables",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "59e3639286f7741777737013",
                ItemName = "Bronze lion figurine",
                Category = "Valuables",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5c1267ee86f77416ec610f72",
                ItemName = "Chain with Prokill medallion",
                Category = "Valuables",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5d235a5986f77443f6329bc6",
                ItemName = "Gold skull ring",
                Category = "Valuables",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5e54f62086f774219b0f1937",
                ItemName = "Raven figurine",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590de7e986f7741b096e5f32",
                ItemName = "Antique vase",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590de71386f774347051a052",
                ItemName = "Antique teapot",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5bc9c1e2d4351e00367fbcf0",
                ItemName = "Antique axe",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5bc9bdb8d4351e003562b8a1",
                ItemName = "Silver Badge",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5f745ee30acaeb0d490d8c5b",
                ItemName = "Veritas guitar pick",
                Category = "Valuables",
                Weight = 9,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5bc9c049d4351e44f824d360",
                ItemName = "Battered antique book",
                Category = "Valuables",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "62a09e73af34e73a266d932a",
                ItemName = "BakeEzy cook book",
                Category = "Valuables",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "62a09cfe4f842e1bd12da3e4",
                ItemName = "Golden egg",
                Category = "Valuables",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590c645c86f77412b01304d9",
                ItemName = "Diary",
                Category = "Valuables",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Info valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590c651286f7741e566b6461",
                ItemName = "Slim diary",
                Category = "Valuables",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Info valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5c12613b86f7743bbe2c3f76",
                ItemName = "Intelligence folder",
                Category = "Valuables",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Info valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "57347ca924597744596b4e71",
                ItemName = "Graphics card",
                Category = "Electronics",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value tech"
            },
            new()
            {
                Enabled = true,
                TplId = "5c05308086f7746b2101e90b",
                ItemName = "Virtex programmable processor",
                Category = "Electronics",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value tech"
            },
            new()
            {
                Enabled = true,
                TplId = "5c05300686f7746dce784e5d",
                ItemName = "VPX Flash Storage Module",
                Category = "Electronics",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value tech"
            },
            new()
            {
                Enabled = true,
                TplId = "5c052f6886f7746b1e3db148",
                ItemName = "Military COFDM Wireless Signal Transmitter",
                Category = "Electronics",
                Weight = 3,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value tech"
            },
            new()
            {
                Enabled = true,
                TplId = "5d0378d486f77420421a5ff4",
                ItemName = "Military power filter",
                Category = "Electronics",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5d0375ff86f774186372f685",
                ItemName = "Military cable",
                Category = "Electronics",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "61bf7c024770ee6f9c6b8b53",
                ItemName = "Secure magnetic tape cassette",
                Category = "Electronics",
                Weight = 8,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590c37d286f77443be3d7827",
                ItemName = "SAS drive",
                Category = "Electronics",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590c392f86f77444754deb29",
                ItemName = "SSD drive",
                Category = "Electronics",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "590c621186f774138d11ea29",
                ItemName = "Secure Flash drive",
                Category = "Electronics",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "62a0a16d0b9d3c46de5b6e97",
                ItemName = "Military flash drive",
                Category = "Electronics",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 3,
                Notes = "Tech valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5c052e6986f7746b207bc3c9",
                ItemName = "Portable defibrillator",
                Category = "Medical",
                Weight = 4,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Medical valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5d1b327086f7742525194449",
                ItemName = "Pressure gauge",
                Category = "Tools",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "60391afc25aff57af81f7085",
                ItemName = "Ratchet wrench",
                Category = "Tools",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "619cbfccbedcde2f5b3f7bdd",
                ItemName = "Pipe grip wrench",
                Category = "Tools",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5d1b32c186f774252167a530",
                ItemName = "Analog thermometer",
                Category = "Tools",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5d1b376e86f774252519444e",
                ItemName = "Bottle of Fierce Hatchling moonshine",
                Category = "FoodDrink",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "High-value alcohol"
            },
            new()
            {
                Enabled = true,
                TplId = "5d403f9186f7743cac3f229b",
                ItemName = "Bottle of Dan Jackiel whiskey",
                Category = "FoodDrink",
                Weight = 5,
                MaxUsesPerRestock = 1,
                MinQty = 1,
                MaxQty = 1,
                Notes = "Valuable alcohol"
            },
            new()
            {
                Enabled = true,
                TplId = "5d40407c86f774318526545a",
                ItemName = "Bottle of Tarkovskaya vodka",
                Category = "FoodDrink",
                Weight = 10,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Valuable alcohol"
            },
            new()
            {
                Enabled = true,
                TplId = "62a09f32621468534a797acb",
                ItemName = "Bottle of Pevko Light beer",
                Category = "FoodDrink",
                Weight = 10,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 2,
                Notes = "Valuable alcohol"
            },
            new()
            {
                Enabled = true,
                TplId = "57347da92459774491567cf5",
                ItemName = "Can of beef stew (Large)",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable food"
            },
            new()
            {
                Enabled = true,
                TplId = "5734773724597737fd047c14",
                ItemName = "Can of condensed milk",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable food"
            },
            new()
            {
                Enabled = true,
                TplId = "57347d9c245977448b40fa85",
                ItemName = "Can of herring",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable food"
            },
            new()
            {
                Enabled = true,
                TplId = "57347d8724597744596b4e76",
                ItemName = "Can of squash spread",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable food"
            },
            new()
            {
                Enabled = true,
                TplId = "59e3577886f774176a362503",
                ItemName = "Pack of sugar",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable food"
            },
            new()
            {
                Enabled = true,
                TplId = "5751435d24597720a27126d1",
                ItemName = "Can of Max Energy energy drink",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "60b0f93284c20f0feb453da7",
                ItemName = "Can of RatCola soda",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "57514643245977207f2c2d09",
                ItemName = "Can of TarCola soda",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "5751496424597720a27126da",
                ItemName = "Can of Hot Rod energy drink",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "62a091170b9d3c46de5b6cf2",
                ItemName = "Axel parrot figure",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "655c652d60d0ac437100fed7",
                ItemName = "BEAR operative figure",
                Category = "FoodDrink",
                Weight = 22,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Valuable drink"
            },
            new()
            {
                Enabled = true,
                TplId = "590c2d8786f774245b1f03f3",
                ItemName = "Screwdriver",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "62a0a098de7ac8199358053b",
                ItemName = "Awl",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5e2af02c86f7746d420957d4",
                ItemName = "Pack of chlorine",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "59e35abd86f7741778269d82",
                ItemName = "Pack of sodium bicarbonate",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "59faf98186f774067b6be103",
                ItemName = "Alkaline cleaner for heat exchangers",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "60b0f561c4449e4cb624c1d7",
                ItemName = "LVNDMARK's rat poison",
                Category = "Tools",
                Weight = 24,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 5,
                Notes = "Tool valuable"
            },
            new()
            {
                Enabled = true,
                TplId = "5755356824597772cb798962",
                ItemName = "AI-2 medkit",
                Category = "Medical",
                Weight = 35,
                MaxUsesPerRestock = 6,
                MinQty = 1,
                MaxQty = 4,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "544fb25a4bdc2dfb738b4567",
                ItemName = "Aseptic bandage",
                Category = "Medical",
                Weight = 35,
                MaxUsesPerRestock = 6,
                MinQty = 1,
                MaxQty = 5,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "544fb37f4bdc2dee738b4567",
                ItemName = "Analgin painkillers",
                Category = "Medical",
                Weight = 25,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 4,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "544fb3364bdc2d34748b456a",
                ItemName = "Immobilizing splint",
                Category = "Medical",
                Weight = 25,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 4,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "62a0a043cf4a99369e2624a5",
                ItemName = "Bottle of OLOLO Multivitamins",
                Category = "Medical",
                Weight = 18,
                MaxUsesPerRestock = 4,
                MinQty = 1,
                MaxQty = 3,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "590c661e86f7741e566b646a",
                ItemName = "Car first aid kit",
                Category = "Medical",
                Weight = 12,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "57347b8b24597737dd42e192",
                ItemName = "Classic matches",
                Category = "AmmoSupplies",
                Weight = 35,
                MaxUsesPerRestock = 8,
                MinQty = 1,
                MaxQty = 8,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "56742c324bdc2d150f8b456d",
                ItemName = "Crickent lighter",
                Category = "AmmoSupplies",
                Weight = 28,
                MaxUsesPerRestock = 6,
                MinQty = 1,
                MaxQty = 5,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "56742c2e4bdc2d95058b456d",
                ItemName = "Zibbo lighter",
                Category = "AmmoSupplies",
                Weight = 20,
                MaxUsesPerRestock = 5,
                MinQty = 1,
                MaxQty = 4,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "590c5a7286f7747884343aea",
                ItemName = "Gunpowder Kite",
                Category = "AmmoSupplies",
                Weight = 18,
                MaxUsesPerRestock = 4,
                MinQty = 1,
                MaxQty = 3,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "5d6fc78386f77449d825f9dc",
                ItemName = "Gunpowder Eagle",
                Category = "AmmoSupplies",
                Weight = 14,
                MaxUsesPerRestock = 3,
                MinQty = 1,
                MaxQty = 2,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "5d6fc87386f77449db3db94e",
                ItemName = "Gunpowder Hawk",
                Category = "AmmoSupplies",
                Weight = 10,
                MaxUsesPerRestock = 2,
                MinQty = 1,
                MaxQty = 2,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "57347c1124597737fb1379e3",
                ItemName = "Bolts",
                Category = "Junk",
                Weight = 35,
                MaxUsesPerRestock = 8,
                MinQty = 1,
                MaxQty = 8,
                Notes = ""
            },
            new()
            {
                Enabled = true,
                TplId = "57347c5b245977448d35f6e1",
                ItemName = "Screw nuts",
                Category = "Junk",
                Weight = 35,
                MaxUsesPerRestock = 8,
                MinQty = 1,
                MaxQty = 8,
                Notes = ""
            }
            ]
        };
    }

    private static void NormalizeGeneratedBarterWhitelistConfig(GeneratedBarterWhitelistConfig config, SettingsConfig settings)
    {
        config.SchemaVersion = Math.Max(2, config.SchemaVersion);
        config.Categories ??= [];
        config.OfferCategoryProfiles ??= [];
        config.Items ??= [];

        foreach (var pair in CreateDefaultGeneratedBarterCategories())
        {
            config.Categories.TryAdd(pair.Key, pair.Value);
        }

        foreach (var pair in CreateDefaultGeneratedBarterOfferCategoryProfiles())
        {
            config.OfferCategoryProfiles.TryAdd(pair.Key, pair.Value);
        }

        foreach (var item in config.Items)
        {
            item.Category = NormalizeBarterPoolCategoryName(item.Category);
            item.Weight = item.Weight > 0 ? item.Weight : settings.GeneratedBarterDefaultItemWeight;
            item.MaxUsesPerRestock = Math.Max(0, item.MaxUsesPerRestock);
            item.MinQty = Math.Max(1, item.MinQty);
            item.MaxQty = Math.Max(0, item.MaxQty);
        }
    }

    private static Dictionary<string, GeneratedBarterCategoryConfig> CreateDefaultGeneratedBarterCategories()
    {
        return new Dictionary<string, GeneratedBarterCategoryConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Electronics"] = new() { Enabled = true, MaxUsesPerRestock = 18, OverusePenalty = 0.50 },
            ["Tools"] = new() { Enabled = true, MaxUsesPerRestock = 22, OverusePenalty = 0.40 },
            ["Medical"] = new() { Enabled = true, MaxUsesPerRestock = 12, OverusePenalty = 0.45 },
            ["FoodDrink"] = new() { Enabled = true, MaxUsesPerRestock = 18, OverusePenalty = 0.35 },
            ["Valuables"] = new() { Enabled = true, MaxUsesPerRestock = 10, OverusePenalty = 0.70 },
            ["WeaponParts"] = new() { Enabled = true, MaxUsesPerRestock = 12, OverusePenalty = 0.45 },
            ["AmmoSupplies"] = new() { Enabled = true, MaxUsesPerRestock = 10, OverusePenalty = 0.45 },
            ["Junk"] = new() { Enabled = true, MaxUsesPerRestock = 18, OverusePenalty = 0.35 },
            ["Default"] = new() { Enabled = true, MaxUsesPerRestock = 0, OverusePenalty = 0.45 }
        };
    }

    private static Dictionary<string, GeneratedBarterOfferCategoryProfile> CreateDefaultGeneratedBarterOfferCategoryProfiles()
    {
        return new Dictionary<string, GeneratedBarterOfferCategoryProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["AmmoPack"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 1,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AmmoSupplies"] = 45, ["Tools"] = 25, ["Junk"] = 20, ["Valuables"] = 10
                }
            },
            ["Weapon"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 2,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WeaponParts"] = 40, ["Tools"] = 30, ["Electronics"] = 20, ["Valuables"] = 10
                }
            },
            ["Medical"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 2,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Medical"] = 70, ["FoodDrink"] = 15, ["Valuables"] = 15
                }
            },
            ["Headwear"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 2,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Electronics"] = 45, ["Tools"] = 35, ["Valuables"] = 20
                }
            },
            ["Armor"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 3,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tools"] = 45, ["Electronics"] = 20, ["Valuables"] = 20, ["Junk"] = 15
                }
            },
            ["CaseOrValuable"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 4,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Valuables"] = 65, ["Electronics"] = 25, ["Tools"] = 10
                }
            },
            ["Electronics"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 3,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Electronics"] = 70, ["Tools"] = 20, ["Valuables"] = 10
                }
            },
            ["Tools"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 3,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tools"] = 75, ["Junk"] = 15, ["Valuables"] = 10
                }
            },
            ["FoodDrink"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 2,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FoodDrink"] = 80, ["Valuables"] = 20
                }
            },
            ["Default"] = new()
            {
                Enabled = true,
                MaxDifferentItems = 3,
                IngredientCategoryWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tools"] = 30, ["Electronics"] = 25, ["FoodDrink"] = 15, ["Valuables"] = 15, ["Junk"] = 15
                }
            }
        };
    }

    private static void AddDictionaryPrices(object? dictionaryObject, Dictionary<string, double> pricesByTpl)
    {
        if (dictionaryObject is not IDictionary dictionary)
        {
            return;
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            var tpl = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(tpl) || YATMConfig.IsCurrencyTemplate(tpl))
            {
                continue;
            }

            if (TryReadDouble(entry.Value, out var price) && price > 0)
            {
                pricesByTpl[tpl] = price;
            }
        }
    }

    private static void AddHandbookPrices(object? handbookObject, Dictionary<string, double> pricesByTpl)
    {
        var items = GetMemberValue(handbookObject, "Items") ?? GetMemberValue(handbookObject, "items");
        if (items is not IEnumerable enumerable)
        {
            return;
        }

        foreach (var item in enumerable)
        {
            var tpl = GetMemberValue(item, "Id")?.ToString()
                      ?? GetMemberValue(item, "_id")?.ToString();

            if (string.IsNullOrWhiteSpace(tpl) || YATMConfig.IsCurrencyTemplate(tpl))
            {
                continue;
            }

            foreach (var priceMember in new[] { "Price", "price", "Value", "value" })
            {
                if (TryReadDouble(GetMemberValue(item, priceMember), out var price) && price > 0)
                {
                    pricesByTpl.TryAdd(tpl, price);
                    break;
                }
            }
        }
    }

    private static bool PaymentSchemesEqual(List<List<PaymentConfigItem>>? left, List<List<PaymentConfigItem>>? right)
    {
        return JsonSerializer.Serialize(left ?? [], JsonOptions)
            == JsonSerializer.Serialize(right ?? [], JsonOptions);
    }

    private static double ApplyGeneratedValueMode(double baseValue, string? mode, int offsetPercent)
    {
        var multiplier = 1.0;
        var offset = Math.Clamp(offsetPercent, 0, 100) / 100.0;

        if (string.Equals(mode, "Under", StringComparison.OrdinalIgnoreCase))
        {
            multiplier -= offset;
        }
        else if (string.Equals(mode, "Over", StringComparison.OrdinalIgnoreCase))
        {
            multiplier += offset;
        }

        multiplier = Math.Max(0.01, multiplier);
        return Math.Max(1, Math.Round(baseValue * multiplier));
    }

    private static double DeterministicJitter(string seed, string tpl, int slot)
    {
        return DeterministicUnit(seed, $"{tpl}:{slot}");
    }

    private static double DeterministicUnit(string seed, string key)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{seed}:{key}"));
        var value = BitConverter.ToUInt32(bytes, 0);
        return value / (double)uint.MaxValue;
    }

    public MarketCashPriceResult ResolveMarketCashPrice(string? tpl, SettingsConfig settings)
    {
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return new MarketCashPriceResult(
                NormalizeMarketCashPriceBlendMode(settings.MarketCashPriceBlendMode),
                0,
                0,
                0);
        }

        return ResolveMarketCashPrice(
            new[] { new MarketCashPriceComponent(tpl, 1) },
            settings);
    }

    public MarketCashPriceResult ResolveMarketCashPrice(IEnumerable<MarketCashPriceComponent>? components, SettingsConfig settings)
    {
        var normalizedComponents = components?
            .Where(x => !string.IsNullOrWhiteSpace(x.TplId) && x.Count > 0)
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .Select(x => new MarketCashPriceComponent(x.Key, x.Sum(y => y.Count)))
            .ToList()
            ?? [];

        var mode = NormalizeMarketCashPriceBlendMode(settings.MarketCashPriceBlendMode);
        if (normalizedComponents.Count == 0)
        {
            return new MarketCashPriceResult(mode, 0, 0, 0);
        }

        double fleaPrice = 0;
        double traderBestPrice = 0;

        foreach (var component in normalizedComponents)
        {
            var count = Math.Max(0, component.Count);
            if (count <= 0)
            {
                continue;
            }

            fleaPrice += Math.Max(0, ResolveDatabasePrice(component.TplId, settings)) * count;
            traderBestPrice += Math.Max(0, ResolveHighestTraderSellPrice(component.TplId)) * count;
        }

        var finalPrice = mode switch
        {
            "AllFlea" => PreferAvailable(fleaPrice, traderBestPrice),
            "HeavyFlea" => BlendMarketPrices(fleaPrice, traderBestPrice, fleaWeight: 0.75),
            "AllTBP" => PreferAvailable(traderBestPrice, fleaPrice),
            "HeavyTBP" => BlendMarketPrices(fleaPrice, traderBestPrice, fleaWeight: 0.25),
            _ => BlendMarketPrices(fleaPrice, traderBestPrice, fleaWeight: 0.50)
        };

        finalPrice = Math.Max(1, Math.Round(finalPrice, MidpointRounding.AwayFromZero));

        return new MarketCashPriceResult(
            mode,
            Math.Max(0, Math.Round(fleaPrice, MidpointRounding.AwayFromZero)),
            Math.Max(0, Math.Round(traderBestPrice, MidpointRounding.AwayFromZero)),
            finalPrice);
    }

    private static double BlendMarketPrices(double fleaPrice, double traderBestPrice, double fleaWeight)
    {
        fleaPrice = Math.Max(0, fleaPrice);
        traderBestPrice = Math.Max(0, traderBestPrice);

        if (fleaPrice <= 1 && traderBestPrice > 0)
        {
            return traderBestPrice;
        }

        if (traderBestPrice <= 0)
        {
            return fleaPrice;
        }

        if (fleaPrice <= 0)
        {
            return traderBestPrice;
        }

        fleaWeight = Math.Clamp(fleaWeight, 0, 1);
        return fleaPrice * fleaWeight + traderBestPrice * (1 - fleaWeight);
    }

    private static double PreferAvailable(double preferred, double fallback)
    {
        preferred = Math.Max(0, preferred);
        fallback = Math.Max(0, fallback);

        if (preferred <= 1 && fallback > 0)
        {
            return fallback;
        }

        return preferred > 0 ? preferred : fallback;
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

    private double ResolveGeneratedPrice(string? tpl, SettingsConfig settings)
    {
        if (!settings.AutoPriceGeneratedOffers || string.IsNullOrWhiteSpace(tpl))
        {
            return 1;
        }

        var basePrice = ResolveDatabasePrice(tpl, settings);
        if (basePrice <= 0)
        {
            basePrice = 1;
        }

        return ApplyGeneratedValueMode(
            basePrice,
            settings.GeneratedOfferPriceMode,
            settings.GeneratedOfferPriceOffsetPercent);
    }

    private double ResolveDatabasePrice(string tpl, SettingsConfig settings)
    {
        var tables = databaseServer.GetTables();

        if (settings.GeneratedPricePreferExternalFleaPrices
            && TryReadExternalFleaPrice(settings, tpl, out var externalPreferredPrice))
        {
            return externalPreferredPrice;
        }

        if (TryReadDictionaryPrice(GetMemberValue(GetMemberValue(tables, "Templates"), "Prices"), tpl, out var templatePrice))
        {
            return templatePrice;
        }

        if (TryReadHandbookPrice(GetMemberValue(GetMemberValue(tables, "Templates"), "Handbook"), tpl, out var handbookPrice))
        {
            return handbookPrice;
        }

        if (TryReadHandbookPrice(GetMemberValue(tables, "Handbook"), tpl, out handbookPrice))
        {
            return handbookPrice;
        }

        var itemTemplate = ResolveItemTemplate(tables, tpl);
        if (itemTemplate != null)
        {
            foreach (var priceMember in new[] { "CreditsPrice", "Price", "price" })
            {
                if (TryReadDouble(GetMemberValue(GetMemberValue(itemTemplate, "Props") ?? GetMemberValue(itemTemplate, "_props") ?? itemTemplate, priceMember), out var itemPrice))
                {
                    return itemPrice;
                }
            }
        }

        if (!settings.GeneratedPricePreferExternalFleaPrices
            && TryReadExternalFleaPrice(settings, tpl, out var externalFallbackPrice))
        {
            return externalFallbackPrice;
        }

        return 1;
    }

    private bool TryReadExternalFleaPrice(SettingsConfig settings, string tpl, out double price)
    {
        price = 0;

        if (!settings.GeneratedPriceUseExternalFleaPriceFiles || string.IsNullOrWhiteSpace(tpl))
        {
            return false;
        }

        var priceTable = LoadExternalFleaPrices(settings);
        return priceTable.TryGetValue(tpl, out price) && price > 0;
    }

    private void AddExternalFleaPrices(SettingsConfig settings, Dictionary<string, double> pricesByTpl)
    {
        if (!settings.GeneratedPriceUseExternalFleaPriceFiles)
        {
            return;
        }

        var externalPrices = LoadExternalFleaPrices(settings);
        if (externalPrices.Count == 0)
        {
            return;
        }

        foreach (var (tpl, price) in externalPrices)
        {
            if (string.IsNullOrWhiteSpace(tpl) || price <= 0)
            {
                continue;
            }

            if (settings.GeneratedPricePreferExternalFleaPrices)
            {
                pricesByTpl[tpl] = price;
            }
            else
            {
                pricesByTpl.TryAdd(tpl, price);
            }
        }
    }

    private Dictionary<string, double> LoadExternalFleaPrices(SettingsConfig settings)
    {
        if (_externalFleaPrices != null)
        {
            return _externalFleaPrices;
        }

        _externalFleaPrices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (!settings.GeneratedPriceUseExternalFleaPriceFiles)
        {
            return _externalFleaPrices;
        }

        var paths = ResolveExternalFleaPricePaths(settings);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var intPrices = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions);
                if (intPrices != null)
                {
                    foreach (var (tpl, value) in intPrices)
                    {
                        if (!string.IsNullOrWhiteSpace(tpl) && value > 0)
                        {
                            _externalFleaPrices[tpl] = value;
                        }
                    }
                }
                else
                {
                    var doublePrices = JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions);
                    if (doublePrices != null)
                    {
                        foreach (var (tpl, value) in doublePrices)
                        {
                            if (!string.IsNullOrWhiteSpace(tpl) && value > 0)
                            {
                                _externalFleaPrices[tpl] = value;
                            }
                        }
                    }
                }

                if (settings.GeneratedPriceLogExternalFleaPriceSource)
                {
                    YATMLogger.Log($"[GeneratedPrices] Loaded {_externalFleaPrices.Count} external flea price row(s) from {Path.GetFileName(path)}.");
                }

                if (_externalFleaPrices.Count > 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                YATMLogger.LogDebug($"[GeneratedPrices] Failed to load external flea prices from {path}: {ex.Message}");
            }
        }

        return _externalFleaPrices;
    }

    private List<string> ResolveExternalFleaPricePaths(SettingsConfig settings)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var preferredFileName = string.Equals(settings.GeneratedPriceExternalFleaGameMode, "pve", StringComparison.OrdinalIgnoreCase)
            ? "prices-pve.json"
            : "prices-regular.json";

        foreach (var configuredPath in settings.GeneratedPriceExternalFleaPriceFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            var fullPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(pathToMod, configuredPath.Replace('/', Path.DirectorySeparatorChar)));

            if (seen.Add(fullPath))
            {
                results.Add(fullPath);
            }
        }

        if (settings.GeneratedPriceScanSiblingModsForFleaPriceFiles)
        {
            var modsDir = Directory.GetParent(pathToMod)?.FullName;
            if (!string.IsNullOrWhiteSpace(modsDir) && Directory.Exists(modsDir))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(modsDir, preferredFileName, SearchOption.AllDirectories))
                    {
                        if (!path.Contains($"{Path.DirectorySeparatorChar}config{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (path.StartsWith(pathToMod, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (seen.Add(path))
                        {
                            results.Add(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    YATMLogger.LogDebug($"[GeneratedPrices] Failed to scan sibling mods for flea price files: {ex.Message}");
                }
            }
        }

        return results;
    }

    private static bool TryReadDictionaryPrice(object? dictionaryObject, string tpl, out double price)
    {
        price = 0;

        if (dictionaryObject is not IDictionary dictionary)
        {
            return false;
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key?.ToString()?.Equals(tpl, StringComparison.OrdinalIgnoreCase) == true
                && TryReadDouble(entry.Value, out price)
                && price > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadHandbookPrice(object? handbookObject, string tpl, out double price)
    {
        price = 0;

        var items = GetMemberValue(handbookObject, "Items") ?? GetMemberValue(handbookObject, "items");
        if (items is not IEnumerable enumerable)
        {
            return false;
        }

        foreach (var item in enumerable)
        {
            var id = GetMemberValue(item, "Id")?.ToString()
                     ?? GetMemberValue(item, "_id")?.ToString();

            if (!string.Equals(id, tpl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var priceMember in new[] { "Price", "price", "Value", "value" })
            {
                if (TryReadDouble(GetMemberValue(item, priceMember), out price) && price > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static object? ResolveItemTemplate(object tables, string tpl)
    {
        var items = GetMemberValue(GetMemberValue(tables, "Templates"), "Items")
                    ?? GetMemberValue(GetMemberValue(tables, "Templates"), "items");

        if (items is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key?.ToString()?.Equals(tpl, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return entry.Value;
                }
            }
        }

        return null;
    }

    private string ResolveItemName(string? tpl)
    {
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return string.Empty;
        }

        try
        {
            var locales = databaseServer.GetTables().Locales.Global["en"];
            if (locales.Value != null && locales.Value.TryGetValue($"{tpl} Name", out var nameVal))
            {
                return nameVal?.ToString() ?? tpl;
            }
        }
        catch
        {
            // fall back below
        }

        return tpl;
    }

    private static bool HasUsableBarterScheme(PriceConfigItem priceConfig)
    {
        return priceConfig.BarterScheme != null
            && HasNonCurrencyPayment(priceConfig.BarterScheme);
    }

    private static bool HasNonCurrencyPayment(List<List<PaymentConfigItem>>? barterScheme)
    {
        if (barterScheme == null)
        {
            return false;
        }

        foreach (var option in barterScheme)
        foreach (var component in option)
        {
            if (!string.IsNullOrWhiteSpace(component.TplId)
                && !YATMConfig.IsCurrencyTemplate(component.TplId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDouble(object? value, out double number)
    {
        number = 0;

        if (value == null)
        {
            return false;
        }

        if (value is double d)
        {
            number = d;
            return true;
        }

        if (value is float f)
        {
            number = f;
            return true;
        }

        if (value is decimal m)
        {
            number = (double)m;
            return true;
        }

        if (value is int i)
        {
            number = i;
            return true;
        }

        if (value is long l)
        {
            number = l;
            return true;
        }

        return double.TryParse(value.ToString(), out number);
    }

    private async Task SaveAsync(Assembly assembly, TraderAssort generatedAssort, IReadOnlyList<PriceConfigItem> priceConfigs)
    {
        var generatedDir = GetGeneratedDir(assembly);
        Directory.CreateDirectory(generatedDir);

        var assortPath = Path.Combine(generatedDir, GeneratedAssortFileName);
        var priceRulesPath = Path.Combine(generatedDir, GeneratedPricesFileName);

        // Only generated/addon assort rows belong in this cache. Tony's base
        // db/CustomTrader/Tony/assort.json stays the source of truth for base offers.
        if (generatedAssort.Items.Count > 0
            || generatedAssort.BarterScheme.Count > 0
            || generatedAssort.LoyalLevelItems.Count > 0)
        {
            await File.WriteAllTextAsync(
                assortPath,
                jsonUtil.Serialize(generatedAssort, true) ?? CreateEmptyAssortJson().ToJsonString(JsonOptions));
        }
        else if (File.Exists(assortPath))
        {
            File.Delete(assortPath);
        }

        // Only generated/addon price rules belong in this cache. Tony's OG
        // config/manual_offers.jsonc is loaded directly by YATMConfig and is not duplicated here.
        if (priceConfigs.Count > 0)
        {
            await File.WriteAllTextAsync(
                priceRulesPath,
                jsonUtil.Serialize(priceConfigs, true) ?? "[]");
        }
        else if (File.Exists(priceRulesPath))
        {
            File.Delete(priceRulesPath);
        }

        DeleteLegacyGeneratedCacheFiles(generatedDir);
    }

    private static void DeleteLegacyGeneratedCacheFiles(string generatedDir)
    {
        foreach (var legacyFileName in new[]
        {
            LegacyGeneratedAssortFileName,
            LegacyGeneratedPricesFileName
        })
        {
            var path = Path.Combine(generatedDir, legacyFileName);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    YATMLogger.LogDebug($"[GeneratedOffers] Could not delete legacy cache file {legacyFileName}: {ex.Message}");
                }
            }
        }
    }

    private List<PriceConfigItem> LoadGeneratedPriceConfigs(Assembly assembly)
    {
        var generatedPricesPath = Path.Combine(GetGeneratedDir(assembly), GeneratedPricesFileName);

        if (!File.Exists(generatedPricesPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(generatedPricesPath);
            return JsonSerializer.Deserialize<List<PriceConfigItem>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedOffers] Failed to load generated price cache: {ex.Message}");
            return [];
        }
    }

    private string GetGeneratedDir(Assembly assembly)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(assembly);
        return Path.Combine(pathToMod, GeneratedDirRelativePath);
    }

    private TraderAssort CreateEmptyTraderAssort()
    {
        return jsonUtil.Deserialize<TraderAssort>(CreateEmptyAssortJson().ToJsonString(JsonOptions))
               ?? throw new InvalidOperationException("Could not create empty TraderAssort.");
    }

    private static JsonObject CreateEmptyAssortJson()
    {
        return new JsonObject
        {
            ["items"] = new JsonArray(),
            ["barter_scheme"] = new JsonObject(),
            ["loyal_level_items"] = new JsonObject()
        };
    }

    private static JsonArray? GetArrayByAnyName(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetPropertyValue(key, out var node) && node is JsonArray array)
            {
                return array;
            }
        }

        return null;
    }

    private static JsonObject? GetObjectByAnyName(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetPropertyValue(key, out var node) && node is JsonObject obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static JsonArray GetItemsArray(JsonObject root)
    {
        var items = root["items"] as JsonArray;
        if (items != null)
        {
            return items;
        }

        items = new JsonArray();
        root["items"] = items;
        return items;
    }

    private static JsonObject GetObject(JsonObject root, string key)
    {
        var obj = root[key] as JsonObject;
        if (obj != null)
        {
            return obj;
        }

        obj = new JsonObject();
        root[key] = obj;
        return obj;
    }

    private static string? ReadObjectString(object target, string memberName)
    {
        var value = GetMemberValue(target, memberName);
        return value?.ToString();
    }

    private static bool? ReadJsonBool(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node == null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        return bool.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static int? ReadJsonInt(JsonObject obj, string key)
    {
        var number = ReadJsonDouble(obj, key);
        return number.HasValue ? (int)Math.Round(number.Value, MidpointRounding.AwayFromZero) : null;
    }

    private static double? ReadJsonDouble(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node == null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }
        }

        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static string? ReadJsonString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node == null)
        {
            return null;
        }

        return node.ToString();
    }

    private static object? GetMemberValue(object? target, string memberName)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();

        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanRead)
        {
            return prop.GetValue(target);
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field != null)
        {
            return field.GetValue(target);
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

    private static string MakeDeterministicMongoId(string seed)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }


    private static readonly HashSet<string> AmmoBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ammo / cartridges
        "5485a8684bdc2da71d8b4567"
    };

    private static readonly HashSet<string> WeaponBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "5422acb9af1c889c16000029"
    };

    private static readonly HashSet<string> MedicalBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "5448f39d4bdc2d0a728b4568"
    };

    private static readonly HashSet<string> HeadwearBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Headwear / helmets
        "5a341c4086f77401f2541505",
        // Headsets / earpieces
        "5645bcb74bdc2ded0b8b4578",
        // Face covers / masks
        "5a341c4686f77469e155819e"
    };

    private static readonly HashSet<string> ArmorBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "5448e5284bdc2dcb718b4567",
        "5448e54d4bdc2dcc718b4568",
        "5448e5724bdc2ddf718b4568",
        "57bef4c42459772e8d35a53b"
    };

    private static readonly HashSet<string> CaseBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "5795f317245977243854e041",
        "5448bf274bdc2dfc2f8b456a"
    };

    private static readonly HashSet<string> ValuableBaseClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "57864a3d24597754843f8721"
    };


    public sealed class GeneratedBarterApplyResult
    {
        public static GeneratedBarterApplyResult Empty => new();

        public bool Changed { get; set; }
        public int SelectedCount { get; set; }
        public int SuccessfulCount { get; set; }
        public int GeneratedOrUpdatedCount { get; set; }
        public int SkippedCount { get; set; }
        public HashSet<string> AttemptedOfferIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SuccessfulOfferIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SkippedOfferIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record BarterCandidate(
        string TplId,
        string ItemName,
        double Price,
        string Category,
        double Weight,
        int MaxUsesPerRestock,
        int MinQty,
        int MaxQty);

    private sealed class GeneratedBarterSkipDetail
    {
        public string OfferId { get; set; } = string.Empty;
        public string TplId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public double Price { get; set; }
        public double TargetPrice { get; set; }
        public string TargetValueBasis { get; set; } = "Unit";
        public string Currency { get; set; } = "RUB";
        public string Reason { get; set; } = string.Empty;
    }

    private sealed record ScoredBarterCandidate(
        BarterCandidate Candidate,
        double Score,
        int Usage,
        double SelectionWeight);

    private sealed record GeneratedOfferBuildResult(
        TraderAssort GeneratedAssort,
        List<PriceConfigItem> PriceConfigs);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
