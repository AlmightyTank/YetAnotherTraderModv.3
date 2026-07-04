using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YetAnotherTraderMod.config;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src;

[Injectable]
public sealed class YATMTraderRuntimeService(
    ModHelper modHelper,
    ImageRouter imageRouter,
    ConfigServer configServer,
    DatabaseServer databaseServer,
    AddCustomTraderHelper addCustomTraderHelper,
    YATMUnlockService yatmUnlockService)
{
    private const string DefaultTonyTraderId = "66a0f6b2c4d8e90123456789";
    private const int RestockPollThrottleSeconds = 5;

    private readonly TraderConfig _traderConfig = configServer.GetConfig<TraderConfig>();
    private readonly RagfairConfig _ragfairConfig = configServer.GetConfig<RagfairConfig>();
    private static readonly Random _random = new();

    private static string _pathToMod = string.Empty;
    private static string _runtimeTraderId = DefaultTonyTraderId;
    private static int? _lastSeenNextResupply;
    private static long _lastRestockPollUnix;
    private static bool _restockRerollReady;
    private static bool _skipFirstRestockUpdateRun = true;
    private static bool _rerollAssortOnRestock = true;
    private static bool _loggedUpdateHookActive;

    // Simple restock pipeline:
    // 1) OnLoad and OnUpdate both start from a fresh clean db/CustomTrader/Tony/assort.json read.
    // 2) Offer IDs are never changed.
    // 3) Payment roll finishes first. This is the only place that decides cash/barter.
    //    Ammo uses paired offers: loose ammo OfferId for cash, PackOfferId for barter.
    // 4) Stock roll starts only after paymentRollResult.Completed is true.
    // 5) Stock roll only changes stock values and ammo pack limits. It never decides payment state.
    // 6) Offer IDs are never changed. The losing paired ammo offer is removed from the clean assort.
    // 7) If PreventBarterOffersOutOfStock is true, the stock roll excludes offers that became barter.
    // 8) RerollAssortOnRestock controls only the OnUpdate/restock reroll side; startup still rolls normally.
    // 9) The first IOnUpdate call after startup is ignored so the update hook cannot immediately reroll after OnLoad.

    // These remember the previous randomized roll so the next restock can avoid
    // picking the same items again when there are enough alternatives.
    private static readonly HashSet<string> _lastRandomBarterRollKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _lastOutOfStockRollKeys = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RollCandidate(string OfferId, string RollKey);

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

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _pathToMod = pathToMod;

        YATMLogger.Init(pathToMod);
        YATMLogger.Log("Mod OnLoad started.");

        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/CustomTrader/Tony/base.json");
        var assort = modHelper.GetJsonDataFromFile<TraderAssort>(pathToMod, "db/CustomTrader/Tony/assort.json");
        var traderImagePath = Path.Combine(pathToMod, "db/CustomTrader/Tony/Tony.jpg");

        var config = new YATMConfig(pathToMod, databaseServer);
        config.LoadOrGenerate(traderBase, assort);

        // Controls only the restock/update reroll side.
        // Startup still rolls normally so the trader starts with randomized stock/payments.
        _rerollAssortOnRestock = GetBoolSetting(config.Settings, "RerollAssortOnRestock", true);

        YATMLogger.IsDebugEnabled = config.Settings.DebugLogging;
        if (YATMLogger.IsDebugEnabled)
        {
            YATMLogger.LogDebug("Debug Mode Enabled. Config Loaded.");
            YATMLogger.LogDebug($"  MinLevel: {config.Settings.MinLevel}");
            YATMLogger.LogDebug($"  UnlockedByDefault: {config.Settings.UnlockedByDefault}");
            YATMLogger.LogDebug($"  UnlimitedStock: {config.Settings.UnlimitedStock}");
            YATMLogger.LogDebug($"  RandomizeStock: {config.Settings.RandomizeStockAvailable} (Chance: {config.Settings.OutOfStockChance}%)");
            YATMLogger.LogDebug($"  PriceMultiplier: {config.Settings.PriceMultiplier}");
            YATMLogger.LogDebug($"  RandomizeCashBarterOffers: {GetBoolSetting(config.Settings, "RandomizeCashBarterOffers", true)}");
            YATMLogger.LogDebug($"  CashOfferPercent: {GetIntSetting(config.Settings, "CashOfferPercent", 85)}");
            YATMLogger.LogDebug($"  ForceCashOnly: {config.Settings.CashOffersOnly}");
            YATMLogger.LogDebug($"  RerollAssortOnRestock: {_rerollAssortOnRestock}");
            YATMLogger.LogDebug($"  PreventBarterOffersOutOfStock: {GetBoolSetting(config.Settings, "PreventBarterOffersOutOfStock", true)}");
        }

        traderBase.UnlockedByDefault = config.Settings.UnlockedByDefault;

        if (traderBase.LoyaltyLevels.Count > 0)
        {
            traderBase.LoyaltyLevels[0].MinLevel = config.Settings.MinLevel;
        }

        if (traderBase.LoyaltyLevels != null)
        {
            var baseInsuranceCoef = config.Settings.InsurancePriceCoef; // Example: 95
            const int insuranceStepDownPerLoyaltyLevel = 10;

            for (var i = 0; i < traderBase.LoyaltyLevels.Count; i++)
            {
                var level = traderBase.LoyaltyLevels[i];
                var loyaltyLevel = i + 1;

                try
                {
                    var prop = level.GetType().GetProperty("InsurancePriceCoefficient");
                    if (prop != null && prop.CanWrite)
                    {
                        var coefForLevel = Math.Max(
                            0,
                            baseInsuranceCoef - (i * insuranceStepDownPerLoyaltyLevel));

                        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                        object val = Convert.ChangeType(coefForLevel, targetType);

                        prop.SetValue(level, val);

                        YATMLogger.LogDebug(
                            $"[Insurance] Set Loyalty Level {loyaltyLevel} " +
                            $"(MinLevel {level.MinLevel}) Coef to: {val}");
                    }
                    else
                    {
                        YATMLogger.LogDebug(
                            $"[Insurance] Warning: InsurancePriceCoefficient property not found on Loyalty Level {loyaltyLevel}.");
                    }
                }
                catch (Exception ex)
                {
                    YATMLogger.Log($"[Insurance] Error setting coef for Loyalty Level {loyaltyLevel}: {ex.Message}");
                }
            }
        }

        if (traderBase.Insurance != null)
        {
            traderBase.Insurance.ExtensionData ??= new Dictionary<string, object>();
            traderBase.Insurance.ExtensionData["insurance_price_coef"] = config.Settings.InsurancePriceCoef;
        }

        if (traderBase.Repair != null)
        {
            traderBase.Repair.Quality = config.Settings.RepairQuality;
        }

        if (!config.Settings.UnlockedByDefault)
        {
            YATMUnlockService.EnableLevelLock = true;
            YATMUnlockService.MinLevelRequired = config.Settings.MinLevel;
            yatmUnlockService.OnLoad();
            YATMLogger.Log($"Level-based unlock enabled. Required level: {config.Settings.MinLevel}");
        }
        else
        {
            YATMUnlockService.EnableLevelLock = false;
            YATMUnlockService.ForceUnlock = true;
            YATMLogger.Log("Trader unlocked by default (ForceUnlock active).");
        }

        if (string.IsNullOrEmpty(traderBase.Id))
        {
            YATMLogger.Log("CRITICAL ERROR: traderBase.Id is null or empty! Hardcoding ID to ensure stability.");
            traderBase.Id = DefaultTonyTraderId;
        }

        _runtimeTraderId = traderBase.Id;

        traderBase.ItemsBuy ??= new() { Category = [], IdList = [] };
        traderBase.ItemsBuyProhibited ??= new() { Category = [], IdList = [] };
        traderBase.ItemsSell ??= [];

        ApplyRuntimeAssortRolls(assort, config, "Startup");
        ApplyPriceMultiplierToMoneyComponents(assort, config);

        var avatarRoute = traderBase.Avatar ?? string.Empty;
        avatarRoute = avatarRoute.Replace(".png", "").Replace(".jpg", "").Replace(".jpeg", "");
        imageRouter.AddRoute(avatarRoute, traderImagePath);

        if (config.Settings.AddTraderToFleaMarket)
        {
            _ragfairConfig.Traders.TryAdd(traderBase.Id, true);
        }
        else
        {
            _ragfairConfig.Traders.Remove(traderBase.Id);
        }

        int restockTime = _random.Next(config.Settings.TraderRefreshMin, config.Settings.TraderRefreshMax);

        YATMLogger.Log($"Setting trader restock timer to {restockTime} seconds.");
        addCustomTraderHelper.SetTraderUpdateTime(
            _traderConfig,
            traderBase,
            restockTime,
            restockTime);

        traderBase.NextResupply = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + restockTime);

        addCustomTraderHelper.AddTraderToDb(traderBase, assort);

        // Paired ammo offers need the same quest unlock state as their loose ammo pair.
        // Do this against the live server DB trader base after Tony has been added.
        PatchServerQuestAssortForPairedAmmoOffers(config);

        _lastSeenNextResupply = traderBase.NextResupply;
        _lastRestockPollUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _skipFirstRestockUpdateRun = true;
        _restockRerollReady = true;

        if (config.Settings.DebugLogging)
        {
            YATMLogger.Log("Trader initialized. Debug Enabled.");
        }

        var localeFirstName = traderBase.Nickname ?? traderBase.Name ?? "Tony";
        var localeDescription = "An ex-BEAR operator and former enforcer for Russian organized crime. After Tarkov collapsed, Volkov turned old connections into a quiet business, supplying weapons, armor, and contraband to smugglers, mercenaries, and criminals. He respects usefulness, hates weakness, and only opens doors for those who earn his trust.";
        addCustomTraderHelper.AddTraderToLocales(traderBase, localeFirstName, localeDescription);

        return Task.CompletedTask;
    }

    public Task<bool> OnRestockUpdate(long timeSinceLastRun)
    {
        try
        {
            if (!_loggedUpdateHookActive)
            {
                _loggedUpdateHookActive = true;
                YATMLogger.Log("[Restock] IOnUpdate hook is active.");
            }

            if (!_restockRerollReady || string.IsNullOrWhiteSpace(_pathToMod))
            {
                return Task.FromResult(true);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Do not let the first IOnUpdate call after OnLoad do any restock work.
            // This primes the watcher against the live server DB state after AddTraderToDb
            // and prevents an immediate startup-adjacent reroll.
            if (_skipFirstRestockUpdateRun)
            {
                _skipFirstRestockUpdateRun = false;
                _lastRestockPollUnix = now;

                var startupTables = databaseServer.GetTables();
                if (startupTables.Traders.TryGetValue(_runtimeTraderId, out var startupTraderData)
                    && startupTraderData.Base != null
                    && startupTraderData.Base.NextResupply > 0)
                {
                    _lastSeenNextResupply = startupTraderData.Base.NextResupply;
                }

                YATMLogger.LogDebug("[Restock] First IOnUpdate run ignored after startup; restock watcher is now primed.");
                return Task.FromResult(true);
            }

            if (!_rerollAssortOnRestock)
            {
                return Task.FromResult(true);
            }

            // IOnUpdate can run often. Polling every few seconds is enough because
            // trader restocks are minute/hour-scale events, not frame-sensitive work.
            if (now - _lastRestockPollUnix < RestockPollThrottleSeconds)
            {
                return Task.FromResult(true);
            }

            _lastRestockPollUnix = now;

            var tables = databaseServer.GetTables();
            if (!tables.Traders.TryGetValue(_runtimeTraderId, out var traderData) || traderData.Base == null)
            {
                return Task.FromResult(true);
            }

            var currentNextResupply = traderData.Base.NextResupply;
            if (currentNextResupply <= 0)
            {
                return Task.FromResult(true);
            }

            if (_lastSeenNextResupply == 0)
            {
                _lastSeenNextResupply = currentNextResupply;
                return Task.FromResult(true);
            }

            // SPT advances NextResupply when the trader refreshes. When we see it move
            // to a new future timestamp, rebuild Tony's assort from the clean JSON and
            // reroll both payment type and out-of-stock state.
            if (currentNextResupply != _lastSeenNextResupply && currentNextResupply > now)
            {
                _lastSeenNextResupply = currentNextResupply;
                RerollTraderAssortFromDisk(traderData);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[Restock] Failed to reroll trader assort: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private void RerollTraderAssortFromDisk(Trader traderData)
    {
        var cleanBase = modHelper.GetJsonDataFromFile<TraderBase>(_pathToMod, "db/CustomTrader/Tony/base.json");
        var cleanAssort = modHelper.GetJsonDataFromFile<TraderAssort>(_pathToMod, "db/CustomTrader/Tony/assort.json");

        if (string.IsNullOrWhiteSpace(cleanBase.Id))
        {
            cleanBase.Id = _runtimeTraderId;
        }

        var config = new YATMConfig(_pathToMod, databaseServer);
        config.LoadOrGenerate(cleanBase, cleanAssort);

        YATMLogger.IsDebugEnabled = config.Settings.DebugLogging;

        ApplyRuntimeAssortRolls(cleanAssort, config, "Restock");
        ApplyPriceMultiplierToMoneyComponents(cleanAssort, config);

        ReplaceLiveAssortInPlace(traderData, cleanAssort);

        // Paired ammo offers can be removed/re-added by each restock roll.
        // After the live assort has been changed, re-apply questassort unlocks in the server DB
        // so whichever paired offer is currently visible has the same quest unlock as its loose ammo pair.
        PatchServerQuestAssortForPairedAmmoOffers(config);

        YATMLogger.Log("[Restock] Rerolled Tony payment split, stock availability, and paired ammo questassort unlocks.");
    }

    private static void ReplaceLiveAssortInPlace(Trader traderData, TraderAssort replacementAssort)
    {
        // Keep this boring on purpose:
        // SPT/client routes can keep a reference to the original Assort object.
        // Assigning traderData.Assort = replacementAssort can leave those routes reading
        // the old items/barter_scheme, which is what makes old barters stay in the barter tab.
        // We do not re-key offers. We only replace the contents of the live collections.
        if (traderData.Assort == null)
        {
            traderData.Assort = replacementAssort;
            SyncAssortExtensionData(replacementAssort);
            return;
        }

        var liveAssort = traderData.Assort;

        liveAssort.Items.Clear();
        foreach (var item in replacementAssort.Items)
        {
            liveAssort.Items.Add(item);
        }

        liveAssort.BarterScheme.Clear();
        foreach (var scheme in replacementAssort.BarterScheme)
        {
            liveAssort.BarterScheme[scheme.Key] = scheme.Value;
        }

        CopyDictionaryMemberIfPresent(liveAssort, replacementAssort, "LoyalLevelItems");
        CopyDictionaryMemberIfPresent(liveAssort, replacementAssort, "loyal_level_items");

        SyncAssortExtensionData(liveAssort);
        traderData.Assort = liveAssort;
    }

    private static void CopyDictionaryMemberIfPresent(object target, object source, string memberName)
    {
        var targetDictionary = GetMemberValue(target, memberName) as IDictionary;
        var sourceDictionary = GetMemberValue(source, memberName) as IDictionary;

        if (targetDictionary == null || sourceDictionary == null)
        {
            return;
        }

        targetDictionary.Clear();

        foreach (DictionaryEntry entry in sourceDictionary)
        {
            targetDictionary[entry.Key] = entry.Value;
        }
    }

    private static void SyncAssortExtensionData(TraderAssort assort)
    {
        SetExtensionDataValue(assort, "items", assort.Items);
        SetExtensionDataValue(assort, "Items", assort.Items);
        SetExtensionDataValue(assort, "barter_scheme", assort.BarterScheme);
        SetExtensionDataValue(assort, "BarterScheme", assort.BarterScheme);

        var loyalLevelItems = GetMemberValue(assort, "LoyalLevelItems")
            ?? GetMemberValue(assort, "loyal_level_items");

        if (loyalLevelItems != null)
        {
            SetExtensionDataValue(assort, "loyal_level_items", loyalLevelItems);
            SetExtensionDataValue(assort, "LoyalLevelItems", loyalLevelItems);
        }
    }


    private void PatchServerQuestAssortForPairedAmmoOffers(YATMConfig config)
    {
        try
        {
            var tables = databaseServer.GetTables();
            if (!tables.Traders.TryGetValue(_runtimeTraderId, out var traderData))
            {
                YATMLogger.LogDebug("[QuestAssort] Tony trader was not found in server DB. Skipping paired ammo questassort patch.");
                return;
            }

            // In SPT 4 the questassort lives on the live Trader object itself,
            // not inside traderData.Base. Base is only the trader base data.
            var questAssort = GetMemberValue(traderData, "QuestAssort")
                ?? GetMemberValue(traderData, "questassort");

            if (questAssort == null)
            {
                YATMLogger.LogDebug("[QuestAssort] traderData.QuestAssort was not found. Skipping paired ammo questassort patch.");
                return;
            }

            // Read clean assort.json so PackOfferId can be inferred from AmmoBarterPackTplId
            // even if the PriceConfigItem class has not been updated with PackOfferId yet.
            var cleanAssort = modHelper.GetJsonDataFromFile<TraderAssort>(_pathToMod, "db/CustomTrader/Tony/assort.json");

            var pairs = BuildPairedAmmoOfferPairs(config, cleanAssort);
            if (pairs.Count == 0)
            {
                YATMLogger.LogDebug("[QuestAssort] No paired ammo offer IDs were found to patch.");
                return;
            }

            var addedCount = 0;
            foreach (var pair in pairs)
            {
                addedCount += CopyQuestAssortUnlock(questAssort, "Started", pair.LooseOfferId, pair.PackOfferId);
                addedCount += CopyQuestAssortUnlock(questAssort, "Success", pair.LooseOfferId, pair.PackOfferId);
                addedCount += CopyQuestAssortUnlock(questAssort, "Fail", pair.LooseOfferId, pair.PackOfferId);

                // Raw JSON / ExtensionData fallback names.
                addedCount += CopyQuestAssortUnlock(questAssort, "started", pair.LooseOfferId, pair.PackOfferId);
                addedCount += CopyQuestAssortUnlock(questAssort, "success", pair.LooseOfferId, pair.PackOfferId);
                addedCount += CopyQuestAssortUnlock(questAssort, "fail", pair.LooseOfferId, pair.PackOfferId);
            }

            YATMLogger.Log($"[QuestAssort] Patched paired ammo pack unlocks in server DB: {addedCount} entries added for {pairs.Count} paired ammo offers.");
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[QuestAssort] Failed to patch paired ammo questassort unlocks: {ex.Message}");
        }
    }

    private sealed record PairedAmmoOfferIds(string LooseOfferId, string PackOfferId);

    private static List<PairedAmmoOfferIds> BuildPairedAmmoOfferPairs(YATMConfig config, TraderAssort cleanAssort)
    {
        var pairs = new List<PairedAmmoOfferIds>();
        var seenPackOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var priceConfig in config.Prices)
        {
            var looseOfferId = priceConfig.OfferId;
            var ammoPackTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId");

            if (string.IsNullOrWhiteSpace(looseOfferId) || string.IsNullOrWhiteSpace(ammoPackTpl))
            {
                continue;
            }

            var packOfferId = GetStringMember(priceConfig, "PackOfferId");
            if (string.IsNullOrWhiteSpace(packOfferId))
            {
                packOfferId = FindRootOfferIdByTpl(cleanAssort, ammoPackTpl, looseOfferId);
            }

            if (string.IsNullOrWhiteSpace(packOfferId))
            {
                YATMLogger.LogDebug($"[QuestAssort] No PackOfferId/static pack offer found for {priceConfig.ItemName} ({looseOfferId}).");
                continue;
            }

            if (!seenPackOfferIds.Add(packOfferId))
            {
                continue;
            }

            pairs.Add(new PairedAmmoOfferIds(looseOfferId, packOfferId));
        }

        return pairs;
    }

    private static int CopyQuestAssortUnlock(object questAssort, string bucketName, string looseOfferId, string packOfferId)
    {
        var bucket = GetQuestAssortBucketDictionary(questAssort, bucketName);
        if (bucket == null)
        {
            return 0;
        }

        object? looseKey = null;
        object? packKey = null;
        object? unlockValue = null;

        foreach (DictionaryEntry entry in bucket)
        {
            var keyText = entry.Key?.ToString();
            if (keyText == null)
            {
                continue;
            }

            if (keyText.Equals(packOfferId, StringComparison.OrdinalIgnoreCase))
            {
                packKey = entry.Key;
            }

            if (keyText.Equals(looseOfferId, StringComparison.OrdinalIgnoreCase))
            {
                looseKey = entry.Key;
                unlockValue = entry.Value;
            }
        }

        // Pack offer already has its own questassort entry.
        if (packKey != null)
        {
            return 0;
        }

        // Loose offer has no quest unlock in this bucket, so there is nothing to copy.
        if (looseKey == null)
        {
            return 0;
        }

        if (!TryConvertValueForMember(packOfferId, looseKey.GetType(), out var convertedPackKey) || convertedPackKey == null)
        {
            YATMLogger.LogDebug($"[QuestAssort] Could not convert PackOfferId {packOfferId} to questassort key type {looseKey.GetType().FullName}.");
            return 0;
        }

        bucket[convertedPackKey] = unlockValue;
        return 1;
    }

    private static IDictionary? GetQuestAssortBucketDictionary(object questAssort, string bucketName)
    {
        var directMember = GetMemberValue(questAssort, bucketName) as IDictionary;
        if (directMember != null)
        {
            return directMember;
        }

        if (questAssort is IDictionary questAssortDictionary)
        {
            foreach (DictionaryEntry entry in questAssortDictionary)
            {
                if (entry.Key?.ToString()?.Equals(bucketName, StringComparison.OrdinalIgnoreCase) == true
                    && entry.Value is IDictionary nestedDictionary)
                {
                    return nestedDictionary;
                }
            }
        }

        return null;
    }

    private void ApplyRuntimeAssortRolls(TraderAssort assort, YATMConfig config, string rollReason)
    {
        // HARD ORDER GUARANTEE:
        // 1) Start from the clean assort object that was just read from assort.json.
        // 2) Finish the full payment roll first. This is the only phase that decides
        //    cash vs barter and loose ammo tpl vs AmmoBarterPackTplId.
        // 3) Only after paymentRollResult.Completed is true, run the stock roll.
        //    Stock reads the payment result; it does not decide barter state.
        var paymentRollResult = RollPayments(assort, config, rollReason);

        if (!paymentRollResult.Completed)
        {
            throw new InvalidOperationException($"[{rollReason}] Payment roll did not complete. Stock roll was not started.");
        }

        RollStock(assort, config, rollReason, paymentRollResult);
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

    private void ApplyPriceMultiplierToMoneyComponents(TraderAssort assort, YATMConfig config)
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
                YATMLogger.LogDebug($"[Pricing] Multiple offers matched TplId {priceConfig.TplId}. Add OfferId to items.json for exact control.");
            }

            foreach (var offer in matchingOffers)
            {
                var offerId = GetMemberValue(offer, "Id")?.ToString();
                if (string.IsNullOrWhiteSpace(offerId))
                {
                    YATMLogger.LogDebug($"[Pricing] Matched offer for {priceConfig.ItemName} has no Id.");
                    continue;
                }

                // Avoid applying the same offer more than once if items.json has duplicate tpl matches.
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

        if (randomizeCashBarter)
        {
            var cashPercent = Math.Clamp(GetIntSetting(config.Settings, "CashOfferPercent", 85), 0, 100);
            var barterPercent = 100 - cashPercent;
            var requestedBarterCount = (int)Math.Round(
                configuredOffers.Count * (barterPercent / 100.0),
                MidpointRounding.AwayFromZero);

            var forcedBarterOfferIds = configuredOffers
                .Where(x => IsAlwaysBarter(x.PriceConfig) && HasUsableBarterScheme(x.PriceConfig))
                .Select(x => GetMemberValue(x.Offer, "Id")?.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var invalidAlwaysBarterCount = configuredOffers
                .Count(x => IsAlwaysBarter(x.PriceConfig) && !HasUsableBarterScheme(x.PriceConfig));

            if (invalidAlwaysBarterCount > 0)
            {
                YATMLogger.Log($"[Pricing] Warning: {invalidAlwaysBarterCount} AlwaysBarter rows have no usable barter scheme and cannot be forced to barter.");
            }

            var random = _random;
            var eligibleRandomBarterCandidates = configuredOffers
                .Where(x => HasUsableBarterScheme(x.PriceConfig))
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

            // AlwaysBarter offers are guaranteed barter and still count against the target barter percent.
            // Example: 15 target barter offers and 2 AlwaysBarter rows means only 13 more are randomly selected.
            var targetBarterCount = Math.Clamp(requestedBarterCount, 0, forcedBarterOfferIds.Count + eligibleRandomBarterCandidates.Count);
            var randomBarterSlots = Math.Max(0, targetBarterCount - forcedBarterOfferIds.Count);

            var freshRandomBarterCandidates = eligibleRandomBarterCandidates
                .Where(x => !_lastRandomBarterRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            var repeatRandomBarterCandidates = eligibleRandomBarterCandidates
                .Where(x => _lastRandomBarterRollKeys.Contains(x.RollKey))
                .OrderBy(_ => random.Next())
                .ToList();

            var randomlySelectedBarterCandidates = freshRandomBarterCandidates
                .Take(randomBarterSlots)
                .Concat(repeatRandomBarterCandidates.Take(Math.Max(0, randomBarterSlots - freshRandomBarterCandidates.Count)))
                .ToList();

            var randomlySelectedBarterOfferIds = randomlySelectedBarterCandidates
                .Select(x => x.OfferId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            selectedBarterOfferIds = forcedBarterOfferIds
                .Concat(randomlySelectedBarterOfferIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var freshSelectedBarterCount = randomlySelectedBarterCandidates.Count(x => !_lastRandomBarterRollKeys.Contains(x.RollKey));
            ReplaceHashSetContents(_lastRandomBarterRollKeys, randomlySelectedBarterCandidates.Select(x => x.RollKey));

            var targetCashCount = configuredOffers.Count - selectedBarterOfferIds.Count;
            YATMLogger.Log($"[Pricing] Random payment split enabled: {targetCashCount} cash offers / {selectedBarterOfferIds.Count} barter offers ({forcedBarterOfferIds.Count} forced barter).");
            YATMLogger.LogDebug($"[Pricing] Non-repeat barter selection: selected {randomlySelectedBarterCandidates.Count} random barter offers ({freshSelectedBarterCount} fresh, {randomlySelectedBarterCandidates.Count - freshSelectedBarterCount} reused). AlwaysBarter rows are forced and can repeat.");

            if (repeatRandomBarterCandidates.Count > 0 && randomBarterSlots > freshRandomBarterCandidates.Count)
            {
                YATMLogger.LogDebug($"[Pricing] Reused {randomBarterSlots - freshRandomBarterCandidates.Count} previous barter picks because there were not enough fresh eligible barter offers.");
            }

            if (forcedBarterOfferIds.Count > requestedBarterCount)
            {
                YATMLogger.Log($"[Pricing] Warning: AlwaysBarter rows ({forcedBarterOfferIds.Count}) exceed requested barter count ({requestedBarterCount}). All AlwaysBarter rows were kept as barter and no random barter offers were added.");
            }

            if ((forcedBarterOfferIds.Count + eligibleRandomBarterCandidates.Count) < requestedBarterCount)
            {
                YATMLogger.Log($"[Pricing] Warning: requested {requestedBarterCount} barter offers, but only {forcedBarterOfferIds.Count + eligibleRandomBarterCandidates.Count} offers have real barter schemes available.");
            }
        }
        else
        {
            _lastRandomBarterRollKeys.Clear();
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

        paymentRollResult.MarkCompleted();
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

        var ammoPackTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId");
        var packOfferId = GetStringMember(priceConfig, "PackOfferId");

        // PackOfferId is preferred, but the runtime can also infer the pair from the static
        // pack offer in assort.json. This keeps the feature working even if the config model
        // has not been updated with a PackOfferId property yet.
        if (string.IsNullOrWhiteSpace(packOfferId) && !string.IsNullOrWhiteSpace(ammoPackTpl))
        {
            packOfferId = FindRootOfferIdByTpl(assort, ammoPackTpl, looseOfferId);
        }

        var hasPairedAmmoPackOffer =
            !string.IsNullOrWhiteSpace(packOfferId)
            && !string.IsNullOrWhiteSpace(ammoPackTpl);

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
            if (hasPairedAmmoPackOffer)
            {
                if (ApplyPairedAmmoPackBarterOffer(assort, looseOfferId, packOfferId!, priceConfig))
                {
                    appliedAmmoPackOfferId = packOfferId;
                    return true;
                }

                // Safety fallback: if the pack offer is missing from assort.json, keep the loose offer
                // as a normal barter instead of crashing or leaving both offers half-mutated.
                YATMLogger.Log($"[Pricing] Warning: PackOfferId {packOfferId} for {priceConfig.ItemName} was not usable. Falling back to loose-ammo barter offer.");
            }

            if (!assort.BarterScheme.TryGetValue(looseOfferId, out var looseBarterSchemeList))
            {
                YATMLogger.LogDebug($"[Pricing] Offer {looseOfferId} has no barter_scheme entry.");
                return false;
            }

            ApplyBarterPaymentToOffer(looseOffer, looseBarterSchemeList, priceConfig);
            return IsAmmoPackBarterConfig(priceConfig);
        }

        if (hasPairedAmmoPackOffer)
        {
            // Cash wins: keep the loose ammo offer and remove the static pack offer.
            RemoveOfferAndChildren(assort, packOfferId!);
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

    private static bool ApplyPairedAmmoPackBarterOffer(
        TraderAssort assort,
        string looseOfferId,
        string packOfferId,
        PriceConfigItem priceConfig)
    {
        var packOffer = FindRootOfferById(assort, packOfferId);
        if (packOffer == null)
        {
            return false;
        }

        if (!assort.BarterScheme.TryGetValue(packOfferId, out var packBarterSchemeList))
        {
            YATMLogger.LogDebug($"[Pricing] Pack offer {packOfferId} has no barter_scheme entry.");
            return false;
        }

        // Barter wins: keep the static pack offer and remove the loose ammo offer.
        RemoveOfferAndChildren(assort, looseOfferId);

        var packTpl = GetStringMember(priceConfig, "AmmoBarterPackTplId");
        if (!string.IsNullOrWhiteSpace(packTpl))
        {
            // This is not the old same-offer swap. The pack offer is already a separate static offer.
            // This write only validates/normalizes the pack offer tpl in case assort.json was edited.
            SetOfferTemplate(packOffer, packTpl);
        }

        ReplaceOfferPaymentScheme(packBarterSchemeList, priceConfig.BarterScheme!);
        YATMLogger.LogRealDebug($"[Pricing] Paired ammo pack barter: {priceConfig.ItemName} | LooseOfferId {looseOfferId} removed | PackOfferId {packOfferId} kept.");

        return true;
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

        return staticPackStackCount > 0
            ? staticPackStackCount
            : 0;
    }

    private static int GetKnownAmmoPackSize(string packTpl)
    {
        if (string.IsNullOrWhiteSpace(packTpl))
        {
            return 0;
        }

        if (IsTplMatch(packTpl,
            // 12.7x55mm PS12B ammo pack
            "648983d6b5a2df1c815a04ec"))
        {
            return 10;
        }

        if (IsTplMatch(packTpl,
            // .366 TKM AP-M ammo pack
            "657023f81419851aef03e6f1",

            // 7.62x39mm MAI AP ammo pack
            "6489851fc827d4637f01791b",

            // 7.62x39mm BP gzh ammo pack
            "64acea16c4eda9354b0226b0",

            // 7.62x39mm PP gzh ammo pack
            "64ace9f9c4eda9354b0226aa",

            // 9x19mm RIP ammo pack
            "5c1127bdd174af44217ab8b9",

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
            "65702474bfc87b3a34093226"))
        {
            return 25;
        }

        if (IsTplMatch(packTpl,
            // 9x19mm PBP ammo pack
            "648987d673c462723909a151",

            // 9x19mm AP 6.3 ammo pack
            "65702591c5d7d4cb4d07857c"))
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
            "57372d1b2459776862260581"))
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
        // and/or the pack size cannot be resolved from the live offer, items.json,
        // known pack tpl table, or explicit config fallback.
        YATMLogger.LogDebug(
            $"[Pricing] Ammo pack BuyRestrictionMax legacy fallback for {priceConfig.ItemName ?? "Unknown item"} | " +
            $"LooseBuyRestrictionMax {looseBuyRestrictionMax} | PackSize {packSize}");

        return GetLegacyAmmoPackBuyRestrictionMax(priceConfig);
    }

    private static int GetLegacyAmmoPackBuyRestrictionMax(PriceConfigItem priceConfig)
    {
        // Use the actual ammo pack tpl from items.json.
        // This is the tpl that the live assort root item is changed to when ammo rolls barter.
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