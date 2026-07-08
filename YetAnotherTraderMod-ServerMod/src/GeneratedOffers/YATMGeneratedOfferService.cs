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

/// <summary>
/// Runtime-facing service for generated/addon Tony offers.
///
/// Responsibilities:
/// - Build generated assort rows from current YATM raw offer files.
/// - Derive price configs from each raw root offer and its yatm_settings metadata.
/// - Save/load the generated cache for restocks.
/// - Merge generated offers and price rules into Tony's live pipeline.
///
/// When ManualBarters is false, this service generates barter recipes from the loaded
/// EFT/SPT price tables and saves them into db/Generated/generated_barters.jsonc through YATMConfig.
/// When ManualBarters is true, only hard-written BarterScheme rows are respected.
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

    private readonly List<PriceConfigItem> _generatedPriceConfigs = [];
    private static readonly Random AutoBarterRandom = new();
    private List<BarterCandidate>? _autoBarterCandidates;
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
        if (config.Settings.CashOffersOnly)
        {
            YATMLogger.LogDebug("[GeneratedBarters] CashOffersOnly enabled; generated barters skipped.");
            return false;
        }

        if (config.Settings.ManualBarters)
        {
            YATMLogger.LogDebug("[GeneratedBarters] ManualBarters enabled; only hard-written BarterScheme rows will be used.");
            return false;
        }

        // Rebuild the auto-barter candidate pool each run so whitelist and external price changes
        // are picked up on startup/restock without stale cached candidates.
        _autoBarterCandidates = null;
        _externalFleaPrices = null;

        var selectedConfigs = selectedPriceConfigs
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TplId))
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.OfferId) ? x.OfferId! : x.TplId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (selectedConfigs.Count == 0)
        {
            return false;
        }

        var changed = false;
        var generatedCount = 0;
        var skippedCount = 0;
        var barterItemUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            if (YATMConfig.IsCurrencyTemplate(priceConfig.TplId))
            {
                skippedCount++;
                continue;
            }

            // Static ammo-pack offers are helper offers for the paired ammo system.
            // They should never get their own generated barter row. The loose ammo row
            // carries the generated barter, and the roll service moves that barter onto
            // the pack offer when barter wins.
            if (IsPairedAmmoPackHelperConfig(priceConfig, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
            {
                skippedCount++;
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

            var generatedScheme = GenerateAutoBarterScheme(priceConfig, config.Settings, currentAssort, barterItemUsage);
            if (generatedScheme.Count == 0)
            {
                skippedCount++;
                continue;
            }

            if (!priceConfig.AutoGeneratedBarter)
            {
                priceConfig.AutoGeneratedBarter = true;
                changed = true;
            }

            if (IsPairedAmmoLooseConfig(priceConfig)
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
        }

        if (generatedCount > 0 || skippedCount > 0)
        {
            YATMLogger.Log($"[GeneratedBarters] Selected barter offers: {selectedConfigs.Count}. Generated/updated {generatedCount} barter scheme(s); skipped {skippedCount}.");
        }

        return changed;
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
            var hasHardWrittenBarter = HasNonCurrencyPayment(copiedBarterScheme);
            var yatmSettings = GetYatmSettingsForOffer(rawOfferFile.YatmSettings, offerId);

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

            if (settings.ManualBarters && !HasUsableBarterScheme(priceConfig))
            {
                priceConfig.CashOnly = true;
                priceConfig.AlwaysBarter = false;
            }

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

        // PackOfferId is accepted in old addon files but intentionally ignored.
        // Same-offer ammo barter uses AmmoBarterPackTplId only.
        priceConfig.PackOfferId = null;
        priceConfig.AmmoBarterPackTplId = ReadJsonString(yatmSettings, "AmmoBarterPackTplId") ?? priceConfig.AmmoBarterPackTplId;
        priceConfig.AmmoBarterPackItemName = ReadJsonString(yatmSettings, "AmmoBarterPackItemName") ?? priceConfig.AmmoBarterPackItemName;
        priceConfig.BarterSchemeValueBasis = ReadJsonString(yatmSettings, "BarterSchemeValueBasis") ?? priceConfig.BarterSchemeValueBasis;
        priceConfig.GeneratedBarterCategoryOverride = ReadJsonString(yatmSettings, "GeneratedBarterCategoryOverride") ?? priceConfig.GeneratedBarterCategoryOverride;

        var packSize = ReadJsonInt(yatmSettings, "AmmoBarterPackSize");
        if (packSize.HasValue && packSize.Value > 0)
        {
            priceConfig.AmmoBarterPackSize = packSize.Value;
        }
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

        if (settings.ManualBarters && !hasHardWrittenBarter)
        {
            priceConfig.CashOnly = true;
            priceConfig.AlwaysBarter = false;
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

        if (!IsPairedAmmoLooseConfig(priceConfig))
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

    private static int ResolveAmmoPackSizeForGeneratedBarter(PriceConfigItem priceConfig, TraderAssort? currentAssort)
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

        return GetKnownAmmoPackSizeForGeneratedBarter(priceConfig.AmmoBarterPackTplId ?? string.Empty);
    }

    private static int GetKnownAmmoPackSizeForGeneratedBarter(string packTpl)
    {
        if (string.IsNullOrWhiteSpace(packTpl))
        {
            return 1;
        }

        if (IsTplMatch(packTpl,
            "648983d6b5a2df1c815a04ec"))
        {
            return 10;
        }

        if (IsTplMatch(packTpl,
            "657023f81419851aef03e6f1",
            "6489851fc827d4637f01791b",
            "64acea16c4eda9354b0226b0",
            "64ace9f9c4eda9354b0226aa",
            "5c1127bdd174af44217ab8b9",
            "6489854673c462723909a14e",
            "657025dabfc87b3a34093256",
            "657025dfcfc010a0f5006a3b",
            "657025cfbfc87b3a34093253"))
        {
            return 20;
        }

        if (IsTplMatch(packTpl,
            "64898838d5b4df6140000a20",
            "65702474bfc87b3a34093226"))
        {
            return 25;
        }

        if (IsTplMatch(packTpl,
            "648987d673c462723909a151",
            "65702591c5d7d4cb4d07857c"))
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

    private List<List<PaymentConfigItem>> GenerateAutoBarterScheme(PriceConfigItem priceConfig, SettingsConfig settings, TraderAssort? currentAssort, Dictionary<string, int> barterItemUsage)
    {
        var targetTpl = priceConfig.TplId;
        if (string.IsNullOrWhiteSpace(targetTpl))
        {
            return [];
        }

        var baseValue = ResolveAutoBarterTargetValue(priceConfig, settings, currentAssort);

        var targetValue = ApplyGeneratedValueMode(
            baseValue,
            settings.GeneratedBarterPriceMode,
            settings.GeneratedBarterPriceOffsetPercent);

        targetValue = Math.Max(1, targetValue);

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

        var candidates = GetAutoBarterCandidates(settings)
            .Where(x => !excludedTpls.Contains(x.TplId))
            .Where(x => !YATMConfig.IsCurrencyTemplate(x.TplId))
            .Where(x => x.Price >= minItemPrice)
            .Where(x => x.Price <= targetValue * 1.5 || x.Price <= baseValue * 1.5)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = GetAutoBarterCandidates(settings)
                .Where(x => !excludedTpls.Contains(x.TplId))
                .Where(x => !YATMConfig.IsCurrencyTemplate(x.TplId))
                .Where(x => x.Price > 0)
                .ToList();
        }

        if (candidates.Count == 0)
        {
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

        for (var slot = 0; slot < maxDifferentItems && remaining > 0; slot++)
        {
            if (!allowOverTarget && !HasAffordableCandidate(candidates, usedTpls, remaining))
            {
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
                allowOverTarget);

            if (pick == null)
            {
                break;
            }

            var count = CalculateAutoBarterCount(pick.Price, idealValue, remaining, maxItemCount, allowOverTarget);
            if (count <= 0)
            {
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
                AddGeneratedBarterUsage(barterItemUsage, pick.TplId, count);
            }

            remaining -= componentValue;

            if (allowOverTarget && GetSelectedBarterValue(selected, candidates) >= targetValue)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
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
                settings.GeneratedBarterBalanceItemUsage);
        }

        return [selected];
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
        return candidates.Any(x => !usedTpls.Contains(x.TplId) && x.Price > 0 && x.Price <= remainingValue);
    }

    private static int CalculateAutoBarterCount(
        double itemPrice,
        double idealValue,
        double remainingValue,
        int maxItemCount,
        bool allowOverTarget)
    {
        if (itemPrice <= 0)
        {
            return 0;
        }

        var count = (int)Math.Round(idealValue / itemPrice, MidpointRounding.AwayFromZero);
        count = Math.Clamp(count, 1, maxItemCount);

        if (allowOverTarget)
        {
            return count;
        }

        var maxAffordableCount = (int)Math.Floor(remainingValue / itemPrice);
        if (maxAffordableCount <= 0)
        {
            return 0;
        }

        return Math.Clamp(count, 1, Math.Min(maxItemCount, maxAffordableCount));
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

    private static void AddGeneratedBarterUsage(Dictionary<string, int> barterItemUsage, string tpl, double count)
    {
        barterItemUsage.TryGetValue(tpl, out var currentUsage);
        // Count larger component stacks as heavier usage so one item does not
        // become the answer for every generated barter recipe.
        barterItemUsage[tpl] = currentUsage + Math.Max(1, (int)Math.Ceiling(count));
    }

    private static void TryRaiseGeneratedBarterToAtLeastTarget(
        List<PaymentConfigItem> selected,
        IReadOnlyList<BarterCandidate> candidates,
        HashSet<string> usedTpls,
        Dictionary<string, int> barterItemUsage,
        double targetValue,
        int maxDifferentItems,
        int maxItemCount,
        bool balanceItemUsage)
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
            BarterCandidate? newCandidate = null;
            double bestNewTotal = double.MaxValue;
            var bestUsage = int.MaxValue;

            foreach (var item in selected)
            {
                if (item.Count >= maxItemCount || !pricesByTpl.TryGetValue(item.TplId, out var candidate))
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
                    newCandidate = null;
                    bestNewTotal = newTotal;
                    bestUsage = usage;
                }
            }

            if (selected.Count < maxDifferentItems)
            {
                foreach (var candidate in candidates.Where(x => !usedTpls.Contains(x.TplId) && x.Price > 0))
                {
                    var count = Math.Clamp((int)Math.Ceiling((targetValue - currentValue) / candidate.Price), 1, maxItemCount);
                    var newTotal = currentValue + candidate.Price * count;
                    var usage = balanceItemUsage && barterItemUsage.TryGetValue(candidate.TplId, out var existingUsage)
                        ? existingUsage
                        : 0;

                    if (newTotal >= targetValue && (newTotal < bestNewTotal || (Math.Abs(newTotal - bestNewTotal) < 0.01 && usage < bestUsage)))
                    {
                        existingToIncrement = null;
                        newCandidate = candidate;
                        bestNewTotal = newTotal;
                        bestUsage = usage;
                    }
                }
            }

            if (existingToIncrement != null)
            {
                existingToIncrement.Count += 1;
                currentValue = bestNewTotal;
                if (balanceItemUsage)
                {
                    AddGeneratedBarterUsage(barterItemUsage, existingToIncrement.TplId, 1);
                }
                continue;
            }

            if (newCandidate != null)
            {
                var count = Math.Clamp((int)Math.Ceiling((targetValue - currentValue) / newCandidate.Price), 1, maxItemCount);
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
                    AddGeneratedBarterUsage(barterItemUsage, newCandidate.TplId, count);
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
        var limit = category switch
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

        if (IsPairedAmmoLooseConfig(priceConfig))
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
        bool allowOverTarget)
    {
        var scoredCandidates = candidates
            .Where(x => !usedTpls.Contains(x.TplId))
            .Select(x =>
            {
                var count = CalculateAutoBarterCount(x.Price, idealValue, remainingValue, maxItemCount, allowOverTarget);
                if (count <= 0)
                {
                    return null;
                }

                var value = x.Price * count;
                var score = Math.Abs(value - idealValue) / Math.Max(1, idealValue);

                if (allowOverTarget && value > remainingValue * 1.5)
                {
                    score += 2.0;
                }

                var usage = 0;
                if (balanceItemUsage)
                {
                    barterItemUsage.TryGetValue(x.TplId, out usage);
                }

                return new { Candidate = x, Score = score, Usage = usage };
            })
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => balanceItemUsage ? x.Usage : 0)
            .ThenBy(x => x.Score)
            .ToList();

        if (scoredCandidates.Count == 0)
        {
            return null;
        }

        if (randomizeSelection)
        {
            // Keep the value close, but prefer the least-used whitelist items first so
            // one cheap/efficient item does not dominate every generated barter.
            var randomPool = scoredCandidates
                .Take(Math.Min(12, scoredCandidates.Count))
                .ToList();

            lock (AutoBarterRandom)
            {
                return randomPool[AutoBarterRandom.Next(randomPool.Count)].Candidate;
            }
        }

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

    private List<BarterCandidate> GetAutoBarterCandidates(SettingsConfig settings)
    {
        if (_autoBarterCandidates != null)
        {
            return _autoBarterCandidates;
        }

        var whitelistTplIds = LoadOrCreateGeneratedBarterWhitelist(settings);

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
            if (whitelistTplIds.Count == 0)
            {
                YATMLogger.Log("[GeneratedBarters] Whitelist is enabled but no enabled tpl IDs were found. Auto-barter candidates are empty.");
                _autoBarterCandidates = [];
                return _autoBarterCandidates;
            }

            query = query.Where(x => whitelistTplIds.Contains(x.Key));
        }

        _autoBarterCandidates = query
            .Select(x => new BarterCandidate(
                x.Key,
                ResolveItemName(x.Key),
                ResolveGeneratedBarterComponentPrice(x.Key, x.Value, settings)))
            .Where(x => x.Price > 0)
            .OrderBy(x => x.TplId)
            .ToList();

        if (settings.GeneratedBarterUseWhitelist)
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Built auto-barter whitelist candidate pool with {_autoBarterCandidates.Count} priced valuable item(s).");
        }
        else
        {
            YATMLogger.LogDebug($"[GeneratedBarters] Built auto-barter candidate pool with {_autoBarterCandidates.Count} priced item(s). Whitelist disabled.");
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

        var relativePath = string.IsNullOrWhiteSpace(settings.GeneratedBarterWhitelistPath)
            ? "config/generated_barter_whitelist.jsonc"
            : settings.GeneratedBarterWhitelistPath;

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var whitelistPath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(pathToMod, relativePath);

        try
        {
            var dir = Path.GetDirectoryName(whitelistPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(whitelistPath))
            {
                SeedGeneratedBarterWhitelistFromTemplate(whitelistPath, relativePath, "missing");
            }

            var json = File.ReadAllText(whitelistPath);
            var config = JsonSerializer.Deserialize<GeneratedBarterWhitelistConfig>(json, JsonOptions)
                         ?? CreateEmptyGeneratedBarterWhitelist();

            if (config.Items.Count == 0 && TryLoadDefaultGeneratedBarterWhitelist(out var defaultWhitelist) && defaultWhitelist.Items.Count > 0)
            {
                config = defaultWhitelist;
                File.WriteAllText(whitelistPath, JsonSerializer.Serialize(config, JsonOptions));
                YATMLogger.Log($"[GeneratedBarters] Repaired empty barter whitelist from data template: {relativePath}");
            }

            return config.Items
                .Where(x => x.Enabled)
                .Select(x => x.TplId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !YATMConfig.IsCurrencyTemplate(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[GeneratedBarters] Failed to load whitelist {relativePath}: {ex.Message}. Auto-barter will use no whitelist items this run.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
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
            SchemaVersion = 1,
            Description = "Auto-generated Tony barters can only use enabled tpl IDs in this file when GeneratedBarterUseWhitelist is true. Add valuables/barter loot only: no weapons, armor, ammo, rigs, plates, or attachments.",
            Items = []
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
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{seed}:{tpl}:{slot}"));
        var value = BitConverter.ToUInt32(bytes, 0);
        return value / (double)uint.MaxValue;
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
        // config/items.json is loaded directly by YATMConfig and is not duplicated here.
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

    private sealed record BarterCandidate(string TplId, string ItemName, double Price);

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
