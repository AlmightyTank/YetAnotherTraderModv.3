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
using System.Text.Json;
using YetAnotherTraderMod.config;
using YetAnotherTraderMod.src.GeneratedOffers;
using YetAnotherTraderMod.src.Services;
using YetAnotherTraderMod.src.Services.Runtime;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src;

[Injectable]
public sealed class YATMTraderRuntimeService(
    ModHelper modHelper,
    ImageRouter imageRouter,
    ConfigServer configServer,
    DatabaseServer databaseServer,
    AddCustomTraderHelper addCustomTraderHelper,
    YATMUnlockService yatmUnlockService,
    YATMTraderOfferFeedService yatmTraderOfferFeedService,
    YATMGeneratedOfferService generatedOfferService,
    YATMTraderAssortRuntimeRollService assortRuntimeRollService,
    WTTServerCommonLib.WTTServerCommonLib wttCommon)
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
    private static bool _traderAddedToDb;
    private static bool _questsLoaded;

    // Simple restock pipeline:
    // 1) OnLoad and OnUpdate both start from a fresh clean db/CustomTrader/Tony/assort.json read.
    // 2) Offer IDs are never changed.
    // 3) Payment roll finishes first. This is the only place that decides cash/barter.
    //    Ammo keeps the loose OfferId. Cash sells loose ammo; barter swaps _tpl to the pack.
    // 4) Stock roll starts only after paymentRollResult.Completed is true.
    // 5) Stock roll only changes stock values and ammo pack limits. It never decides payment state.
    // 6) Offer IDs are never changed. Standalone ammo-pack helper offers are removed; the loose offer swaps in-place.
    // 7) If PreventBarterOffersOutOfStock is true, the stock roll excludes offers that became barter.
    // 8) RerollAssortOnRestock controls only the OnUpdate/restock reroll side; startup still rolls normally.
    // 9) The first IOnUpdate call after startup is ignored so the update hook cannot immediately reroll after OnLoad.

    public async Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _pathToMod = pathToMod;

        YATMLogger.Init(pathToMod);
        YATMLogger.Log("Mod OnLoad started.");

        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/CustomTrader/Tony/base.json");
        var assort = modHelper.GetJsonDataFromFile<TraderAssort>(pathToMod, "db/CustomTrader/Tony/assort.json");
        var traderImagePath = Path.Combine(pathToMod, "db/CustomTrader/Tony/Tony.jpg");

        // Load Tony's own feed files, then auto-discover addon feed files before the runtime assort is built.
        // Addons can call YATMCommonLib.CustomTraderOfferServiceExtended before this runtime runs,
        // or simply ship files under db/YATM/CustomTraderOffers and db/YATM/CustomWeaponPresets.
        await yatmTraderOfferFeedService.CreateCustomTraderOffers(Assembly.GetExecutingAssembly());
        await yatmTraderOfferFeedService.CreateAddonTonyTraderOffers(Assembly.GetExecutingAssembly());
        await yatmTraderOfferFeedService.CreateAddonCustomWeaponPresets(Assembly.GetExecutingAssembly());

        // Tony/addons use one offer-feed schema: db/CustomTraderOffers/*.json/jsonc for Tony,
        // or db/YATM/CustomTraderOffers/*.json/jsonc for addons.
        // Shape: { TonyTraderId: { items, barter_scheme, loyal_level_items, yatm_settings } }.
        // Optional config/manual_offers.jsonc rows are loaded by YATMConfig as manual overrides only.
        var generatedOfferDefinitions = yatmTraderOfferFeedService
            .GetRegisteredTonyTraderOffers()
            .ToList();

        var config = new YATMConfig(pathToMod, databaseServer);
        config.LoadOrGenerateSettingsOnly(traderBase);

        var generatedAssort = await generatedOfferService.BuildOrLoadGeneratedAssort(
            Assembly.GetExecutingAssembly(),
            generatedOfferDefinitions,
            config.Settings);

        generatedOfferService.MergeGeneratedAssortIntoTonyAssort(assort, generatedAssort);

        config.LoadOrGeneratePricesOnly(assort);
        generatedOfferService.MergeGeneratedPriceConfigs(config);
        config.StripManualBarterSchemesForAutoGeneratedMode();
        // Generated barter recipes are intentionally NOT built here.
        // Runtime order is: roll selected barter offers first, generate schemes only for those selected offers,
        // then run the stock roll. This keeps generated barter output small and prevents unused barter recipes.

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
            YATMLogger.LogDebug($"  MarketRepriceCashOffers: {config.Settings.MarketRepriceCashOffers}");
            YATMLogger.LogDebug($"  MarketCashPriceBlendMode: {config.Settings.MarketCashPriceBlendMode}");
            YATMLogger.LogDebug($"  MarketWeaponCashPricePercent: {config.Settings.MarketWeaponCashPricePercent}%");
            YATMLogger.LogDebug($"  MarketArmorCashPricePercent: {config.Settings.MarketArmorCashPricePercent}%");
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
            // Quest-gated unlock mode.
            // Tony's Fence intro quest already awards a TraderUnlock reward for Tony,
            // so do NOT run the old level-based unlock timer here.
            // MinLevel still controls Tony LL1 once the quest reward unlocks him.
            YATMUnlockService.EnableLevelLock = false;
            YATMUnlockService.ForceUnlock = false;
            YATMUnlockService.MinLevelRequired = config.Settings.MinLevel;
            YATMLogger.Log($"Quest-gated unlock active. Tony stays locked until Fence quest TraderUnlock reward. LL1 MinLevel: {config.Settings.MinLevel}");
        }
        else
        {
            YATMUnlockService.EnableLevelLock = false;
            YATMUnlockService.ForceUnlock = true;
            await yatmUnlockService.OnLoad();
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

        assortRuntimeRollService.ApplyRuntimeAssortRolls(assort, config, "Startup");
        assortRuntimeRollService.ApplyPriceMultiplierToMoneyComponents(assort, config);

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
        _traderAddedToDb = true;

        // WTT quest-assort import must happen only after Tony exists in the live trader DB.
        // This is intentionally inside the trader runtime instead of a separate IOnLoad so the order is guaranteed.
        await LoadQuestsAfterTraderExists(Assembly.GetExecutingAssembly(), pathToMod);

        // Ammo pack barter uses the same OfferId as the loose ammo offer, so questassort does not need patching.

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

        return;
    }

    public async Task<bool> OnRestockUpdate(long timeSinceLastRun)
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
                return true;
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
                return true;
            }

            if (!_rerollAssortOnRestock)
            {
                return true;
            }

            // IOnUpdate can run often. Polling every few seconds is enough because
            // trader restocks are minute/hour-scale events, not frame-sensitive work.
            if (now - _lastRestockPollUnix < RestockPollThrottleSeconds)
            {
                return true;
            }

            _lastRestockPollUnix = now;

            var tables = databaseServer.GetTables();
            if (!tables.Traders.TryGetValue(_runtimeTraderId, out var traderData) || traderData.Base == null)
            {
                return true;
            }

            var currentNextResupply = traderData.Base.NextResupply;
            if (currentNextResupply <= 0)
            {
                return true;
            }

            if (_lastSeenNextResupply == 0)
            {
                _lastSeenNextResupply = currentNextResupply;
                return true;
            }

            // SPT advances NextResupply when the trader refreshes. When we see it move
            // to a new future timestamp, rebuild Tony's assort from the clean JSON and
            // reroll both payment type and out-of-stock state.
            if (currentNextResupply != _lastSeenNextResupply && currentNextResupply > now)
            {
                _lastSeenNextResupply = currentNextResupply;
                await RerollTraderAssortFromDisk(traderData);
            }

            return true;
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[Restock] Failed to reroll trader assort: {ex.Message}");
            return false;
        }
    }

    private Task RerollTraderAssortFromDisk(Trader traderData)
    {
        var cleanBase = modHelper.GetJsonDataFromFile<TraderBase>(_pathToMod, "db/CustomTrader/Tony/base.json");
        var cleanAssort = modHelper.GetJsonDataFromFile<TraderAssort>(_pathToMod, "db/CustomTrader/Tony/assort.json");

        if (string.IsNullOrWhiteSpace(cleanBase.Id))
        {
            cleanBase.Id = _runtimeTraderId;
        }

        var generatedAssort = generatedOfferService.LoadGeneratedAssortFromCache(
            Assembly.GetExecutingAssembly());

        generatedOfferService.MergeGeneratedAssortIntoTonyAssort(cleanAssort, generatedAssort);

        var config = new YATMConfig(_pathToMod, databaseServer);
        config.LoadOrGenerate(cleanBase, cleanAssort);

        generatedOfferService.LoadGeneratedPriceConfigsFromCache(Assembly.GetExecutingAssembly());
        generatedOfferService.MergeGeneratedPriceConfigs(config);
        config.StripManualBarterSchemesForAutoGeneratedMode();
        // Generated barter recipes are intentionally NOT loaded/generated before the restock payment roll.
        // The roll service picks barter offers first, generates schemes for only those offers, then rolls stock.

        YATMLogger.IsDebugEnabled = config.Settings.DebugLogging;

        assortRuntimeRollService.ApplyRuntimeAssortRolls(cleanAssort, config, "Restock");
        assortRuntimeRollService.ApplyPriceMultiplierToMoneyComponents(cleanAssort, config);

        ReplaceLiveAssortInPlace(traderData, cleanAssort);

        // Ammo pack barter uses the same OfferId as the loose ammo offer, so questassort does not need patching.

        YATMLogger.Log("[Restock] Rerolled Tony payment split, stock availability, generated addon offers, and ammo pack in-place swaps.");
        return Task.CompletedTask;
    }



    private void ReplaceLiveAssortInPlace(Trader traderData, TraderAssort replacementAssort)
    {
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

    private static string? GetStringMember(object target, string memberName)
    {
        return GetMemberValue(target, memberName)?.ToString();
    }

    private static object? GetMemberValue(object target, string memberName)
    {
        var type = target.GetType();

        var prop = type.GetProperty(memberName);
        if (prop != null && prop.CanRead)
        {
            return prop.GetValue(target);
        }

        var field = type.GetField(memberName);
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

    private static void SetExtensionDataValue(object target, string key, object? value)
    {
        var extensionData = GetMemberValue(target, "ExtensionData");
        if (extensionData is IDictionary<string, object?> stringObjectDictionary)
        {
            stringObjectDictionary[key] = value;
            return;
        }

        if (extensionData is IDictionary dictionary)
        {
            dictionary[key] = value;
        }
    }

    private static bool TryConvertValueForMember(object? value, Type memberType, out object? convertedValue)
    {
        convertedValue = null;

        if (value == null)
        {
            return false;
        }

        var targetType = Nullable.GetUnderlyingType(memberType) ?? memberType;

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

        if (targetType == typeof(int) && int.TryParse(value.ToString(), out var intValue))
        {
            convertedValue = intValue;
            return true;
        }

        if (targetType == typeof(double) && double.TryParse(value.ToString(), out var doubleValue))
        {
            convertedValue = doubleValue;
            return true;
        }

        if (targetType == typeof(bool) && bool.TryParse(value.ToString(), out var boolValue))
        {
            convertedValue = boolValue;
            return true;
        }

        if (targetType.IsEnum)
        {
            try
            {
                convertedValue = Enum.Parse(targetType, value.ToString() ?? string.Empty, ignoreCase: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var valueText = value.ToString() ?? string.Empty;
        if (IsMongoIdLikeType(targetType) && TryCreateMongoIdLikeValue(targetType, valueText, out var mongoIdLikeValue))
        {
            convertedValue = mongoIdLikeValue;
            return true;
        }

        try
        {
            convertedValue = Convert.ChangeType(value, targetType);
            return true;
        }
        catch
        {
            // try operator/constructor below
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

    private static bool IsMongoIdLikeType(Type type)
    {
        return type.Name.Contains("MongoId", StringComparison.OrdinalIgnoreCase)
            || type.FullName?.Contains("MongoId", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryCreateMongoIdLikeValue(Type targetType, string value, out object? convertedValue)
    {
        convertedValue = null;

        try
        {
            convertedValue = Activator.CreateInstance(targetType, value);
            return convertedValue != null;
        }
        catch
        {
            // try static parse/from methods below
        }

        foreach (var methodName in new[] { "Parse", "FromString", "op_Implicit", "op_Explicit" })
        {
            var method = targetType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x =>
                    x.Name == methodName
                    && x.ReturnType == targetType
                    && x.GetParameters().Length == 1
                    && x.GetParameters()[0].ParameterType == typeof(string));

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
                // try next method
            }
        }

        return false;
    }


    private async Task LoadQuestsAfterTraderExists(Assembly assembly, string modPath)
    {
        if (_questsLoaded)
        {
            YATMLogger.LogDebug("[QuestImport] Quests already imported. Skipping duplicate load.");
            return;
        }

        _questsLoaded = true;

        try
        {
            YATMLogger.Log("[QuestImport] Starting quest load after Tony trader was added to the DB.");

            if (CustomQuestsEnabled(modPath) && CustomSideQuestEnabled(modPath))
            {
                YATMLogger.LogRealDebug("[QuestImport] Loading custom main quests, side quests, and quest zones...");

                await wttCommon.CustomQuestService.CreateCustomQuests(assembly, Path.Join("db", "CustomQuests", "MainQuests"));
                await wttCommon.CustomQuestService.CreateCustomQuests(assembly, Path.Join("db", "CustomQuests", "SideQuests"));
                await wttCommon.CustomQuestZoneService.CreateCustomQuestZones(assembly);
            }
            else if (CustomQuestsEnabled(modPath) && !CustomSideQuestEnabled(modPath))
            {
                YATMLogger.Log("[QuestImport] Custom quests are enabled in settings.json, but custom side quests are disabled.");

                await wttCommon.CustomQuestService.CreateCustomQuests(assembly, Path.Join("db", "CustomQuests", "MainQuests"));
                await wttCommon.CustomQuestZoneService.CreateCustomQuestZones(assembly);
            }
            else
            {
                YATMLogger.Log("[QuestImport] Custom quests are disabled in settings.json. Skipping quests and quest zones.");
            }

            YATMLogger.Log("[QuestImport] Finished quest load after Tony trader DB insert.");
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[QuestImport] Failed to import custom quests after Tony trader DB insert: {ex.Message}");
            throw;
        }
    }

    private static bool CustomQuestsEnabled(string modPath)
    {
        return ReadBoolSetting(modPath, "EnableCustomQuests", true);
    }

    private static bool CustomSideQuestEnabled(string modPath)
    {
        return ReadBoolSetting(modPath, "EnableCustomSideQuests", true);
    }

    private static bool ReadBoolSetting(string modPath, string propertyName, bool defaultValue)
    {
        var settingsPath = Path.Combine(modPath, "config", "settings.json");

        if (!File.Exists(settingsPath))
        {
            return defaultValue;
        }

        try
        {
            var json = File.ReadAllText(settingsPath);

            using var doc = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            if (doc.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[QuestImport] Failed to read {propertyName} from settings.json. Defaulting to {defaultValue}. Error: {ex.Message}");
        }

        return defaultValue;
    }


}