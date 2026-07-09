using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using YetAnotherTraderMod.config;
using YetAnotherTraderMod.src.GeneratedOffers;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.Services.Runtime;

[Injectable(InjectionType.Singleton)]
public sealed class YATMTraderAssortRuntimeRollService(
    DatabaseServer databaseServer,
    YATMGeneratedOfferService generatedOfferService,
    ModHelper modHelper)
{
    private static readonly Random _random = new();

    // These remember the previous randomized roll so the next restock can avoid
    // picking the same items again when there are enough alternatives.
    private static readonly HashSet<string> _lastRandomBarterRollKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _lastOutOfStockRollKeys = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RollCandidate(string OfferId, string RollKey);

    private sealed record CashMarketPriceCandidate(
        string OfferId,
        string TplId,
        IReadOnlyList<MarketCashPriceComponent> PriceComponents,
        string PriceSnapshotKey,
        string MarketPriceCategory,
        int MarketPricePercent,
        object CashComponent)
    {
        public bool IsPresetBundle => PriceComponents.Count > 1
                                      || PriceComponents.Any(x => Math.Abs(x.Count - 1) > 0.001);
    }

    private sealed class MarketCashPriceCacheFile
    {
        public int SchemaVersion { get; set; } = 4;
        public string Description { get; set; } = "Generated Tony market price cache used before barter generation. Safe to delete; Tony recreates it when needed.";
        public string CacheKey { get; set; } = string.Empty;
        public string BlendMode { get; set; } = string.Empty;
        public int WeaponCashPricePercent { get; set; } = 50;
        public int ArmorCashPricePercent { get; set; } = 50;
        public int OfferCount { get; set; }
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<string, MarketCashPriceCacheEntry> Offers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MarketCashPriceCacheEntry
    {
        public string OfferId { get; set; } = string.Empty;
        public string TplId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string PriceSnapshotKey { get; set; } = string.Empty;
        public string MarketPriceCategory { get; set; } = "Default";
        public int MarketPricePercent { get; set; } = 100;
        public List<MarketCashPriceComponent> Components { get; set; } = [];
        public double FleaPrice { get; set; }
        public double TraderBestPrice { get; set; }
        public double RawFinalPrice { get; set; }
        public double FinalPrice { get; set; }
    }

    private sealed record AmmoPackBarterOfferLimitsData(
        PriceConfigItem PriceConfig,
        int LooseBuyRestrictionMax,
        int PackSize,
        int PackBuyRestrictionMax);

    private sealed class PaymentRollResult
    {
        public Dictionary<string, AmmoPackBarterOfferLimitsData> AmmoPackBarterOffersById { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Filled by the completed payment roll. The stock roll can use this
        // to prevent barter offers from becoming out of stock when the setting is enabled.
        public HashSet<string> BarterOfferIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Completed { get; private set; }

        public void MarkCompleted()
        {
            Completed = true;
        }
    }

    public void ApplyRuntimeAssortRolls(TraderAssort assort, YATMConfig config, string rollReason)
    {
        // HARD ORDER GUARANTEE:
        // 1) Start from the clean assort object after base/custom/addon content has been merged.
        // 2) Remove standalone ammo-pack helper roots so the visible rollable snapshot is stable.
        // 3) Market-price Tony's configured offer rows before the payment roll.
        //    Generated barters then use the same weapon/armor-adjusted price that cash offers will use.
        // 4) Roll which offers become barter before generating barter schemes.
        // 5) Generate barter schemes only for those selected barter offers, using the whitelist and balanced usage.
        // 6) Apply the completed payment roll. Ammo barter offers keep the same OfferId and swap _tpl to the pack.
        // 7) Only after paymentRollResult.Completed is true, run the stock roll.
        //    Stock reads the payment result; it does not decide barter state.
        RemoveStandaloneAmmoPackRootOffers(assort, config);
        var marketPricesPrimedBeforeBarters = PrimeMarketCashPricesForConfiguredOffers(assort, config, rollReason);

        var paymentRollResult = RollPayments(assort, config, rollReason);

        if (!paymentRollResult.Completed)
        {
            throw new InvalidOperationException($"[{rollReason}] Payment roll did not complete. Stock roll was not started.");
        }

        RollStock(assort, config, rollReason, paymentRollResult);

        if (!marketPricesPrimedBeforeBarters)
        {
            // Safety fallback for unprimed/legacy setups. Normal runs already wrote market prices
            // into PriceConfig before barter generation, so ApplyCashPaymentToOffer used the same values.
            RepriceCashOffersFromMarket(assort, config, rollReason);
        }
        else
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Final cash reprice skipped; market prices were primed before barter generation.");
        }
    }

    private void RollStock(
        TraderAssort assort,
        YATMConfig config,
        string rollReason,
        PaymentRollResult paymentRollResult)
    {
        if (!config.Settings.RandomizeStockAvailable && !config.Settings.UnlimitedStock)
        {
            return;
        }

        YATMLogger.LogDebug($"[{rollReason}] Starting Stock Manipulation...");

        var outOfStockNames = new List<string>();
        var random = _random;

        int modifiedCount = 0;
        int zeroedCount = 0;

        var locales = databaseServer.GetTables().Locales.Global["en"];

        var priceConfigsByOfferId = config.Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
            .GroupBy(x => x.OfferId!)
            .ToDictionary(x => x.Key, x => x.First());

        var priceConfigsByTplId = config.Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.TplId))
            .GroupBy(x => x.TplId)
            .ToDictionary(x => x.Key, x => x.First());

        var selectedOutOfStockOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preventBarterOffersOutOfStock = GetBoolSetting(config.Settings, "PreventBarterOffersOutOfStock", true);

        if (preventBarterOffersOutOfStock)
        {
            YATMLogger.LogDebug($"[{rollReason}] PreventBarterOffersOutOfStock enabled: barter offers will be excluded from the random out-of-stock pool.");
        }

        if (config.Settings.RandomizeStockAvailable)
        {
            var eligibleOutOfStockCandidates = new List<RollCandidate>();

            foreach (var candidateItem in assort.Items)
            {
                if (candidateItem.ParentId != "hideout" || candidateItem.Upd == null || string.IsNullOrWhiteSpace(candidateItem.Id))
                {
                    continue;
                }

                var candidateTpl = YATMConfig.GetTemplateId(candidateItem);

                if (IsConfiguredAmmoPackTpl(config, candidateTpl))
                {
                    continue;
                }

                // Ammo pack barter offers keep special stock limits and are not part of the random OOS pool.
                // Check by OfferId first because the _tpl swap can fail readback on some SPT model wrappers
                // even after the serialized value has been updated.
                if (paymentRollResult.AmmoPackBarterOffersById.ContainsKey(candidateItem.Id))
                {
                    continue;
                }

                if (preventBarterOffersOutOfStock
                    && (paymentRollResult.BarterOfferIds.Contains(candidateItem.Id)
                        || OfferUsesNonCurrencyPayment(assort, candidateItem.Id)))
                {
                    continue;
                }

                var candidatePriceConfig = FindPriceConfigForStock(
                    candidateItem,
                    candidateTpl,
                    priceConfigsByOfferId,
                    priceConfigsByTplId);

                if (candidatePriceConfig != null && IsAlwaysInStock(candidatePriceConfig))
                {
                    continue;
                }

                eligibleOutOfStockCandidates.Add(new RollCandidate(candidateItem.Id, GetStockRollKey(candidateItem, candidateTpl)));
            }

            // If the same item exists more than once, only one copy can be zeroed in a single roll.
            eligibleOutOfStockCandidates = eligibleOutOfStockCandidates
                .GroupBy(x => x.RollKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderBy(_ => random.Next()).First())
                .ToList();

            var requestedOutOfStockCount = (int)Math.Round(
                eligibleOutOfStockCandidates.Count * (Math.Clamp(config.Settings.OutOfStockChance, 0, 100) / 100.0),
                MidpointRounding.AwayFromZero);

            requestedOutOfStockCount = Math.Clamp(requestedOutOfStockCount, 0, eligibleOutOfStockCandidates.Count);

            var freshOutOfStockCandidates = eligibleOutOfStockCandidates
                .Where(x => !_lastOutOfStockRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            var repeatOutOfStockCandidates = eligibleOutOfStockCandidates
                .Where(x => _lastOutOfStockRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            var selectedOutOfStockCandidates = freshOutOfStockCandidates
                .Take(requestedOutOfStockCount)
                .Concat(repeatOutOfStockCandidates.Take(Math.Max(0, requestedOutOfStockCount - freshOutOfStockCandidates.Count)))
                .ToList();

            selectedOutOfStockOfferIds = selectedOutOfStockCandidates
                .Select(x => x.OfferId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var freshSelectedOutOfStockCount = selectedOutOfStockCandidates.Count(x => !_lastOutOfStockRollKeys.Contains(x.RollKey));
            ReplaceHashSetContents(_lastOutOfStockRollKeys, selectedOutOfStockCandidates.Select(x => x.RollKey));

            YATMLogger.LogDebug($"[{rollReason}] Non-repeat stock selection: selected {selectedOutOfStockOfferIds.Count} out-of-stock offers ({freshSelectedOutOfStockCount} fresh, {selectedOutOfStockOfferIds.Count - freshSelectedOutOfStockCount} reused).");

            if (selectedOutOfStockOfferIds.Count > freshSelectedOutOfStockCount)
            {
                YATMLogger.LogDebug($"[{rollReason}] Reused {selectedOutOfStockOfferIds.Count - freshSelectedOutOfStockCount} previous out-of-stock picks because there were not enough fresh eligible offers.");
            }
        }
        else
        {
            _lastOutOfStockRollKeys.Clear();
        }

        foreach (var item in assort.Items)
        {
            if (item.ParentId != "hideout")
            {
                continue;
            }

            if (item.Upd == null)
            {
                YATMLogger.LogDebug($"[Stock] Skipping offer with no Upd data: {item.Id}");
                continue;
            }

            string itemName = item.Id;
            var tpl = YATMConfig.GetTemplateId(item);

            if (!string.IsNullOrEmpty(tpl)
                && locales.Value != null
                && locales.Value.TryGetValue($"{tpl} Name", out var nameVal))
            {
                itemName = nameVal?.ToString() ?? item.Id;
            }

            var priceConfigForStock = FindPriceConfigForStock(
                item,
                tpl,
                priceConfigsByOfferId,
                priceConfigsByTplId);

            // Ammo offers that rolled into barter were already switched to the pack tpl
            // during the completed payment pass. The stock pass only applies pack stock limits.
            if (paymentRollResult.AmmoPackBarterOffersById.TryGetValue(item.Id, out var selectedAmmoPackLimitsData))
            {
                ApplyAmmoPackBarterOfferLimits(item, selectedAmmoPackLimitsData);
                modifiedCount++;
                continue;
            }

            // Cash/loose ammo starts from clean assort.json and pack offers are separate,
            // so there are no stale pack-only limits to clear here.

            if (priceConfigForStock != null && IsAlwaysInStock(priceConfigForStock))
            {
                ApplyAlwaysInStockOfferLimits(item, itemName, config.Settings.UnlimitedStock);
                modifiedCount++;
                continue;
            }

            if (config.Settings.RandomizeStockAvailable && selectedOutOfStockOfferIds.Contains(item.Id))
            {
                // Keep the offer loaded in the trader assort.
                // Do not remove the root item, child items, barter scheme, or loyalty entry.
                // Setting stock to 0 makes it show as out of stock instead of disappearing.
                item.Upd.UnlimitedCount = false;
                item.Upd.StackObjectsCount = 0;

                if (item.Upd.BuyRestrictionMax > 0)
                {
                    item.Upd.BuyRestrictionCurrent = 0;
                }

                zeroedCount++;
                outOfStockNames.Add($"{itemName} ({item.Id})");

                YATMLogger.LogDebug($"[Random Stock] zeroed stock: {itemName} ({item.Id})");
                continue;
            }

            if (config.Settings.UnlimitedStock)
            {
                item.Upd.UnlimitedCount = true;
                item.Upd.StackObjectsCount = 999999;

                if (item.Upd.BuyRestrictionMax > 0)
                {
                    item.Upd.BuyRestrictionMax = 9999;
                    item.Upd.BuyRestrictionCurrent = 0;
                }

                modifiedCount++;
            }
            else
            {
                item.Upd.UnlimitedCount = false;

                if (item.Upd.BuyRestrictionMax > 0)
                {
                    item.Upd.BuyRestrictionCurrent = 0;
                }

                modifiedCount++;
            }
        }

        YATMLogger.LogDebug($"[{rollReason}] Total items modified for Stock setting: {modifiedCount}");

        if (zeroedCount > 0)
        {
            YATMLogger.Log($"[{rollReason}] [Stock] Zeroed {zeroedCount} offers due to randomization.");
            YATMLogger.LogRealDebug($"Out of Stock Items:\n  {string.Join("\n  ", outOfStockNames)}");
        }
        else
        {
            YATMLogger.LogDebug($"[{rollReason}] No items were zeroed by randomization this turn.");
        }
    }

    private static bool IsConfiguredAmmoPackTpl(YATMConfig config, string? tpl)
    {
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return false;
        }

        return config.Prices.Any(priceConfig =>
            string.Equals(
                GetStringMember(priceConfig, "AmmoBarterPackTplId"),
                tpl,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool OfferUsesNonCurrencyPayment(TraderAssort assort, string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId)
            || !assort.BarterScheme.TryGetValue(offerId, out var schemeList)
            || schemeList == null)
        {
            return false;
        }

        foreach (var paymentOption in schemeList)
        {
            if (paymentOption == null)
            {
                continue;
            }

            foreach (var component in paymentOption)
            {
                var tpl = component?.Template.ToString();
                if (!string.IsNullOrWhiteSpace(tpl) && !YATMConfig.IsCurrencyTemplate(tpl))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool PrimeMarketCashPricesForConfiguredOffers(TraderAssort assort, YATMConfig config, string rollReason)
    {
        if (!config.Settings.MarketRepriceCashOffers)
        {
            return false;
        }

        var rootItems = assort.Items
            .Where(x => x.ParentId == "hideout")
            .ToList();

        if (rootItems.Count == 0 || config.Prices.Count == 0)
        {
            return false;
        }

        var childrenByParentId = BuildChildrenByParentId(assort);
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

        var configuredOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configCandidates = new List<(CashMarketPriceCandidate Candidate, PriceConfigItem PriceConfig)>();
        var skippedNonRubCount = 0;
        var skippedMissingTplCount = 0;
        var skippedCurrencyTplCount = 0;
        var skippedHelperCount = 0;

        foreach (var priceConfig in config.Prices)
        {
            if (priceConfig == null)
            {
                continue;
            }

            var configuredCurrency = string.IsNullOrWhiteSpace(priceConfig.Currency)
                ? "RUB"
                : priceConfig.Currency.Trim().ToUpperInvariant();

            if (!string.Equals(configuredCurrency, "RUB", StringComparison.OrdinalIgnoreCase))
            {
                skippedNonRubCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(priceConfig.TplId) && string.IsNullOrWhiteSpace(priceConfig.OfferId))
            {
                skippedMissingTplCount++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(priceConfig.TplId) && YATMConfig.IsCurrencyTemplate(priceConfig.TplId))
            {
                skippedCurrencyTplCount++;
                continue;
            }

            if (IsPairedAmmoPackHelperConfig(priceConfig, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
            {
                skippedHelperCount++;
                continue;
            }

            var matchingOffers = rootItems
                .Where(item => DoesConfigMatchOffer(item, priceConfig))
                .ToList();

            if (matchingOffers.Count == 0)
            {
                continue;
            }

            if (matchingOffers.Count > 1 && string.IsNullOrWhiteSpace(priceConfig.OfferId))
            {
                YATMLogger.LogDebug($"[MarketPricing] Multiple offers matched TplId {priceConfig.TplId}. Add OfferId to manual_offers.jsonc for exact weapon preset market pricing.");
            }

            foreach (var offer in matchingOffers)
            {
                var offerId = GetMemberValue(offer, "Id")?.ToString();
                if (string.IsNullOrWhiteSpace(offerId))
                {
                    continue;
                }

                if (!configuredOfferIds.Add(offerId))
                {
                    continue;
                }

                var soldTpl = YATMConfig.GetTemplateId(offer);
                if (string.IsNullOrWhiteSpace(soldTpl))
                {
                    skippedMissingTplCount++;
                    continue;
                }

                if (YATMConfig.IsCurrencyTemplate(soldTpl))
                {
                    skippedCurrencyTplCount++;
                    continue;
                }

                var priceComponents = BuildMarketCashPriceComponents(offer, offerId, soldTpl, childrenByParentId);
                var marketPriceCategory = ResolveMarketCashPriceCategory(soldTpl);
                var marketPricePercent = ResolveMarketCashPricePercentForCategory(marketPriceCategory, config.Settings);
                var priceSnapshotKey = BuildMarketCashPriceSnapshotKey(priceComponents, marketPriceCategory, marketPricePercent);

                configCandidates.Add((
                    new CashMarketPriceCandidate(
                        offerId,
                        soldTpl,
                        priceComponents,
                        priceSnapshotKey,
                        marketPriceCategory,
                        marketPricePercent,
                        priceConfig),
                    priceConfig));
            }
        }

        if (configCandidates.Count == 0)
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] No configured RUB offer rows found for pre-barter market pricing.");
            LogMarketPricingPreRollSkipSummary(rollReason, skippedNonRubCount, skippedMissingTplCount, skippedCurrencyTplCount, skippedHelperCount);
            return false;
        }

        var candidates = configCandidates.Select(x => x.Candidate).ToList();
        var cacheKey = BuildMarketCashPriceCacheKey(candidates, config.Settings);

        if (config.Settings.MarketCashPriceCacheEnabled
            && TryLoadMatchingMarketCashPriceCache(config.Settings, cacheKey, candidates, out var cachedPrices))
        {
            var cachedChangedCount = 0;
            foreach (var (candidate, priceConfig) in configCandidates)
            {
                if (!cachedPrices.TryGetValue(candidate.OfferId, out var cachedEntry) || cachedEntry.FinalPrice <= 0)
                {
                    continue;
                }

                if (ApplyMarketPriceToPriceConfig(priceConfig, cachedEntry.FinalPrice, candidate.OfferId, candidate.TplId, "cached"))
                {
                    cachedChangedCount++;
                }
            }

            YATMLogger.Log(
                $"[{rollReason}] [MarketPricing] Reused cached pre-barter market prices for {configCandidates.Count} configured offer row(s) " +
                $"from {NormalizeGeneratedPath(config.Settings.MarketCashPriceCachePath)}.");

            if (cachedChangedCount > 0)
            {
                YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Applied {cachedChangedCount} cached pre-barter price update(s). Generated barters will use these prices.");
            }

            LogMarketPricingPreRollSkipSummary(rollReason, skippedNonRubCount, skippedMissingTplCount, skippedCurrencyTplCount, skippedHelperCount);
            return true;
        }

        var priceCacheBySnapshot = new Dictionary<string, MarketCashPriceResult>(StringComparer.OrdinalIgnoreCase);
        var cacheEntriesByOfferId = new Dictionary<string, MarketCashPriceCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var changedCount = 0;

        foreach (var (candidate, priceConfig) in configCandidates)
        {
            if (!priceCacheBySnapshot.TryGetValue(candidate.PriceSnapshotKey, out var marketPrice))
            {
                var rawMarketPrice = generatedOfferService.ResolveMarketCashPrice(candidate.PriceComponents, config.Settings);
                marketPrice = ApplyMarketCashPriceCategoryPercent(rawMarketPrice, candidate);
                priceCacheBySnapshot[candidate.PriceSnapshotKey] = marketPrice;
            }

            if (marketPrice.FinalPrice <= 0)
            {
                continue;
            }

            if (ApplyMarketPriceToPriceConfig(priceConfig, marketPrice.FinalPrice, candidate.OfferId, candidate.TplId, marketPrice.Mode))
            {
                changedCount++;
            }

            cacheEntriesByOfferId[candidate.OfferId] = new MarketCashPriceCacheEntry
            {
                OfferId = candidate.OfferId,
                TplId = candidate.TplId,
                Mode = marketPrice.Mode,
                PriceSnapshotKey = candidate.PriceSnapshotKey,
                MarketPriceCategory = candidate.MarketPriceCategory,
                MarketPricePercent = candidate.MarketPricePercent,
                Components = candidate.PriceComponents.ToList(),
                FleaPrice = marketPrice.FleaPrice,
                TraderBestPrice = marketPrice.TraderBestPrice,
                RawFinalPrice = ResolveRawMarketFinalPriceForLog(marketPrice, candidate),
                FinalPrice = marketPrice.FinalPrice
            };
        }

        if (config.Settings.MarketCashPriceCacheEnabled)
        {
            SaveMarketCashPriceCache(config.Settings, cacheKey, configCandidates.Count, cacheEntriesByOfferId, rollReason);
        }

        if (changedCount > 0)
        {
            YATMLogger.Log(
                $"[{rollReason}] [MarketPricing] Pre-priced {changedCount} configured offer row(s) before barter generation using {config.Settings.MarketCashPriceBlendMode}. " +
                "Generated barters and cash offers now share the same market-adjusted values.");
        }
        else
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Pre-barter market pricing found no configured offer prices that needed changing.");
        }

        LogMarketPricingPreRollSkipSummary(rollReason, skippedNonRubCount, skippedMissingTplCount, skippedCurrencyTplCount, skippedHelperCount);
        return true;
    }

    private static bool ApplyMarketPriceToPriceConfig(
        PriceConfigItem priceConfig,
        double marketPrice,
        string offerId,
        string tplId,
        string priceMode)
    {
        if (marketPrice <= 0)
        {
            return false;
        }

        var oldPrice = priceConfig.Price;
        var newPrice = Math.Max(1, Math.Round(marketPrice, 0, MidpointRounding.AwayFromZero));
        priceConfig.Currency = "RUB";

        if (oldPrice > 0 && Math.Abs(oldPrice - newPrice) <= 0.001)
        {
            return false;
        }

        priceConfig.Price = newPrice;
        YATMLogger.LogRealDebug(
            $"[MarketPricing] Pre-barter price {offerId} {tplId}: {oldPrice:0.##} -> {newPrice:0.##} RUB | Mode {priceMode}");
        return true;
    }

    private static void LogMarketPricingPreRollSkipSummary(
        string rollReason,
        int skippedNonRubCount,
        int skippedMissingTplCount,
        int skippedCurrencyTplCount,
        int skippedHelperCount)
    {
        if (skippedNonRubCount == 0
            && skippedMissingTplCount == 0
            && skippedCurrencyTplCount == 0
            && skippedHelperCount == 0)
        {
            return;
        }

        YATMLogger.LogDebug(
            $"[{rollReason}] [MarketPricing] Pre-barter skipped rows: non-RUB={skippedNonRubCount}, missing tpl/offer={skippedMissingTplCount}, currency tpl={skippedCurrencyTplCount}, ammo-pack helper={skippedHelperCount}.");
    }

    private void RepriceCashOffersFromMarket(TraderAssort assort, YATMConfig config, string rollReason)
    {
        if (!config.Settings.MarketRepriceCashOffers)
        {
            return;
        }

        var itemMap = assort.Items
            .Where(x => x.ParentId == "hideout" && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy<Item, string>(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var childrenByParentId = BuildChildrenByParentId(assort);

        var cashCandidates = new List<CashMarketPriceCandidate>();
        var skippedBarterOrMixedCount = 0;
        var skippedCurrencyCount = 0;
        var skippedMissingTplCount = 0;

        foreach (var itemSchemePair in assort.BarterScheme)
        {
            var offerId = itemSchemePair.Key;
            var schemeList = itemSchemePair.Value;

            if (!itemMap.TryGetValue(offerId, out var offer))
            {
                continue;
            }

            var soldTpl = YATMConfig.GetTemplateId(offer);
            if (string.IsNullOrWhiteSpace(soldTpl))
            {
                skippedMissingTplCount++;
                continue;
            }

            foreach (var schemeSubList in schemeList)
            {
                if (!TryGetSingleRubCashComponent(schemeSubList, out var cashComponent, out var skippedBecauseMixed, out var skippedBecauseCurrency))
                {
                    if (skippedBecauseCurrency)
                    {
                        skippedCurrencyCount++;
                    }
                    else if (skippedBecauseMixed)
                    {
                        skippedBarterOrMixedCount++;
                    }

                    continue;
                }

                var priceComponents = BuildMarketCashPriceComponents(offer, offerId, soldTpl, childrenByParentId);
                var marketPriceCategory = ResolveMarketCashPriceCategory(soldTpl);
                var marketPricePercent = ResolveMarketCashPricePercentForCategory(marketPriceCategory, config.Settings);
                var priceSnapshotKey = BuildMarketCashPriceSnapshotKey(priceComponents, marketPriceCategory, marketPricePercent);
                cashCandidates.Add(new CashMarketPriceCandidate(
                    offerId,
                    soldTpl,
                    priceComponents,
                    priceSnapshotKey,
                    marketPriceCategory,
                    marketPricePercent,
                    cashComponent));
            }
        }

        if (cashCandidates.Count == 0)
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] No final pure RUB cash offers found for repricing.");
            LogMarketPricingSkipSummary(rollReason, skippedBarterOrMixedCount, skippedCurrencyCount, skippedMissingTplCount);
            return;
        }

        var cacheKey = BuildMarketCashPriceCacheKey(cashCandidates, config.Settings);
        if (config.Settings.MarketCashPriceCacheEnabled
            && TryLoadMatchingMarketCashPriceCache(config.Settings, cacheKey, cashCandidates, out var cachedPrices))
        {
            var cachedChangedCount = ApplyCachedMarketCashPrices(cashCandidates, cachedPrices);
            YATMLogger.Log(
                $"[{rollReason}] [MarketPricing] Reused cached market cash prices for {cashCandidates.Count} final cash payment option(s) " +
                $"from {NormalizeGeneratedPath(config.Settings.MarketCashPriceCachePath)}.");

            if (cachedChangedCount > 0)
            {
                YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Applied {cachedChangedCount} cached cash price update(s). Global PriceMultiplier still applies after this step.");
            }

            LogMarketPricingSkipSummary(rollReason, skippedBarterOrMixedCount, skippedCurrencyCount, skippedMissingTplCount);
            return;
        }

        var priceCacheBySnapshot = new Dictionary<string, MarketCashPriceResult>(StringComparer.OrdinalIgnoreCase);
        var cacheEntriesByOfferId = new Dictionary<string, MarketCashPriceCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var changedCount = 0;

        foreach (var candidate in cashCandidates)
        {
            if (!priceCacheBySnapshot.TryGetValue(candidate.PriceSnapshotKey, out var marketPrice))
            {
                var rawMarketPrice = generatedOfferService.ResolveMarketCashPrice(candidate.PriceComponents, config.Settings);
                marketPrice = ApplyMarketCashPriceCategoryPercent(rawMarketPrice, candidate);
                priceCacheBySnapshot[candidate.PriceSnapshotKey] = marketPrice;
            }

            if (marketPrice.FinalPrice <= 0)
            {
                continue;
            }

            var oldPrice = GetDoubleMember(candidate.CashComponent, "Count", 0);
            var newPrice = marketPrice.FinalPrice;

            if (oldPrice <= 0 || Math.Abs(oldPrice - newPrice) > 0.001)
            {
                SetMemberValue(candidate.CashComponent, "Count", newPrice);
                changedCount++;

                YATMLogger.LogRealDebug(
                    $"[MarketPricing] {candidate.OfferId} {candidate.TplId}: {oldPrice:0.##} -> {newPrice:0.##} RUB | " +
                    $"Mode {marketPrice.Mode}, Flea {marketPrice.FleaPrice:0.##}, TBP {marketPrice.TraderBestPrice:0.##}, " +
                    $"Category {candidate.MarketPriceCategory} {candidate.MarketPricePercent}%, Components {candidate.PriceComponents.Count}");
            }

            cacheEntriesByOfferId[candidate.OfferId] = new MarketCashPriceCacheEntry
            {
                OfferId = candidate.OfferId,
                TplId = candidate.TplId,
                Mode = marketPrice.Mode,
                PriceSnapshotKey = candidate.PriceSnapshotKey,
                MarketPriceCategory = candidate.MarketPriceCategory,
                MarketPricePercent = candidate.MarketPricePercent,
                Components = candidate.PriceComponents.ToList(),
                FleaPrice = marketPrice.FleaPrice,
                TraderBestPrice = marketPrice.TraderBestPrice,
                RawFinalPrice = ResolveRawMarketFinalPriceForLog(marketPrice, candidate),
                FinalPrice = marketPrice.FinalPrice
            };
        }

        if (config.Settings.MarketCashPriceCacheEnabled)
        {
            SaveMarketCashPriceCache(config.Settings, cacheKey, cashCandidates.Count, cacheEntriesByOfferId, rollReason);
        }

        if (changedCount > 0)
        {
            YATMLogger.Log(
                $"[{rollReason}] [MarketPricing] Repriced {changedCount} final cash offer(s) using {config.Settings.MarketCashPriceBlendMode}. " +
                "Global PriceMultiplier still applies after this step.");
        }
        else
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] No final cash offers needed repricing.");
        }

        LogMarketPricingSkipSummary(rollReason, skippedBarterOrMixedCount, skippedCurrencyCount, skippedMissingTplCount);
    }

    private static Dictionary<string, List<object>> BuildChildrenByParentId(TraderAssort assort)
    {
        var childrenByParentId = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in assort.Items)
        {
            var itemId = GetMemberValue(item, "Id")?.ToString();
            var parentId = GetMemberValue(item, "ParentId")?.ToString();

            if (string.IsNullOrWhiteSpace(itemId)
                || string.IsNullOrWhiteSpace(parentId)
                || string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!childrenByParentId.TryGetValue(parentId, out var children))
            {
                children = [];
                childrenByParentId[parentId] = children;
            }

            children.Add(item);
        }

        return childrenByParentId;
    }

    private static IReadOnlyList<MarketCashPriceComponent> BuildMarketCashPriceComponents(
        object rootOffer,
        string rootOfferId,
        string rootTpl,
        IReadOnlyDictionary<string, List<object>> childrenByParentId)
    {
        var rawComponents = new List<MarketCashPriceComponent>();
        var visitedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(object item, string itemId, string? knownTpl, bool isRoot)
        {
            if (string.IsNullOrWhiteSpace(itemId) || !visitedItemIds.Add(itemId))
            {
                return;
            }

            var tpl = knownTpl ?? YATMConfig.GetTemplateId(item);
            if (!string.IsNullOrWhiteSpace(tpl) && !YATMConfig.IsCurrencyTemplate(tpl))
            {
                rawComponents.Add(new MarketCashPriceComponent(
                    tpl,
                    isRoot ? 1 : ResolveChildAssortItemQuantity(item)));
            }

            if (!childrenByParentId.TryGetValue(itemId, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                var childId = GetMemberValue(child, "Id")?.ToString();
                if (string.IsNullOrWhiteSpace(childId))
                {
                    continue;
                }

                Visit(child, childId, YATMConfig.GetTemplateId(child), isRoot: false);
            }
        }

        Visit(rootOffer, rootOfferId, rootTpl, isRoot: true);

        return rawComponents
            .Where(x => !string.IsNullOrWhiteSpace(x.TplId) && x.Count > 0)
            .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .Select(x => new MarketCashPriceComponent(x.Key, Math.Round(x.Sum(y => y.Count), 4)))
            .OrderBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double ResolveChildAssortItemQuantity(object item)
    {
        var quantity = 0.0;

        var upd = GetMemberValue(item, "Upd") ?? GetMemberValue(item, "upd");
        if (upd != null)
        {
            quantity = GetDoubleMember(upd, "StackObjectsCount", 0);
        }

        if (quantity <= 0)
        {
            quantity = GetDoubleMember(item, "StackObjectsCount", 0);
        }

        return quantity > 0
            ? Math.Round(quantity, 4)
            : 1;
    }

    private string ResolveMarketCashPriceCategory(string tpl)
    {
        if (generatedOfferService.IsWeaponTemplate(tpl))
        {
            return "Weapon";
        }

        if (generatedOfferService.IsArmorTemplate(tpl))
        {
            return "Armor";
        }

        return "Default";
    }

    private static int ResolveMarketCashPricePercentForCategory(string category, SettingsConfig settings)
    {
        return category switch
        {
            "Weapon" => Math.Clamp(settings.MarketWeaponCashPricePercent, 1, 100),
            "Armor" => Math.Clamp(settings.MarketArmorCashPricePercent, 1, 100),
            _ => 100
        };
    }

    private static MarketCashPriceResult ApplyMarketCashPriceCategoryPercent(
        MarketCashPriceResult price,
        CashMarketPriceCandidate candidate)
    {
        var percent = Math.Clamp(candidate.MarketPricePercent, 1, 100);
        if (price.FinalPrice <= 0 || percent >= 100)
        {
            return price;
        }

        var finalPrice = Math.Max(1, Math.Round(price.FinalPrice * (percent / 100.0), MidpointRounding.AwayFromZero));
        return price with { FinalPrice = finalPrice };
    }

    private static double ResolveRawMarketFinalPriceForLog(MarketCashPriceResult price, CashMarketPriceCandidate candidate)
    {
        var percent = Math.Clamp(candidate.MarketPricePercent, 1, 100);
        if (percent >= 100 || price.FinalPrice <= 0)
        {
            return price.FinalPrice;
        }

        return Math.Max(1, Math.Round(price.FinalPrice / (percent / 100.0), MidpointRounding.AwayFromZero));
    }

    private static string BuildMarketCashPriceSnapshotKey(
        IEnumerable<MarketCashPriceComponent> components,
        string marketPriceCategory,
        int marketPricePercent)
    {
        var snapshot = string.Join(
            "|",
            components
                .Where(x => !string.IsNullOrWhiteSpace(x.TplId) && x.Count > 0)
                .GroupBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
                .Select(x => new MarketCashPriceComponent(x.Key, Math.Round(x.Sum(y => y.Count), 4)))
                .OrderBy(x => x.TplId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.TplId}:{x.Count:0.####}"));

        snapshot = $"category={marketPriceCategory}|percent={Math.Clamp(marketPricePercent, 1, 100)}|{snapshot}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    private static int ApplyCachedMarketCashPrices(
        IEnumerable<CashMarketPriceCandidate> cashCandidates,
        IReadOnlyDictionary<string, MarketCashPriceCacheEntry> cachedPrices)
    {
        var changedCount = 0;

        foreach (var candidate in cashCandidates)
        {
            if (!cachedPrices.TryGetValue(candidate.OfferId, out var cachedEntry)
                || cachedEntry.FinalPrice <= 0
                || !string.Equals(cachedEntry.TplId, candidate.TplId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cachedEntry.PriceSnapshotKey, candidate.PriceSnapshotKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cachedEntry.MarketPriceCategory, candidate.MarketPriceCategory, StringComparison.OrdinalIgnoreCase)
                || cachedEntry.MarketPricePercent != candidate.MarketPricePercent)
            {
                continue;
            }

            var oldPrice = GetDoubleMember(candidate.CashComponent, "Count", 0);
            var newPrice = cachedEntry.FinalPrice;

            if (oldPrice <= 0 || Math.Abs(oldPrice - newPrice) > 0.001)
            {
                SetMemberValue(candidate.CashComponent, "Count", newPrice);
                changedCount++;
            }
        }

        return changedCount;
    }

    private static void LogMarketPricingSkipSummary(
        string rollReason,
        int skippedBarterOrMixedCount,
        int skippedCurrencyCount,
        int skippedMissingTplCount)
    {
        if (skippedBarterOrMixedCount > 0 || skippedCurrencyCount > 0 || skippedMissingTplCount > 0)
        {
            YATMLogger.LogDebug(
                $"[{rollReason}] [MarketPricing] Skipped {skippedBarterOrMixedCount} barter/mixed payment option(s), " +
                $"{skippedCurrencyCount} non-RUB cash option(s), {skippedMissingTplCount} offer(s) with missing tpl.");
        }
    }

    private bool TryLoadMatchingMarketCashPriceCache(
        SettingsConfig settings,
        string cacheKey,
        IReadOnlyCollection<CashMarketPriceCandidate> cashCandidates,
        out Dictionary<string, MarketCashPriceCacheEntry> cachedPrices)
    {
        cachedPrices = new Dictionary<string, MarketCashPriceCacheEntry>(StringComparer.OrdinalIgnoreCase);

        var cachePath = ResolveModRelativePath(settings.MarketCashPriceCachePath);
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<MarketCashPriceCacheFile>(File.ReadAllText(cachePath), MarketCashPriceCacheJsonOptions);
            if (cache == null
                || cache.SchemaVersion != 4
                || !string.Equals(cache.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase)
                || cache.OfferCount != cashCandidates.Count
                || cache.Offers.Count == 0)
            {
                return false;
            }

            foreach (var candidate in cashCandidates)
            {
                if (!cache.Offers.TryGetValue(candidate.OfferId, out var entry)
                    || !string.Equals(entry.TplId, candidate.TplId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.PriceSnapshotKey, candidate.PriceSnapshotKey, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.MarketPriceCategory, candidate.MarketPriceCategory, StringComparison.OrdinalIgnoreCase)
                    || entry.MarketPricePercent != candidate.MarketPricePercent
                    || entry.FinalPrice <= 0)
                {
                    return false;
                }

                cachedPrices[candidate.OfferId] = entry;
            }

            if (!MarketCashPriceCacheSpotCheckPassed(settings, cashCandidates, cachedPrices))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            YATMLogger.LogDebug($"[MarketPricing] Failed to read generated cash price cache {NormalizeGeneratedPath(settings.MarketCashPriceCachePath)}: {ex.Message}");
            return false;
        }
    }

    private bool MarketCashPriceCacheSpotCheckPassed(
        SettingsConfig settings,
        IEnumerable<CashMarketPriceCandidate> cashCandidates,
        IReadOnlyDictionary<string, MarketCashPriceCacheEntry> cachedPrices)
    {
        // Keep this small: the goal is to catch changed flea/TBP data without doing the full expensive pass.
        const int spotCheckCount = 8;

        var sampledCandidates = cashCandidates
            .GroupBy(x => x.PriceSnapshotKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderBy(y => y.OfferId, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(x => x.OfferId, StringComparer.OrdinalIgnoreCase)
            .Take(spotCheckCount)
            .ToList();

        foreach (var candidate in sampledCandidates)
        {
            if (!cachedPrices.TryGetValue(candidate.OfferId, out var cachedEntry))
            {
                return false;
            }

            var livePrice = ApplyMarketCashPriceCategoryPercent(
                generatedOfferService.ResolveMarketCashPrice(candidate.PriceComponents, settings),
                candidate);
            if (livePrice.FinalPrice <= 0)
            {
                return false;
            }

            if (!string.Equals(livePrice.Mode, cachedEntry.Mode, StringComparison.OrdinalIgnoreCase)
                || Math.Abs(livePrice.FinalPrice - cachedEntry.FinalPrice) > 0.001
                || Math.Abs(livePrice.FleaPrice - cachedEntry.FleaPrice) > 0.001
                || Math.Abs(livePrice.TraderBestPrice - cachedEntry.TraderBestPrice) > 0.001)
            {
                YATMLogger.LogDebug(
                    $"[MarketPricing] Cache spot-check changed for {candidate.OfferId} {candidate.TplId}: " +
                    $"cached {cachedEntry.FinalPrice:0.##} RUB, live {livePrice.FinalPrice:0.##} RUB. Rebuilding generated cash price cache.");
                return false;
            }
        }

        return true;
    }

    private void SaveMarketCashPriceCache(
        SettingsConfig settings,
        string cacheKey,
        int cashCandidateCount,
        Dictionary<string, MarketCashPriceCacheEntry> entriesByOfferId,
        string rollReason)
    {
        if (entriesByOfferId.Count == 0)
        {
            return;
        }

        var cachePath = ResolveModRelativePath(settings.MarketCashPriceCachePath);
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return;
        }

        try
        {
            var cacheDir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var cache = new MarketCashPriceCacheFile
            {
                CacheKey = cacheKey,
                BlendMode = NormalizeMarketCashPriceBlendMode(settings.MarketCashPriceBlendMode),
                WeaponCashPricePercent = Math.Clamp(settings.MarketWeaponCashPricePercent, 1, 100),
                ArmorCashPricePercent = Math.Clamp(settings.MarketArmorCashPricePercent, 1, 100),
                OfferCount = cashCandidateCount,
                GeneratedAtUtc = DateTime.UtcNow,
                Offers = entriesByOfferId
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
            };

            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, MarketCashPriceCacheJsonOptions));
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Saved generated cash price cache to {NormalizeGeneratedPath(settings.MarketCashPriceCachePath)}.");
        }
        catch (Exception ex)
        {
            YATMLogger.LogDebug($"[{rollReason}] [MarketPricing] Failed to save generated cash price cache {NormalizeGeneratedPath(settings.MarketCashPriceCachePath)}: {ex.Message}");
        }
    }

    private string ResolveModRelativePath(string? configuredPath)
    {
        configuredPath = NormalizeGeneratedPath(configuredPath);
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        return Path.Combine(pathToMod, configuredPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string NormalizeGeneratedPath(string? configuredPath)
    {
        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? "db/Generated/customAssort.json"
            : configuredPath.Trim().Replace('\\', '/');

        return normalized;
    }

    private static string BuildMarketCashPriceCacheKey(
        IEnumerable<CashMarketPriceCandidate> cashCandidates,
        SettingsConfig settings)
    {
        var snapshotBuilder = new StringBuilder();
        snapshotBuilder.Append("schema=3|");
        snapshotBuilder.Append("mode=").Append(NormalizeMarketCashPriceBlendMode(settings.MarketCashPriceBlendMode)).Append('|');
        snapshotBuilder.Append("weaponPct=").Append(Math.Clamp(settings.MarketWeaponCashPricePercent, 1, 100)).Append('|');
        snapshotBuilder.Append("armorPct=").Append(Math.Clamp(settings.MarketArmorCashPricePercent, 1, 100)).Append('|');
        snapshotBuilder.Append("externalFlea=").Append(settings.GeneratedPriceUseExternalFleaPriceFiles).Append('|');
        snapshotBuilder.Append("externalMode=").Append(settings.GeneratedPriceExternalFleaGameMode ?? string.Empty).Append('|');
        snapshotBuilder.Append("scanSiblings=").Append(settings.GeneratedPriceScanSiblingModsForFleaPriceFiles).Append('|');
        snapshotBuilder.Append("preferExternal=").Append(settings.GeneratedPricePreferExternalFleaPrices).Append('|');
        snapshotBuilder.Append("paths=").Append(string.Join(",", settings.GeneratedPriceExternalFleaPriceFilePaths ?? new List<string>())).Append('|');

        foreach (var candidate in cashCandidates
                     .OrderBy(x => x.OfferId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.TplId, StringComparer.OrdinalIgnoreCase))
        {
            snapshotBuilder.Append(candidate.OfferId)
                .Append('=')
                .Append(candidate.TplId)
                .Append('|')
                .Append(candidate.PriceSnapshotKey)
                .Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotBuilder.ToString())));
    }

    private static string NormalizeMarketCashPriceBlendMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "EqualParts";
        }

        return mode.Trim() switch
        {
            var value when value.Equals("AllFlea", StringComparison.OrdinalIgnoreCase) => "AllFlea",
            var value when value.Equals("HeavyFlea", StringComparison.OrdinalIgnoreCase) => "HeavyFlea",
            var value when value.Equals("AllTBP", StringComparison.OrdinalIgnoreCase) => "AllTBP",
            var value when value.Equals("AllTraderBestPrice", StringComparison.OrdinalIgnoreCase) => "AllTBP",
            var value when value.Equals("HeavyTBP", StringComparison.OrdinalIgnoreCase) => "HeavyTBP",
            var value when value.Equals("HeavyTraderBestPrice", StringComparison.OrdinalIgnoreCase) => "HeavyTBP",
            _ => "EqualParts"
        };
    }

    private static readonly JsonSerializerOptions MarketCashPriceCacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static bool TryGetSingleRubCashComponent(
        IEnumerable paymentOption,
        out object cashComponent,
        out bool skippedBecauseMixed,
        out bool skippedBecauseCurrency)
    {
        cashComponent = null!;
        skippedBecauseMixed = false;
        skippedBecauseCurrency = false;

        var components = paymentOption?.Cast<object>().Where(x => x != null).ToList() ?? new List<object>();
        if (components.Count != 1)
        {
            skippedBecauseMixed = true;
            return false;
        }

        var component = components[0];
        var tpl = GetMemberValue(component, "Template")?.ToString();
        if (!YATMConfig.IsCurrencyTemplate(tpl))
        {
            skippedBecauseMixed = true;
            return false;
        }

        var currency = YATMConfig.TemplateToCurrency(tpl);
        if (!string.Equals(currency, "RUB", StringComparison.OrdinalIgnoreCase))
        {
            skippedBecauseCurrency = true;
            return false;
        }

        cashComponent = component;
        return true;
    }

    public void ApplyPriceMultiplierToMoneyComponents(TraderAssort assort, YATMConfig config)
    {
        // Price multiplier only affects money components, not barter item counts.
        if (Math.Abs(config.Settings.PriceMultiplier - 1.0) <= 0.001)
        {
            return;
        }

        YATMLogger.LogDebug($"Applying Price Multiplier {config.Settings.PriceMultiplier}...");
        int changedCount = 0;

        var itemMap = assort.Items.ToDictionary(x => x.Id, x => x);
        var localesForPrice = databaseServer.GetTables().Locales.Global["en"];

        foreach (var itemSchemePair in assort.BarterScheme)
        {
            var itemId = itemSchemePair.Key;
            var schemeList = itemSchemePair.Value;

            foreach (var schemeSubList in schemeList)
            {
                foreach (var component in schemeSubList)
                {
                    if (component.Count.HasValue && YATMConfig.IsCurrencyTemplate(component.Template.ToString()))
                    {
                        var oldPrice = component.Count.Value;
                        component.Count = (double)Math.Round(component.Count.Value * config.Settings.PriceMultiplier);

                        string itemName = itemId;
                        if (itemMap.TryGetValue(itemId, out var item))
                        {
                            var tpl = YATMConfig.GetTemplateId(item);
                            if (!string.IsNullOrEmpty(tpl) && localesForPrice.Value != null && localesForPrice.Value.TryGetValue($"{tpl} Name", out var nameVal))
                            {
                                itemName = nameVal?.ToString() ?? itemId;
                            }
                        }

                        YATMLogger.LogRealDebug($"  Price adjust: {oldPrice} -> {component.Count} | {itemName} ({itemId})");
                        changedCount++;
                    }
                }
            }
        }

        YATMLogger.Log($"[Pricing] Applied Global Price Multiplier: {config.Settings.PriceMultiplier} to {changedCount} money components.");
    }

    private static PriceConfigItem? FindPriceConfigForStock(
        object offer,
        string? currentTpl,
        Dictionary<string, PriceConfigItem> priceConfigsByOfferId,
        Dictionary<string, PriceConfigItem> priceConfigsByTplId)
    {
        var offerId = GetMemberValue(offer, "Id")?.ToString();

        if (!string.IsNullOrWhiteSpace(offerId)
            && priceConfigsByOfferId.TryGetValue(offerId, out var byOfferIdConfig))
        {
            return byOfferIdConfig;
        }

        if (!string.IsNullOrWhiteSpace(currentTpl)
            && priceConfigsByTplId.TryGetValue(currentTpl, out var byTplConfig))
        {
            return byTplConfig;
        }

        return null;
    }

    private static void ApplyAlwaysInStockOfferLimits(object offer, string? itemName, bool unlimitedStock)
    {
        var upd = GetMemberValue(offer, "Upd");
        if (upd == null)
        {
            YATMLogger.LogDebug($"[Stock] AlwaysInStock skipped because offer has no Upd data: {itemName ?? "Unknown item"}");
            return;
        }

        if (unlimitedStock)
        {
            SetMemberValue(upd, "UnlimitedCount", true);
            SetMemberValue(upd, "StackObjectsCount", 999999);

            var buyRestrictionMax = GetIntMember(upd, "BuyRestrictionMax", 0);
            if (buyRestrictionMax > 0)
            {
                SetMemberValue(upd, "BuyRestrictionMax", 9999);
                SetMemberValue(upd, "BuyRestrictionCurrent", 0);
            }

            YATMLogger.LogDebug($"[Stock] AlwaysInStock protected unlimited offer: {itemName ?? "Unknown item"}");
            return;
        }

        SetMemberValue(upd, "UnlimitedCount", false);

        var existingBuyRestrictionMax = GetIntMember(upd, "BuyRestrictionMax", 0);
        if (existingBuyRestrictionMax > 0)
        {
            SetMemberValue(upd, "BuyRestrictionCurrent", 0);
        }

        YATMLogger.LogDebug($"[Stock] AlwaysInStock protected offer: {itemName ?? "Unknown item"} | StackObjectsCount preserved");
    }

    private PaymentRollResult RollPayments(TraderAssort assort, YATMConfig config, string rollReason)
    {
        RemoveStandaloneAmmoPackRootOffers(assort, config);

        var rootItems = assort.Items
            .Where(x => x.ParentId == "hideout")
            .ToList();

        var configuredOffers = new List<(object Offer, PriceConfigItem PriceConfig)>();
        var configuredOfferIds = new HashSet<string>();
        var paymentRollResult = new PaymentRollResult();

        foreach (var priceConfig in config.Prices)
        {
            var matchingOffers = rootItems
                .Where(item => DoesConfigMatchOffer(item, priceConfig))
                .ToList();

            if (matchingOffers.Count == 0)
            {
                YATMLogger.LogDebug($"[Pricing] No matching offer for {priceConfig.ItemName} / {priceConfig.TplId}");
                continue;
            }

            if (matchingOffers.Count > 1 && string.IsNullOrWhiteSpace(priceConfig.OfferId))
            {
                YATMLogger.LogDebug($"[Pricing] Multiple offers matched TplId {priceConfig.TplId}. Add OfferId to manual_offers.jsonc for exact control.");
            }

            foreach (var offer in matchingOffers)
            {
                var offerId = GetMemberValue(offer, "Id")?.ToString();
                if (string.IsNullOrWhiteSpace(offerId))
                {
                    YATMLogger.LogDebug($"[Pricing] Matched offer for {priceConfig.ItemName} has no Id.");
                    continue;
                }

                // Avoid applying the same offer more than once if manual_offers.jsonc has duplicate tpl matches.
                if (!configuredOfferIds.Add(offerId))
                {
                    YATMLogger.LogDebug($"[Pricing] Duplicate configured offer skipped: {priceConfig.ItemName} ({offerId})");
                    continue;
                }

                configuredOffers.Add((offer, priceConfig));
            }
        }

        if (configuredOffers.Count == 0)
        {
            YATMLogger.LogDebug("[Pricing] No configured offers were matched.");
            paymentRollResult.MarkCompleted();
            return paymentRollResult;
        }

        var CashOffersOnly = config.Settings.CashOffersOnly;
        var randomizeCashBarter = GetBoolSetting(config.Settings, "RandomizeCashBarterOffers", true) && !CashOffersOnly;
        var selectedBarterOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedBarterCandidateOfferIds = new List<string>();
        var targetGeneratedBarterCount = 0;
        // ManualBarters now means "prefer hard-written manual recipes when present",
        // not "disable generated barters." This keeps the normal barter roll working
        // while allowing manual_offers.jsonc/addon recipes to win for selected rows.
        var canGenerateBarters = !CashOffersOnly;

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

        if (randomizeCashBarter)
        {
            var cashPercent = Math.Clamp(GetIntSetting(config.Settings, "CashOfferPercent", 85), 0, 100);
            var barterPercent = 100 - cashPercent;

            var forcedBarterOfferIds = configuredOffers
                .Where(x => IsAlwaysBarter(x.PriceConfig) && CanOfferBecomeBarter(x.PriceConfig, canGenerateBarters, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
                .Select(x => GetMemberValue(x.Offer, "Id")?.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var invalidAlwaysBarterCount = configuredOffers
                .Count(x => IsAlwaysBarter(x.PriceConfig) && !CanOfferBecomeBarter(x.PriceConfig, canGenerateBarters, pairedAmmoPackOfferIds, pairedAmmoPackTplIds));

            if (invalidAlwaysBarterCount > 0)
            {
                YATMLogger.Log($"[Pricing] Warning: {invalidAlwaysBarterCount} AlwaysBarter rows cannot use manual or generated barter schemes and cannot be forced to barter.");
            }

            var random = _random;
            var eligibleRandomBarterCandidates = configuredOffers
                .Where(x => CanOfferBecomeBarter(x.PriceConfig, canGenerateBarters, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
                .Select(x =>
                {
                    var offerId = GetMemberValue(x.Offer, "Id")?.ToString();
                    if (string.IsNullOrWhiteSpace(offerId))
                    {
                        return null;
                    }

                    return new RollCandidate(offerId, GetPaymentRollKey(x.Offer, x.PriceConfig));
                })
                .Where(x => x != null)
                .Cast<RollCandidate>()
                .Where(x => !forcedBarterOfferIds.Contains(x.OfferId))
                // If the same item exists more than once, only one copy can be randomly chosen in a single roll.
                .GroupBy(x => x.RollKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderBy(_ => random.Next()).First())
                .ToList();

            // CashOfferPercent applies to offers that can actually participate in the cash/barter roll.
            // Do not count fixed-cash rows or helper rows in the barter target, otherwise 85% cash can
            // still produce too many barter offers when the assort contains ammo-pack helpers or other
            // non-rollable rows.
            var rollablePaymentOfferCount = forcedBarterOfferIds.Count + eligibleRandomBarterCandidates.Count;
            var requestedBarterCount = (int)Math.Round(
                rollablePaymentOfferCount * (barterPercent / 100.0),
                MidpointRounding.AwayFromZero);

            // AlwaysBarter offers are guaranteed barter and still count against the target barter percent.
            // Example: 15 target barter offers and 2 AlwaysBarter rows means only 13 more are randomly selected.
            var targetBarterCount = Math.Clamp(requestedBarterCount, 0, rollablePaymentOfferCount);
            var randomBarterSlots = Math.Max(0, targetBarterCount - forcedBarterOfferIds.Count);

            var freshRandomBarterCandidates = eligibleRandomBarterCandidates
                .Where(x => !_lastRandomBarterRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            var repeatRandomBarterCandidates = eligibleRandomBarterCandidates
                .Where(x => _lastRandomBarterRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            // Full ordered candidate list. The generator consumes from this list until the requested
            // number of successful barter schemes is reached. If an early candidate fails, the next
            // candidate is tried instead of lowering the final barter count.
            var orderedRandomBarterCandidates = freshRandomBarterCandidates
                .Concat(repeatRandomBarterCandidates)
                .ToList();

            var randomlySelectedBarterCandidates = orderedRandomBarterCandidates
                .Take(randomBarterSlots)
                .ToList();

            var randomlySelectedBarterOfferIds = randomlySelectedBarterCandidates
                .Select(x => x.OfferId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            selectedBarterOfferIds = forcedBarterOfferIds
                .Concat(randomlySelectedBarterOfferIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            targetGeneratedBarterCount = targetBarterCount;
            orderedBarterCandidateOfferIds = forcedBarterOfferIds
                .Concat(orderedRandomBarterCandidates.Select(x => x.OfferId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var freshSelectedBarterCount = randomlySelectedBarterCandidates.Count(x => !_lastRandomBarterRollKeys.Contains(x.RollKey));
            ReplaceHashSetContents(_lastRandomBarterRollKeys, randomlySelectedBarterCandidates.Select(x => x.RollKey));

            var rollableCashCount = Math.Max(0, rollablePaymentOfferCount - selectedBarterOfferIds.Count);
            var fixedCashOnlyCount = Math.Max(0, configuredOffers.Count - rollablePaymentOfferCount);
            YATMLogger.Log($"[Pricing] Random payment split enabled: {rollableCashCount} cash offers / {selectedBarterOfferIds.Count} barter offers from {rollablePaymentOfferCount} rollable offers ({fixedCashOnlyCount} fixed cash/helper offers, {forcedBarterOfferIds.Count} forced barter).");
            YATMLogger.LogDebug($"[Pricing] Non-repeat barter selection: selected {randomlySelectedBarterCandidates.Count} random barter offers ({freshSelectedBarterCount} fresh, {randomlySelectedBarterCandidates.Count - freshSelectedBarterCount} reused). AlwaysBarter rows are forced and can repeat.");

            if (repeatRandomBarterCandidates.Count > 0 && randomBarterSlots > freshRandomBarterCandidates.Count)
            {
                YATMLogger.LogDebug($"[Pricing] Reused {randomBarterSlots - freshRandomBarterCandidates.Count} previous barter picks because there were not enough fresh eligible barter offers.");
            }

            if (forcedBarterOfferIds.Count > requestedBarterCount)
            {
                YATMLogger.Log($"[Pricing] Warning: AlwaysBarter rows ({forcedBarterOfferIds.Count}) exceed requested barter count ({requestedBarterCount}). All AlwaysBarter rows were kept as barter and no random barter offers were added.");
            }

            if (rollablePaymentOfferCount < requestedBarterCount)
            {
                YATMLogger.Log($"[Pricing] Warning: requested {requestedBarterCount} barter offers, but only {rollablePaymentOfferCount} offers can use manual or generated barter schemes.");
            }
        }
        else
        {
            _lastRandomBarterRollKeys.Clear();

            if (!CashOffersOnly)
            {
                selectedBarterOfferIds = configuredOffers
                    .Where(x => IsAlwaysBarter(x.PriceConfig)
                        && CanOfferBecomeBarter(x.PriceConfig, canGenerateBarters, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
                    .Select(x => GetMemberValue(x.Offer, "Id")?.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                targetGeneratedBarterCount = selectedBarterOfferIds.Count;
                orderedBarterCandidateOfferIds = selectedBarterOfferIds.ToList();
            }
        }

        if (selectedBarterOfferIds.Count > 0 && canGenerateBarters)
        {
            if (targetGeneratedBarterCount <= 0)
            {
                targetGeneratedBarterCount = selectedBarterOfferIds.Count;
            }

            if (orderedBarterCandidateOfferIds.Count == 0)
            {
                orderedBarterCandidateOfferIds = selectedBarterOfferIds.ToList();
            }

            var configuredOffersByOfferId = configuredOffers
                .Select(x => new
                {
                    OfferId = GetMemberValue(x.Offer, "Id")?.ToString(),
                    x.Offer,
                    x.PriceConfig
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
                .GroupBy(x => x.OfferId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var orderedSelectedPriceConfigs = orderedBarterCandidateOfferIds
                .Where(configuredOffersByOfferId.ContainsKey)
                .Select(x => configuredOffersByOfferId[x].PriceConfig)
                .ToList();

            var generationResult = generatedOfferService.ApplyAutoGeneratedBartersToSelectedConfigWithResult(
                config,
                assort,
                orderedSelectedPriceConfigs,
                targetGeneratedBarterCount);

            if (generationResult.Changed)
            {
                config.SaveGeneratedBarters();
            }

            selectedBarterOfferIds = configuredOffersByOfferId
                .Where(x => generationResult.SuccessfulOfferIds.Contains(x.Key)
                    || (!string.IsNullOrWhiteSpace(x.Value.PriceConfig.TplId)
                        && generationResult.SuccessfulOfferIds.Contains(x.Value.PriceConfig.TplId)))
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var attemptedRandomRollKeys = configuredOffersByOfferId
                .Where(x => generationResult.AttemptedOfferIds.Contains(x.Key)
                    || (!string.IsNullOrWhiteSpace(x.Value.PriceConfig.TplId)
                        && generationResult.AttemptedOfferIds.Contains(x.Value.PriceConfig.TplId)))
                .Where(x => !IsAlwaysBarter(x.Value.PriceConfig))
                .Select(x => GetPaymentRollKey(x.Value.Offer, x.Value.PriceConfig))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (attemptedRandomRollKeys.Count > 0)
            {
                ReplaceHashSetContents(_lastRandomBarterRollKeys, attemptedRandomRollKeys);
            }

            if (generationResult.SuccessfulCount < targetGeneratedBarterCount)
            {
                YATMLogger.Log($"[Pricing] Generated barter retry filled {generationResult.SuccessfulCount}/{targetGeneratedBarterCount} barter slot(s). {targetGeneratedBarterCount - generationResult.SuccessfulCount} slot(s) stayed cash because no more candidates could generate valid barter schemes.");
            }
            else if (generationResult.SkippedCount > 0)
            {
                YATMLogger.Log($"[Pricing] Generated barter retry replaced {generationResult.SkippedCount} skipped candidate(s) and still filled {generationResult.SuccessfulCount}/{targetGeneratedBarterCount} barter slot(s).");
            }
        }

        foreach (var configuredOffer in configuredOffers)
        {
            var offerId = GetMemberValue(configuredOffer.Offer, "Id")?.ToString();
            if (string.IsNullOrWhiteSpace(offerId))
            {
                continue;
            }

            var useBarter = randomizeCashBarter && selectedBarterOfferIds.Contains(offerId);
            var appliedAmmoPackBarter = ApplyPaymentToOffer(
                assort,
                configuredOffer.Offer,
                offerId,
                configuredOffer.PriceConfig,
                CashOffersOnly,
                randomizeCashBarter,
                useBarter,
                out var appliedAmmoPackOfferId);

            if (appliedAmmoPackBarter && !string.IsNullOrWhiteSpace(appliedAmmoPackOfferId))
            {
                paymentRollResult.AmmoPackBarterOffersById[appliedAmmoPackOfferId] =
                    BuildAmmoPackBarterOfferLimitsData(assort, configuredOffer.Offer, appliedAmmoPackOfferId, configuredOffer.PriceConfig);
            }

            var appliedBarterOfferId = !string.IsNullOrWhiteSpace(appliedAmmoPackOfferId)
                ? appliedAmmoPackOfferId
                : offerId;

            if (!string.IsNullOrWhiteSpace(appliedBarterOfferId)
                && OfferUsesNonCurrencyPayment(assort, appliedBarterOfferId))
            {
                paymentRollResult.BarterOfferIds.Add(appliedBarterOfferId);
            }
        }

        EnsureAmmoPackBarterOffersSellPacks(assort, paymentRollResult);

        paymentRollResult.MarkCompleted();

        if (randomizeCashBarter)
        {
            var appliedCashCount = Math.Max(0, configuredOffers.Count - paymentRollResult.BarterOfferIds.Count);
            YATMLogger.Log($"[Pricing] Applied payment split: {appliedCashCount} cash offers / {paymentRollResult.BarterOfferIds.Count} barter offers ({selectedBarterOfferIds.Count} selected before generation).");
        }

        YATMLogger.LogDebug($"[{rollReason}] Payment roll completed before stock roll. Barter offers: {paymentRollResult.BarterOfferIds.Count}. Ammo pack barter offers: {paymentRollResult.AmmoPackBarterOffersById.Count}.");
        return paymentRollResult;
    }

    private static string GetPaymentRollKey(object offer, PriceConfigItem priceConfig)
    {
        // Use tpl as the roll identity so duplicate offers for the same item do not get picked again.
        // Fall back to offer id for rows with missing tpl.
        if (!string.IsNullOrWhiteSpace(priceConfig.TplId))
        {
            return priceConfig.TplId;
        }

        return GetMemberValue(offer, "Id")?.ToString() ?? string.Empty;
    }

    private static string GetStockRollKey(object offer, string? currentTpl)
    {
        // Use current tpl as the stock identity so the same visible item is avoided next restock.
        // Fall back to offer id for weird rows with missing tpl.
        if (!string.IsNullOrWhiteSpace(currentTpl))
        {
            return currentTpl;
        }

        return GetMemberValue(offer, "Id")?.ToString() ?? string.Empty;
    }

    private static void ReplaceHashSetContents(HashSet<string> target, IEnumerable<string> values)
    {
        target.Clear();

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }
    }

    private static bool DoesConfigMatchOffer(object item, PriceConfigItem priceConfig)
    {
        var itemId = GetMemberValue(item, "Id")?.ToString();

        if (!string.IsNullOrWhiteSpace(priceConfig.OfferId))
        {
            return itemId == priceConfig.OfferId;
        }

        var tpl = YATMConfig.GetTemplateId(item);
        return !string.IsNullOrEmpty(tpl) && tpl == priceConfig.TplId;
    }

    private static void EnsureAmmoPackBarterOffersSellPacks(
        TraderAssort assort,
        PaymentRollResult paymentRollResult)
    {
        if (paymentRollResult.AmmoPackBarterOffersById.Count == 0)
        {
            return;
        }

        foreach (var entry in paymentRollResult.AmmoPackBarterOffersById)
        {
            var offerId = entry.Key;
            var priceConfig = entry.Value.PriceConfig;
            var packTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId");

            if (string.IsNullOrWhiteSpace(packTpl))
            {
                continue;
            }

            var offer = FindRootOfferById(assort, offerId);
            if (offer == null)
            {
                YATMLogger.LogDebug($"[Pricing] Warning: ammo barter pack verification could not find offer {offerId} for {priceConfig.ItemName ?? "Unknown ammo"}.");
                continue;
            }

            var currentTpl = YATMConfig.GetTemplateId(offer);
            if (!string.Equals(currentTpl, packTpl, StringComparison.OrdinalIgnoreCase))
            {
                YATMLogger.Log($"[Pricing] Warning: ammo barter offer {priceConfig.ItemName ?? offerId} was selected as barter but still pointed at {currentTpl ?? "null"}. Forcing pack tpl {packTpl}.");
                SetOfferTemplate(offer, packTpl);
            }

            var verifiedTpl = YATMConfig.GetTemplateId(offer);
            if (string.Equals(verifiedTpl, packTpl, StringComparison.OrdinalIgnoreCase))
            {
                YATMLogger.LogRealDebug($"[Pricing] Ammo barter verified as pack: {priceConfig.ItemName ?? offerId} | Offer {offerId} | _tpl = {packTpl}");
            }
            else
            {
                YATMLogger.Log($"[Pricing] Warning: ammo barter offer {priceConfig.ItemName ?? offerId} could not be verified as pack after write. Expected {packTpl}, read {verifiedTpl ?? "null"}.");
            }
        }
    }

    private static bool ApplyPaymentToOffer(
        TraderAssort assort,
        object looseOffer,
        string looseOfferId,
        PriceConfigItem priceConfig,
        bool CashOffersOnly,
        bool randomizeCashBarter,
        bool useBarter,
        out string? appliedAmmoPackOfferId)
    {
        appliedAmmoPackOfferId = null;

        var shouldUseBarter = false;

        if (randomizeCashBarter)
        {
            shouldUseBarter = useBarter && HasUsableBarterScheme(priceConfig);
        }
        else
        {
            shouldUseBarter = !CashOffersOnly
                && HasUsableBarterScheme(priceConfig)
                && (IsAlwaysBarter(priceConfig) || !priceConfig.CashOnly);
        }

        if (shouldUseBarter)
        {
            if (!assort.BarterScheme.TryGetValue(looseOfferId, out var looseBarterSchemeList))
            {
                YATMLogger.LogDebug($"[Pricing] Offer {looseOfferId} has no barter_scheme entry.");
                return false;
            }

            ApplyBarterPaymentToOffer(looseOffer, looseBarterSchemeList, priceConfig);

            if (IsAmmoPackBarterConfig(priceConfig))
            {
                appliedAmmoPackOfferId = looseOfferId;
                return true;
            }

            return false;
        }

        if (!assort.BarterScheme.TryGetValue(looseOfferId, out var existingSchemeList))
        {
            YATMLogger.LogDebug($"[Pricing] Offer {looseOfferId} has no barter_scheme entry.");
            return false;
        }

        ApplyCashPaymentToOffer(looseOffer, existingSchemeList, priceConfig);

        if (randomizeCashBarter)
        {
            YATMLogger.LogRealDebug($"[Pricing] Random cash offer: {priceConfig.ItemName} = {priceConfig.Price} {priceConfig.Currency}");
        }
        else
        {
            YATMLogger.LogRealDebug($"[Pricing] Cash override: {priceConfig.ItemName} = {priceConfig.Price} {priceConfig.Currency}");
        }

        return false;
    }

    private static void RemoveStandaloneAmmoPackRootOffers(TraderAssort assort, YATMConfig config)
    {
        var looseOfferIds = config.Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
            .Select(x => x.OfferId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ammoPackTplIds = config.Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.AmmoBarterPackTplId))
            .Select(x => x.AmmoBarterPackTplId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oldPackOfferIds = config.Prices
            .Where(x => !string.IsNullOrWhiteSpace(x.PackOfferId))
            .Select(x => x.PackOfferId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ammoPackTplIds.Count == 0 && oldPackOfferIds.Count == 0)
        {
            return;
        }

        var rootOfferIdsToRemove = assort.Items
            .Where(x => x.ParentId == "hideout")
            .Select(x => new
            {
                OfferId = GetMemberValue(x, "Id")?.ToString(),
                Tpl = YATMConfig.GetTemplateId(x)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.OfferId))
            .Where(x => !looseOfferIds.Contains(x.OfferId!))
            .Where(x => oldPackOfferIds.Contains(x.OfferId!)
                || (!string.IsNullOrWhiteSpace(x.Tpl) && ammoPackTplIds.Contains(x.Tpl!)))
            .Select(x => x.OfferId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var offerId in rootOfferIdsToRemove)
        {
            RemoveOfferAndChildren(assort, offerId);
        }

        if (rootOfferIdsToRemove.Count > 0)
        {
            YATMLogger.LogDebug($"[Pricing] Removed {rootOfferIdsToRemove.Count} standalone ammo-pack helper offer(s); ammo barter now uses the loose offer ID and swaps _tpl in-place.");
        }
    }

    private static object? FindRootOfferById(TraderAssort assort, string offerId)
    {
        return assort.Items.FirstOrDefault(x =>
            x.ParentId == "hideout"
            && string.Equals(x.Id, offerId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindRootOfferIdByTpl(TraderAssort assort, string tpl, string excludedOfferId)
    {
        var offer = assort.Items.FirstOrDefault(x =>
            x.ParentId == "hideout"
            && !string.Equals(x.Id, excludedOfferId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(YATMConfig.GetTemplateId(x), tpl, StringComparison.OrdinalIgnoreCase));

        return offer?.Id;
    }

    private static void RemoveOfferAndChildren(TraderAssort assort, string rootOfferId)
    {
        if (string.IsNullOrWhiteSpace(rootOfferId))
        {
            return;
        }

        var idsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rootOfferId
        };

        var foundChild = true;
        while (foundChild)
        {
            foundChild = false;

            foreach (var item in assort.Items)
            {
                var itemId = GetMemberValue(item, "Id")?.ToString();
                var parentId = GetMemberValue(item, "ParentId")?.ToString();

                if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(parentId))
                {
                    continue;
                }

                if (idsToRemove.Contains(parentId) && idsToRemove.Add(itemId))
                {
                    foundChild = true;
                }
            }
        }

        var itemsToRemove = assort.Items
            .Where(x =>
            {
                var itemId = GetMemberValue(x, "Id")?.ToString();
                return !string.IsNullOrWhiteSpace(itemId) && idsToRemove.Contains(itemId);
            })
            .ToList();

        foreach (var item in itemsToRemove)
        {
            assort.Items.Remove(item);
        }

        foreach (var idToRemove in idsToRemove)
        {
            assort.BarterScheme.Remove(idToRemove);
            RemoveDictionaryEntryByStringKey(GetMemberValue(assort, "LoyalLevelItems"), idToRemove);
            RemoveDictionaryEntryByStringKey(GetMemberValue(assort, "loyal_level_items"), idToRemove);
        }
    }

    private static void RemoveDictionaryEntryByStringKey(object? dictionaryObject, string keyToRemove)
    {
        if (dictionaryObject is not IDictionary dictionary)
        {
            return;
        }

        object? actualKey = null;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key?.ToString()?.Equals(keyToRemove, StringComparison.OrdinalIgnoreCase) == true)
            {
                actualKey = entry.Key;
                break;
            }
        }

        if (actualKey != null)
        {
            dictionary.Remove(actualKey);
        }
    }

    private static bool IsAmmoPackBarterConfig(PriceConfigItem priceConfig)
    {
        return HasUsableBarterScheme(priceConfig)
            && !string.IsNullOrWhiteSpace(GetStringMember(priceConfig, "AmmoBarterPackTplId"));
    }

    private static void ApplyBarterPaymentToOffer(object offer, object existingSchemeList, PriceConfigItem priceConfig)
    {
        // Keep the same OfferId, but allow the sold tpl to change for ammo barter offers.
        // Normal barter offers sell priceConfig.TplId. Ammo barter offers sell the configured pack tpl.
        var ammoPackTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId");
        var targetTpl = !string.IsNullOrWhiteSpace(ammoPackTpl)
            ? ammoPackTpl
            : priceConfig.TplId;

        SetOfferTemplate(offer, targetTpl);

        if (!string.IsNullOrWhiteSpace(ammoPackTpl))
        {
            YATMLogger.LogRealDebug($"[Pricing] Ammo pack barter payment: {priceConfig.ItemName} | OfferId kept | _tpl = {targetTpl}");
        }
        else
        {
            YATMLogger.LogRealDebug($"[Pricing] Barter offer: {priceConfig.ItemName} | OfferId kept | _tpl = {targetTpl}");
        }

        ReplaceOfferPaymentScheme(existingSchemeList, priceConfig.BarterScheme!);

        // Final write after payment replacement so nothing in the payment pass can leave ammo
        // barters selling the loose round. Stock values are applied later by RollStock.
        if (!string.IsNullOrWhiteSpace(ammoPackTpl))
        {
            SetOfferTemplate(offer, targetTpl);
        }
    }

    private static AmmoPackBarterOfferLimitsData BuildAmmoPackBarterOfferLimitsData(
        TraderAssort assort,
        object looseOffer,
        string packOfferId,
        PriceConfigItem priceConfig)
    {
        var looseBuyRestrictionMax = ResolveLooseAmmoBuyRestrictionMax(looseOffer, priceConfig);
        var packSize = ResolveAmmoPackSize(assort, packOfferId, priceConfig);
        var packBuyRestrictionMax = GetAmmoPackBuyRestrictionMax(priceConfig, looseBuyRestrictionMax, packSize);

        return new AmmoPackBarterOfferLimitsData(
            priceConfig,
            looseBuyRestrictionMax,
            packSize,
            packBuyRestrictionMax);
    }

    private static int ResolveLooseAmmoBuyRestrictionMax(object looseOffer, PriceConfigItem priceConfig)
    {
        var looseUpd = GetMemberValue(looseOffer, "Upd");

        var looseBuyRestrictionMax = looseUpd != null
            ? GetIntMember(looseUpd, "BuyRestrictionMax", 0)
            : 0;

        if (looseBuyRestrictionMax > 0)
        {
            return looseBuyRestrictionMax;
        }

        // Fallbacks are intentionally config-driven. If a future PriceConfigItem grows
        // one of these fields, this method will use it without another model change here.
        foreach (var memberName in new[]
        {
            "LooseBuyRestrictionMax",
            "AmmoLooseBuyRestrictionMax",
            "BuyRestrictionMax"
        })
        {
            looseBuyRestrictionMax = GetIntMember(priceConfig, memberName, 0);
            if (looseBuyRestrictionMax > 0)
            {
                return looseBuyRestrictionMax;
            }
        }

        return 0;
    }

    private static int ResolveAmmoPackSize(
        TraderAssort assort,
        string packOfferId,
        PriceConfigItem priceConfig)
    {
        foreach (var memberName in new[]
        {
            "AmmoBarterPackSize",
            "AmmoPackSize",
            "PackSize"
        })
        {
            var configuredPackSize = GetIntMember(priceConfig, memberName, 0);
            if (configuredPackSize > 0)
            {
                return configuredPackSize;
            }
        }

        var packTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId") ?? string.Empty;
        var knownPackSize = GetKnownAmmoPackSize(packTpl);
        if (knownPackSize > 0)
        {
            return knownPackSize;
        }

        var packOffer = FindRootOfferById(assort, packOfferId);
        var packUpd = packOffer != null
            ? GetMemberValue(packOffer, "Upd")
            : null;

        var staticPackStackCount = packUpd != null
            ? GetIntMember(packUpd, "StackObjectsCount", 0)
            : 0;

        if (staticPackStackCount > 0)
        {
            return staticPackStackCount;
        }

        var inferredPackSize = InferAmmoPackSizeFromLooseAmmoName(priceConfig.ItemName);
        return inferredPackSize > 1 ? inferredPackSize : 0;
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

    private static int GetKnownAmmoPackSize(string packTpl)
    {
        if (string.IsNullOrWhiteSpace(packTpl))
        {
            return 0;
        }

        if (IsTplMatch(packTpl,
            // 12.7x55mm PS12B ammo pack
            "648983d6b5a2df1c815a04ec",
            // Tony custom 12.7x55mm PS12V ammo pack
            "6a4933e1fb1eff152bd649b9"))
        {
            return 10;
        }

        if (IsTplMatch(packTpl,
            // .366 TKM AP-M ammo pack
            "657023f81419851aef03e6f1",
            // .366 TKM EKO ammo pack
            "657024011419851aef03e6f4",
            // .366 TKM AP-S ammo pack
            "6a4933e1fb1eff152bd649bb",

            // 7.62x39mm MAI AP ammo pack
            "6489851fc827d4637f01791b",

            // 7.62x39mm BP gzh ammo pack
            "64acea16c4eda9354b0226b0",

            // 7.62x39mm PP gzh ammo pack
            "64ace9f9c4eda9354b0226aa",

            // 9x19mm RIP ammo pack
            "5c1127bdd174af44217ab8b9",

            // 7.62x54mm R LPS gzh ammo pack
            "65702577cfc010a0f5006a2c",

            // 7.62x54mm R BS gs ammo pack
            "648984b8d5b4df6140000a1a",

            // 7.62x54mm R SNB ammo pack
            "560d75f54bdc2da74d8b4573",

            // 9x39mm BP ammo pack
            "6489854673c462723909a14e",

            // 9x39mm SP-6 ammo pack
            "657025dabfc87b3a34093256",

            // 9x39mm SPP ammo pack
            "657025dfcfc010a0f5006a3b",

            // 9x39mm PAB-9 ammo pack
            "657025cfbfc87b3a34093253"))
        {
            return 20;
        }

        if (IsTplMatch(packTpl,
            // 12/70 AP-20 ammo pack
            "64898838d5b4df6140000a20",

            // 12/70 flechette ammo pack
            "65702474bfc87b3a34093226",

            // 12/70 7mm buckshot ammo pack
            "657024361419851aef03e6fa",
            // 12/70 handmade slug ammo pack
            "6a4933e1fb1eff152bd649b6"))
        {
            return 25;
        }

        if (IsTplMatch(packTpl,
            // Tony custom 5.45x39mm BP-M ammo pack
            "6a493587fb1eff152bd649be",
            // Tony custom 5.45x39mm BT-R ammo pack
            "6a493587fb1eff152bd649c0",
            // Tony custom 5.45x39mm PP-M ammo pack
            "6a493587fb1eff152bd649c2",
            // Tony custom 5.45x39mm PS-R ammo pack
            "6a493587fb1eff152bd649c5"))
        {
            return 30;
        }

        if (IsTplMatch(packTpl,
            // 9x19mm PBP ammo pack
            "648987d673c462723909a151",

            // 9x19mm AP 6.3 ammo pack
            "65702591c5d7d4cb4d07857c",
            // 9x18mm PM SP7 ammo pack
            "657026341419851aef03e730",
            // 9x19mm Pst ammo pack
            "657025a81419851aef03e724"))
        {
            return 50;
        }

        if (IsTplMatch(packTpl,
            // 5.45x39mm PPBS Igolnik ammo pack
            "657025ebc5d7d4cb4d078588",

            // 5.45x39mm BS gs ammo pack
            "57372b832459776701014e41",

            // 5.45x39mm BP gs ammo pack
            "5737292724597765e5728562",

            // 5.45x39mm BT gs ammo pack
            "57372c21245977670937c6c2",

            // 5.45x39mm PP gs ammo pack
            "57372d1b2459776862260581",
            // 5.45x39mm PS gs ammo pack
            "57372e73245977685d4159b4"))
        {
            return 120;
        }

        return 0;
    }

    private static void ApplyAmmoPackBarterOfferLimits(object offer, AmmoPackBarterOfferLimitsData limitsData)
    {
        var priceConfig = limitsData.PriceConfig;
        var upd = GetMemberValue(offer, "Upd");

        if (upd == null)
        {
            YATMLogger.LogDebug($"[Pricing] Ammo pack barter stock skipped because offer has no Upd data: {priceConfig.ItemName ?? "Unknown item"}");
            return;
        }

        // Number of packs the player can barter for this reset.
        var packBuyRestrictionMax = Math.Max(1, limitsData.PackBuyRestrictionMax);

        // Number of rounds inside one pack.
        // This must stay as the pack content count, not the trader buy limit.
        var packContentCount = limitsData.PackSize > 0
            ? limitsData.PackSize
            : Math.Max(1, GetIntMember(upd, "StackObjectsCount", 1));

        SetMemberValue(upd, "UnlimitedCount", true);
        SetMemberValue(upd, "StackObjectsCount", packContentCount);
        SetMemberValue(upd, "BuyRestrictionMax", packBuyRestrictionMax);
        SetMemberValue(upd, "BuyRestrictionCurrent", 0);

        YATMLogger.LogRealDebug(
            $"[Pricing] Ammo pack barter stock: {priceConfig.ItemName ?? "Unknown item"} | " +
            $"LooseBuyRestrictionMax {limitsData.LooseBuyRestrictionMax} | " +
            $"PackSize {limitsData.PackSize} | " +
            $"PackContentCount {packContentCount} | " +
            $"BuyRestrictionMax {packBuyRestrictionMax}");
    }

    private static int GetAmmoPackBuyRestrictionMax(
        PriceConfigItem priceConfig,
        int looseBuyRestrictionMax,
        int packSize)
    {
        if (looseBuyRestrictionMax > 0 && packSize > 0)
        {
            return Math.Max(
                1,
                (int)Math.Ceiling(looseBuyRestrictionMax / (double)packSize));
        }

        foreach (var memberName in new[]
        {
            "AmmoBarterPackBuyRestrictionMax",
            "AmmoPackBuyRestrictionMax",
            "PackBuyRestrictionMax"
        })
        {
            var configuredPackLimit = GetIntMember(priceConfig, memberName, 0);
            if (configuredPackLimit > 0)
            {
                YATMLogger.LogDebug(
                    $"[Pricing] Ammo pack BuyRestrictionMax used configured fallback for {priceConfig.ItemName ?? "Unknown item"}: {configuredPackLimit}");
                return configuredPackLimit;
            }
        }

        // This should now be rare. It only happens when both the loose ammo limit
        // and/or the pack size cannot be resolved from the live offer or known pack tpl table.
        YATMLogger.LogDebug(
            $"[Pricing] Ammo pack BuyRestrictionMax legacy fallback for {priceConfig.ItemName ?? "Unknown item"} | " +
            $"LooseBuyRestrictionMax {looseBuyRestrictionMax} | PackSize {packSize}");

        return GetLegacyAmmoPackBuyRestrictionMax(priceConfig);
    }

    private static int GetLegacyAmmoPackBuyRestrictionMax(PriceConfigItem priceConfig)
    {
        // Use the runtime-resolved ammo pack tpl. This is the tpl that the live assort
        // root item is changed to when ammo rolls barter.
        var packTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId") ?? string.Empty;

        if (IsHighTierAmmoPack(packTpl))
        {
            return 1;
        }

        if (IsMidTierAmmoPack(packTpl))
        {
            return 2;
        }

        // Anything not listed as high/mid is treated as low-tier ammo pack.
        return 3;
    }

    private static bool IsHighTierAmmoPack(string packTpl)
    {
        return IsTplMatch(packTpl,
            // .366 TKM AP-M ammo pack (20 pcs)
            "657023f81419851aef03e6f1",

            // 12/70 AP-20 ammo pack (25 pcs)
            "64898838d5b4df6140000a20",

            // 5.45x39mm PPBS Igolnik ammo pack (120 pcs)
            "657025ebc5d7d4cb4d078588",

            // 5.45x39mm BS gs ammo pack (120 pcs)
            "57372b832459776701014e41",

            // 7.62x39mm MAI AP ammo pack (20 pcs)
            "6489851fc827d4637f01791b",

            // 9x19mm PBP ammo pack (50 pcs)
            "648987d673c462723909a151",

            // 9x39mm BP ammo pack (20 pcs)
            "6489854673c462723909a14e",

            // 9x39mm SP-6 ammo pack (20 pcs)
            "657025dabfc87b3a34093256",

            // 12.7x55mm PS12B ammo pack (10 pcs)
            "648983d6b5a2df1c815a04ec"
        );
    }

    private static bool IsMidTierAmmoPack(string packTpl)
    {
        return IsTplMatch(packTpl,
            // 12/70 flechette ammo pack (25 pcs)
            "65702474bfc87b3a34093226",

            // 5.45x39mm BP gs ammo pack (120 pcs)
            "5737292724597765e5728562",

            // 5.45x39mm BT gs ammo pack
            "57372c21245977670937c6c2",

            // 5.45x39mm PP gs ammo pack (120 pcs)
            "57372d1b2459776862260581",

            // 7.62x39mm BP gzh ammo pack (20 pcs)
            "64acea16c4eda9354b0226b0",

            // 7.62x39mm PP gzh ammo pack (20 pcs)
            "64ace9f9c4eda9354b0226aa",

            // 9x19mm AP 6.3 ammo pack (50 pcs)
            "65702591c5d7d4cb4d07857c",

            // 9x19mm RIP ammo pack (20 pcs)
            "5c1127bdd174af44217ab8b9",

            // 9x39mm SPP ammo pack (20 pcs)
            "657025dfcfc010a0f5006a3b",

            // 9x39mm PAB-9 ammo pack (20 pcs)
            "657025cfbfc87b3a34093253"
        );
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

    private static void ApplyCashPaymentToOffer(object offer, object existingSchemeList, PriceConfigItem priceConfig)
    {
        // Cash offers always sell the normal configured item. For ammo this means the loose bullet tpl.
        // The OfferId stays the same; only _tpl, payment scheme, and stock values are changed.
        SetOfferTemplate(offer, priceConfig.TplId);

        var currencyTpl = YATMConfig.CurrencyToTemplate(priceConfig.Currency);

        ReplaceOfferPaymentScheme(existingSchemeList, new List<List<PaymentConfigItem>>
        {
            new()
            {
                new PaymentConfigItem
                {
                    TplId = currencyTpl,
                    ItemName = priceConfig.Currency.ToUpperInvariant(),
                    Count = priceConfig.Price
                }
            }
        });

        // Final write in the payment pass: cash ammo must always go back to loose ammo tpl.
        SetOfferTemplate(offer, priceConfig.TplId);

        if (!string.IsNullOrWhiteSpace(GetStringMember(priceConfig, "AmmoBarterPackTplId")))
        {
            YATMLogger.LogRealDebug($"[Pricing] Ammo cash offer reset: {priceConfig.ItemName} | _tpl = {priceConfig.TplId}");
        }
    }

    private static void SetOfferTemplate(object offer, string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }
        SetMemberValue(offer, "_tpl", templateId);
        SetMemberValue(offer, "Template", templateId);
        SetMemberValue(offer, "Tpl", templateId);
        SetMemberValue(offer, "TemplateId", templateId);

        var rawTpl = GetMemberValue(offer, "_tpl")?.ToString();
        var resolvedTpl = YATMConfig.GetTemplateId(offer);
        if (!string.Equals(rawTpl, templateId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedTpl, templateId, StringComparison.OrdinalIgnoreCase))
        {
            var offerId = GetMemberValue(offer, "Id")?.ToString() ?? "unknown offer";
            YATMLogger.LogDebug($"[Pricing] Warning: attempted to set assort _tpl for {offerId} to {templateId}, but readback returned _tpl={rawTpl ?? "null"}, resolved={resolvedTpl ?? "null"}.");
        }
    }

    private static void SetExtensionDataValue(object target, string key, object? value)
    {
        var type = target.GetType();

        var extensionMember = type.GetProperty(
                "ExtensionData",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? (MemberInfo?)type.GetField(
                "ExtensionData",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (extensionMember == null)
        {
            return;
        }

        object? extensionData = extensionMember switch
        {
            PropertyInfo prop => prop.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => null
        };

        if (extensionData == null)
        {
            // Most SPT models use Dictionary<string, object> for ExtensionData.
            extensionData = new Dictionary<string, object?>();

            try
            {
                switch (extensionMember)
                {
                    case PropertyInfo prop when prop.CanWrite:
                        prop.SetValue(target, extensionData);
                        break;
                    case FieldInfo field:
                        field.SetValue(target, extensionData);
                        break;
                }
            }
            catch
            {
                return;
            }
        }

        if (extensionData is IDictionary dictionary)
        {
            dictionary[key] = value;
        }
    }

    private static string? GetStringMember(object target, string memberName)
    {
        return GetMemberValue(target, memberName)?.ToString();
    }

    private static int GetIntMember(object target, string memberName, int defaultValue)
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

        if (int.TryParse(value.ToString(), out var parsedInt))
        {
            return parsedInt;
        }

        return defaultValue;
    }

    private static bool CanOfferBecomeBarter(
        PriceConfigItem priceConfig,
        bool canGenerateBarters,
        HashSet<string> pairedAmmoPackOfferIds,
        HashSet<string> pairedAmmoPackTplIds)
    {
        if (priceConfig == null || string.IsNullOrWhiteSpace(priceConfig.TplId))
        {
            return false;
        }

        if (YATMConfig.IsCurrencyTemplate(priceConfig.TplId))
        {
            return false;
        }

        if (IsPairedAmmoPackHelperConfig(priceConfig, pairedAmmoPackOfferIds, pairedAmmoPackTplIds))
        {
            return false;
        }

        return HasUsableBarterScheme(priceConfig) || canGenerateBarters;
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

    private static bool IsAlwaysBarter(PriceConfigItem priceConfig)
    {
        return priceConfig.AlwaysBarter;
    }

    private static bool IsAlwaysInStock(PriceConfigItem priceConfig)
    {
        return priceConfig.AlwaysInStock;
    }

    private static bool HasUsableBarterScheme(PriceConfigItem priceConfig)
    {
        if (priceConfig.BarterScheme == null || priceConfig.BarterScheme.Count == 0)
        {
            return false;
        }

        foreach (var paymentOption in priceConfig.BarterScheme)
        {
            if (paymentOption == null || paymentOption.Count == 0)
            {
                continue;
            }

            foreach (var paymentConfig in paymentOption)
            {
                if (paymentConfig == null || string.IsNullOrWhiteSpace(paymentConfig.TplId))
                {
                    continue;
                }

                if (!YATMConfig.IsCurrencyTemplate(paymentConfig.TplId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ReplaceOfferPaymentScheme(object existingSchemeListObject, List<List<PaymentConfigItem>> newScheme)
    {
        if (existingSchemeListObject is not IList existingSchemeList)
        {
            throw new InvalidOperationException("Trader barter scheme list is not IList-compatible.");
        }

        var paymentComponentType = FindExistingPaymentComponentType(existingSchemeList);
        if (paymentComponentType == null)
        {
            throw new InvalidOperationException("Could not determine SPT barter payment component type.");
        }

        var paymentListType = typeof(List<>).MakeGenericType(paymentComponentType);

        existingSchemeList.Clear();

        foreach (var paymentOption in newScheme)
        {
            var newPaymentOptionList = (IList)Activator.CreateInstance(paymentListType)!;

            foreach (var paymentConfig in paymentOption)
            {
                var newPaymentComponent = Activator.CreateInstance(paymentComponentType)!;

                SetPaymentComponentValues(newPaymentComponent, paymentConfig.TplId, paymentConfig.Count);

                newPaymentOptionList.Add(newPaymentComponent);
            }

            existingSchemeList.Add(newPaymentOptionList);
        }
    }


    private static void SetPaymentComponentValues(object paymentComponent, string tpl, double? count)
    {
        SetMemberValue(paymentComponent, "_tpl", tpl);
        SetMemberValue(paymentComponent, "tpl", tpl);
        SetMemberValue(paymentComponent, "Template", tpl);
        SetMemberValue(paymentComponent, "Tpl", tpl);
        SetMemberValue(paymentComponent, "TemplateId", tpl);
        SetMemberValue(paymentComponent, "count", count);
        SetMemberValue(paymentComponent, "Count", count);
    }

    private static Type? FindExistingPaymentComponentType(IList existingSchemeList)
    {
        foreach (var paymentOption in existingSchemeList)
        {
            if (paymentOption is not IList paymentComponents)
            {
                continue;
            }

            if (paymentComponents.Count > 0 && paymentComponents[0] != null)
            {
                return paymentComponents[0]!.GetType();
            }
        }

        return null;
    }


    private static bool GetBoolSetting(object settings, string settingName, bool defaultValue)
    {
        var value = GetMemberValue(settings, settingName);
        if (value == null)
        {
            return defaultValue;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        if (bool.TryParse(value.ToString(), out var parsedBool))
        {
            return parsedBool;
        }

        if (int.TryParse(value.ToString(), out var parsedInt))
        {
            return parsedInt != 0;
        }

        return defaultValue;
    }

    private static double GetDoubleMember(object target, string memberName, double defaultValue)
    {
        var value = GetMemberValue(target, memberName);
        if (value == null)
        {
            return defaultValue;
        }

        if (value is double doubleValue)
        {
            return doubleValue;
        }

        if (value is float floatValue)
        {
            return floatValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return double.TryParse(value.ToString(), out var parsedDouble)
            ? parsedDouble
            : defaultValue;
    }

    private static int GetIntSetting(object settings, string settingName, int defaultValue)
    {
        var value = GetMemberValue(settings, settingName);
        if (value == null)
        {
            return defaultValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        if (int.TryParse(value.ToString(), out var parsedInt))
        {
            return parsedInt;
        }

        return defaultValue;
    }

    private static object? GetMemberValue(object target, string memberName)
    {
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

            if (extensionData is IDictionary genericDictionary)
            {
                foreach (DictionaryEntry entry in genericDictionary)
                {
                    if (entry.Key?.ToString()?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return entry.Value;
                    }
                }
            }
        }

        return null;
    }

    private static void SetMemberValue(object target, string memberName, object? value)
    {
        var type = target.GetType();

        var prop = type.GetProperty(
            memberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (prop != null && prop.CanWrite)
        {
            if (!TryConvertValueForMember(value, prop.PropertyType, out var convertedValue))
            {
                return;
            }

            try
            {
                prop.SetValue(target, convertedValue);
            }
            catch (Exception)
            {
                // If assignment fails silently skip - best effort (avoids throwing on incompatible runtime SPT types)
            }

            return;
        }

        var field = type.GetField(
            memberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field != null)
        {
            if (!TryConvertValueForMember(value, field.FieldType, out var convertedValue))
            {
                return;
            }

            try
            {
                field.SetValue(target, convertedValue);
            }
            catch (Exception)
            {
                // swallow - best effort
            }
        }
    }

    private static bool TryConvertValueForMember(object? value, Type memberType, out object? convertedValue)
    {
        var targetType = Nullable.GetUnderlyingType(memberType) ?? memberType;
        convertedValue = null;

        if (value == null)
        {
            return !targetType.IsValueType || Nullable.GetUnderlyingType(memberType) != null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            convertedValue = value;
            return true;
        }

        if (targetType == typeof(string))
        {
            convertedValue = value.ToString();
            return true;
        }

        if (value is string stringValue && IsMongoIdLikeType(targetType))
        {
            if (TryCreateMongoIdLikeValue(targetType, stringValue, out convertedValue))
            {
                return true;
            }

            convertedValue = null;
            return false;
        }

        try
        {
            convertedValue = Convert.ChangeType(value, targetType);
            return true;
        }
        catch
        {
            convertedValue = null;
            return false;
        }
    }

    private static bool IsMongoIdLikeType(Type type)
    {
        return type.Name.Equals("MongoId", StringComparison.OrdinalIgnoreCase)
            || type.FullName?.EndsWith(".MongoId", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryCreateMongoIdLikeValue(Type targetType, string value, out object? convertedValue)
    {
        convertedValue = null;

        var ctor = targetType.GetConstructor(new[] { typeof(string) });
        if (ctor != null)
        {
            try
            {
                convertedValue = ctor.Invoke(new object[] { value });
                return true;
            }
            catch
            {
                // try other conversion paths below
            }
        }

        foreach (var methodName in new[] { "Parse", "FromString" })
        {
            var method = targetType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (method == null)
            {
                continue;
            }

            try
            {
                convertedValue = method.Invoke(null, new object[] { value });
                return convertedValue != null;
            }
            catch
            {
                // try other conversion paths below
            }
        }

        var implicitOrExplicitOperator = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x =>
                (x.Name == "op_Implicit" || x.Name == "op_Explicit")
                && x.ReturnType == targetType
                && x.GetParameters().Length == 1
                && x.GetParameters()[0].ParameterType == typeof(string));

        if (implicitOrExplicitOperator != null)
        {
            try
            {
                convertedValue = implicitOrExplicitOperator.Invoke(null, new object[] { value });
                return convertedValue != null;
            }
            catch
            {
                // try Activator below
            }
        }

        try
        {
            convertedValue = Activator.CreateInstance(targetType, new object[] { value });
            return convertedValue != null;
        }
        catch
        {
            convertedValue = null;
            return false;
        }
    }

}
