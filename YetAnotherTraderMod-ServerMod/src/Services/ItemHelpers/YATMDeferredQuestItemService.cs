using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using YetAnotherTraderMod.src.Models;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.Services.ItemHelpers;

/// <summary>
/// Reads quest-assort and quest-reward extensions from WTT custom-item JSON files,
/// then applies them only after the final trader assort and custom quests exist.
///
/// This avoids the normal early-load failure where the item loader sees the JSON
/// before Tony's real assort has replaced the placeholder assort.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMDeferredQuestItemService(
    DatabaseServer databaseServer,
    YATMWeaponBuildService weaponBuildService)
{
    private const string TonyTraderId = "66a0f6b2c4d8e90123456789";
    private const string StartedBucket = "Started";
    private const string SuccessBucket = "Success";
    private const string FailBucket = "Fail";

    private const string RubTpl = "5449016a4bdc2d6f028b456f";
    private const string UsdTpl = "5696686a4bdc2da3298b456a";
    private const string EurTpl = "569668774bdc2da2298b4568";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Dictionary<string, string> TraderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tony"] = TonyTraderId,
        ["prapor"] = "54cb50c76803fa8b248b4571",
        ["therapist"] = "54cb57776803fa99248b456e",
        ["fence"] = "579dc571d53a0658a154fbec",
        ["skier"] = "58330581ace78e27b8b10cee",
        ["peacekeeper"] = "5935c25fb3acc3127c3d8cd9",
        ["mechanic"] = "5a7c2eca46aef81a7ca2145d",
        ["ragman"] = "5ac3b934156ae10c4430e83c",
        ["jaeger"] = "5c0647fdd443bc2504c2d371",
        ["ref"] = "6617beeaa9cfa777ca915b7c"
    };

    private readonly List<YATMItemModificationRequest> _requests = [];
    private readonly HashSet<string> _registeredRequestKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appliedQuestAssortKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appliedQuestRewardKeys = new(StringComparer.OrdinalIgnoreCase);

    public async Task RegisterFromItemFolders(Assembly assembly, IEnumerable<string> relativePaths)
    {
        var modPath = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve mod path.");

        var registered = 0;

        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(modPath, relativePath);
            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            var files = Directory
                .EnumerateFiles(fullPath, "*.json", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(fullPath, "*.jsonc", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var file in files)
            {
                registered += await RegisterFile(file);
            }
        }

        if (registered > 0)
        {
            YATMLogger.Log($"[DeferredQuestItems] Registered {registered} custom item request(s) with deferred quest data.");
        }
        else
        {
            YATMLogger.LogDebug("[DeferredQuestItems] No enabled quest-assort or quest-reward item extensions found.");
        }
    }

    public void ApplyDeferredQuestData(string reason, bool finalPass = false)
    {
        var assortApplied = 0;
        var rewardApplied = 0;
        var unresolved = 0;

        foreach (var request in _requests)
        {
            if (request.AddToQuestAssorts)
            {
                foreach (var config in request.QuestAssorts)
                {
                    var key = BuildQuestAssortKey(request, config);
                    if (_appliedQuestAssortKeys.Contains(key))
                    {
                        continue;
                    }

                    if (TryApplyQuestAssort(request, config))
                    {
                        _appliedQuestAssortKeys.Add(key);
                        assortApplied++;
                    }
                    else
                    {
                        unresolved++;
                        LogDeferredFailure(finalPass, request, $"quest assort quest={config.QuestId}, trader={config.TraderId}, assort={config.AssortId}");
                    }
                }
            }

            if (request.AddToQuestRewards)
            {
                foreach (var config in request.QuestRewards)
                {
                    var key = BuildQuestRewardKey(request, config);
                    if (_appliedQuestRewardKeys.Contains(key))
                    {
                        continue;
                    }

                    if (TryApplyQuestReward(request, config))
                    {
                        _appliedQuestRewardKeys.Add(key);
                        rewardApplied++;
                    }
                    else
                    {
                        unresolved++;
                        LogDeferredFailure(finalPass, request, $"quest reward quest={config.QuestId}, type={config.RewardType}, build={config.WeaponBuildId}, preset={config.PresetId}");
                    }
                }
            }
        }

        if (assortApplied > 0 || rewardApplied > 0)
        {
            YATMLogger.Log($"[{reason}] [DeferredQuestItems] Applied {assortApplied} quest assort(s) and {rewardApplied} quest reward(s).");
        }

        if (finalPass && unresolved > 0)
        {
            YATMLogger.Log($"[{reason}] [DeferredQuestItems] {unresolved} quest extension(s) could not be resolved. See the preceding warnings.");
        }
    }

    private async Task<int> RegisterFile(string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var requests = JsonSerializer.Deserialize<Dictionary<string, YATMItemModificationRequest>>(json, JsonOptions);
            if (requests == null || requests.Count == 0)
            {
                return 0;
            }

            var registered = 0;

            foreach (var (itemId, request) in requests)
            {
                if (request == null)
                {
                    continue;
                }

                request.ItemId = itemId;
                request.SourceFile = file;

                var hasQuestAssorts = request.AddToQuestAssorts && request.QuestAssorts.Count > 0;
                var hasQuestRewards = request.AddToQuestRewards && request.QuestRewards.Count > 0;
                if (!hasQuestAssorts && !hasQuestRewards)
                {
                    continue;
                }

                var requestKey = $"{Path.GetFullPath(file)}|{itemId}";
                if (!_registeredRequestKeys.Add(requestKey))
                {
                    continue;
                }

                _requests.Add(request);
                registered++;
            }

            return registered;
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[DeferredQuestItems] Failed to read '{file}': {ex.Message}");
            return 0;
        }
    }

    private bool TryApplyQuestAssort(YATMItemModificationRequest request, YATMQuestAssortConfig config)
    {
        if (!TryCreateMongoId(config.QuestId, out _))
        {
            return false;
        }

        var tables = databaseServer.GetTables();
        if (!tables.Templates.Quests.TryGetValue(config.QuestId, out var quest) || quest == null)
        {
            return false;
        }

        var traderId = ResolveTraderForQuestAssort(request, config);
        if (string.IsNullOrWhiteSpace(traderId)
            || !tables.Traders.TryGetValue(traderId, out var trader)
            || trader?.Assort == null
            || trader.QuestAssort == null)
        {
            return false;
        }

        var assortId = ResolveFinalAssortId(request, config, traderId, trader);
        if (string.IsNullOrWhiteSpace(assortId)
            || !TryCreateMongoId(assortId, out var assortMongoId)
            || !TryCreateMongoId(config.QuestId, out var questMongoId))
        {
            return false;
        }

        if (!HasValidTraderAssort(trader, assortMongoId))
        {
            return false;
        }

        var statusKey = ResolveQuestAssortBucketKey(trader, config.Status);
        if (string.IsNullOrWhiteSpace(statusKey)
            || !trader.QuestAssort.TryGetValue(statusKey, out var statusBucket)
            || statusBucket == null)
        {
            return false;
        }

        statusBucket[assortMongoId] = questMongoId;

        if (!statusBucket.TryGetValue(assortMongoId, out var storedQuestId)
            || !storedQuestId.Equals(questMongoId))
        {
            return false;
        }

        AddQuestAssortRewardDisplay(quest, traderId, trader, assortMongoId, config.Status);

        YATMLogger.LogDebug(
            $"[DeferredQuestItems] Mapped trader={traderId}, assort={assortId}, quest={config.QuestId}, status={statusKey}, item={request.ItemId}.");

        return true;
    }

    private bool TryApplyQuestReward(YATMItemModificationRequest request, YATMQuestRewardConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.QuestId)
            || !databaseServer.GetTables().Templates.Quests.TryGetValue(config.QuestId, out var quest)
            || quest == null)
        {
            return false;
        }

        var rewardBucket = NormalizeRewardBucket(config.Status);
        EnsureRewardBuckets(quest);

        var rewardType = config.RewardType?.Trim().ToLowerInvariant();
        switch (rewardType)
        {
            case "item":
                AddItemReward(quest, request.ItemId, Math.Max(1, config.Count), config.FindInRaid, config.IsHidden, rewardBucket);
                return true;

            case "ammo":
                AddItemReward(quest, request.ItemId, Math.Max(1, config.Count), config.FindInRaid, config.IsHidden, rewardBucket);
                return true;

            case "weapon":
                return AddWeaponBuildReward(
                    quest,
                    request,
                    config,
                    Math.Max(1, config.Count),
                    config.FindInRaid,
                    config.IsHidden,
                    rewardBucket);

            case "currency":
                AddItemReward(
                    quest,
                    NormalizeCurrencyTpl(config.CurrencyTpl ?? RubTpl),
                    Math.Max(1, config.Count),
                    false,
                    config.IsHidden,
                    rewardBucket);
                return true;

            case "weaponpreset":
                if (string.IsNullOrWhiteSpace(config.PresetId))
                {
                    return false;
                }

                return AddWeaponPresetReward(
                    quest,
                    request,
                    config.PresetId,
                    config.FindInRaid,
                    config.IsHidden,
                    rewardBucket);

            case "assortunlock":
            case "assortmentunlock":
                return AddExplicitAssortUnlockReward(quest, request, config, rewardBucket);

            default:
                YATMLogger.Log($"[DeferredQuestItems] Unknown rewardType '{config.RewardType}' for item {request.ItemId}.");
                return false;
        }
    }

    private string? ResolveTraderForQuestAssort(YATMItemModificationRequest request, YATMQuestAssortConfig config)
    {
        var tables = databaseServer.GetTables();
        var configuredTraderId = ResolveTraderId(config.TraderId);

        if (!string.IsNullOrWhiteSpace(configuredTraderId)
            && tables.Traders.TryGetValue(configuredTraderId, out var configuredTrader)
            && configuredTrader?.Assort != null)
        {
            if (string.IsNullOrWhiteSpace(config.AssortId)
                || TraderContainsAssort(configuredTrader, config.AssortId)
                || FindCandidateRootOffers(request, configuredTrader, configuredTraderId, config.AssortId).Count > 0)
            {
                return configuredTraderId;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.AssortId))
        {
            foreach (var (traderId, trader) in tables.Traders)
            {
                if (trader?.Assort != null && TraderContainsAssort(trader, config.AssortId))
                {
                    if (!string.IsNullOrWhiteSpace(configuredTraderId)
                        && !string.Equals(configuredTraderId, traderId, StringComparison.OrdinalIgnoreCase))
                    {
                        YATMLogger.LogDebug(
                            $"[DeferredQuestItems] Quest assort trader fallback: configured={configuredTraderId}, resolved={traderId}, assort={config.AssortId}.");
                    }

                    return traderId;
                }
            }
        }

        if (!request.AddToTraders)
        {
            return configuredTraderId;
        }

        foreach (var (traderKey, entries) in request.PresetTraders)
        {
            if (entries == null || entries.Count == 0)
            {
                continue;
            }

            var matches = entries.Any(x =>
                string.IsNullOrWhiteSpace(config.AssortId)
                || x.Key.Equals(config.AssortId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(config.PresetId)
                    && x.Value?.PresetId.Equals(config.PresetId, StringComparison.OrdinalIgnoreCase) == true));

            if (!matches)
            {
                continue;
            }

            var inferredTraderId = ResolveTraderId(traderKey);
            if (!string.IsNullOrWhiteSpace(inferredTraderId)
                && tables.Traders.ContainsKey(inferredTraderId))
            {
                if (!string.IsNullOrWhiteSpace(configuredTraderId)
                    && !string.Equals(configuredTraderId, inferredTraderId, StringComparison.OrdinalIgnoreCase))
                {
                    YATMLogger.LogDebug(
                        $"[DeferredQuestItems] Quest assort inferred trader from presetTraders: configured={configuredTraderId}, resolved={inferredTraderId}.");
                }

                return inferredTraderId;
            }
        }

        foreach (var (traderKey, entries) in request.WeaponBuildTraders)
        {
            if (entries == null || entries.Count == 0)
            {
                continue;
            }

            var matches = entries.Any(x =>
                string.IsNullOrWhiteSpace(config.AssortId)
                || x.Key.Equals(config.AssortId, StringComparison.OrdinalIgnoreCase));

            if (!matches)
            {
                continue;
            }

            var inferredTraderId = ResolveTraderId(traderKey);
            if (!string.IsNullOrWhiteSpace(inferredTraderId)
                && tables.Traders.ContainsKey(inferredTraderId))
            {
                if (!string.IsNullOrWhiteSpace(configuredTraderId)
                    && !string.Equals(configuredTraderId, inferredTraderId, StringComparison.OrdinalIgnoreCase))
                {
                    YATMLogger.LogDebug(
                        $"[DeferredQuestItems] Quest assort inferred trader from weaponBuildTraders: configured={configuredTraderId}, resolved={inferredTraderId}.");
                }

                return inferredTraderId;
            }
        }

        return configuredTraderId;
    }

    private string? ResolveFinalAssortId(
        YATMItemModificationRequest request,
        YATMQuestAssortConfig config,
        string traderId,
        Trader trader)
    {
        if (!string.IsNullOrWhiteSpace(config.AssortId) && TraderContainsAssort(trader, config.AssortId))
        {
            return config.AssortId;
        }

        var candidates = FindCandidateRootOffers(request, trader, traderId, config.AssortId);
        if (candidates.Count == 1)
        {
            var resolved = candidates[0].Id.ToString();
            if (!string.IsNullOrWhiteSpace(config.AssortId)
                && !resolved.Equals(config.AssortId, StringComparison.OrdinalIgnoreCase))
            {
                YATMLogger.LogDebug(
                    $"[DeferredQuestItems] Resolved source assort {config.AssortId} to final offer root {resolved} for item {request.ItemId}.");
            }

            return resolved;
        }

        if (candidates.Count > 1)
        {
            var matchingLoyalty = ResolveConfiguredLoyalty(request, traderId, config.AssortId);
            if (matchingLoyalty > 0)
            {
                var loyaltyMatches = candidates
                    .Where(x => trader.Assort!.LoyalLevelItems.TryGetValue(x.Id, out var loyalty) && loyalty == matchingLoyalty)
                    .ToList();

                if (loyaltyMatches.Count == 1)
                {
                    return loyaltyMatches[0].Id.ToString();
                }
            }
        }

        return null;
    }

    private List<Item> FindCandidateRootOffers(
        YATMItemModificationRequest request,
        Trader trader,
        string traderId,
        string? sourceAssortId)
    {
        if (trader.Assort?.Items == null)
        {
            return [];
        }

        var candidates = trader.Assort.Items
            .Where(x => string.Equals(x.ParentId?.ToString(), "hideout", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(x.Template.ToString(), request.ItemId, StringComparison.OrdinalIgnoreCase))
            .Where(x => HasValidTraderAssort(trader, x.Id))
            .ToList();

        if (!string.IsNullOrWhiteSpace(sourceAssortId))
        {
            var exact = candidates.FirstOrDefault(x => x.Id.ToString().Equals(sourceAssortId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return [exact];
            }
        }

        var configuredLoyalty = ResolveConfiguredLoyalty(request, traderId, sourceAssortId);
        if (configuredLoyalty > 0)
        {
            var filtered = candidates
                .Where(x => trader.Assort.LoyalLevelItems.TryGetValue(x.Id, out var loyalty) && loyalty == configuredLoyalty)
                .ToList();

            if (filtered.Count > 0)
            {
                return filtered;
            }
        }

        return candidates;
    }

    private int ResolveConfiguredLoyalty(YATMItemModificationRequest request, string traderId, string? sourceAssortId)
    {
        if (!request.AddToTraders)
        {
            return 0;
        }

        foreach (var (traderKey, entries) in request.PresetTraders)
        {
            if (!string.Equals(ResolveTraderId(traderKey), traderId, StringComparison.OrdinalIgnoreCase)
                || entries == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sourceAssortId)
                && entries.TryGetValue(sourceAssortId, out var exactEntry)
                && exactEntry?.BarterSettings != null)
            {
                return exactEntry.BarterSettings.LoyalLevel;
            }

            if (entries.Count == 1)
            {
                return entries.Values.FirstOrDefault(x => x != null)?.BarterSettings?.LoyalLevel ?? 1;
            }
        }

        foreach (var (traderKey, entries) in request.WeaponBuildTraders)
        {
            if (!string.Equals(ResolveTraderId(traderKey), traderId, StringComparison.OrdinalIgnoreCase)
                || entries == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sourceAssortId)
                && entries.TryGetValue(sourceAssortId, out var exactEntry)
                && exactEntry?.BarterSettings != null)
            {
                return exactEntry.BarterSettings.LoyalLevel;
            }

            if (entries.Count == 1)
            {
                return entries.Values.FirstOrDefault(x => x != null)?.BarterSettings?.LoyalLevel ?? 1;
            }
        }

        return 0;
    }

    private void AddQuestAssortRewardDisplay(
        Quest quest,
        string traderId,
        Trader trader,
        MongoId assortId,
        string? status)
    {
        EnsureRewardBuckets(quest);
        var rewardBucket = NormalizeRewardBucket(status);
        var rewards = quest.Rewards![rewardBucket];
        var assortIdText = assortId.ToString();

        if (rewards.Any(x =>
                x != null
                && x.Type == RewardType.AssortmentUnlock
                && string.Equals(x.Target?.ToString(), assortIdText, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.TraderId.ToString(), traderId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var rewardItems = BuildQuestRewardItemsFromTraderAssort(trader.Assort!, assortIdText);
        if (rewardItems.Count == 0)
        {
            return;
        }

        var loyaltyLevel = trader.Assort!.LoyalLevelItems.TryGetValue(assortId, out var loyalty)
            ? loyalty
            : 1;

        rewards.Add(new Reward
        {
            AvailableInGameEditions = [],
            GameMode = ["regular", "pve"],
            Id = new MongoId(),
            Index = rewards.Count,
            Type = RewardType.AssortmentUnlock,
            Target = assortIdText,
            TraderId = new MongoId(traderId),
            Value = 1,
            Unknown = false,
            IsHidden = false,
            LoyaltyLevel = loyaltyLevel,
            Items = rewardItems
        });
    }

    private bool AddExplicitAssortUnlockReward(
        Quest quest,
        YATMItemModificationRequest request,
        YATMQuestRewardConfig config,
        string rewardBucket)
    {
        var traderId = ResolveTraderId(config.TraderId);
        if (string.IsNullOrWhiteSpace(traderId)
            || string.IsNullOrWhiteSpace(config.AssortId)
            || !databaseServer.GetTables().Traders.TryGetValue(traderId, out var trader)
            || trader?.Assort == null
            || !TryCreateMongoId(config.AssortId, out var assortMongoId)
            || !HasValidTraderAssort(trader, assortMongoId))
        {
            return false;
        }

        AddQuestAssortRewardDisplay(quest, traderId, trader, assortMongoId, rewardBucket);
        return true;
    }

    private static List<Item> BuildQuestRewardItemsFromTraderAssort(TraderAssort assort, string assortId)
    {
        var sourceItems = CollectOfferItems(assort.Items, assortId);
        if (sourceItems.Count == 0)
        {
            return [];
        }

        var cloned = sourceItems.Select(CloneItem).ToList();
        var root = cloned.FirstOrDefault(x => x.Id.ToString().Equals(assortId, StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            root.ParentId = null;
            root.SlotId = null;
        }

        return cloned;
    }

    private static List<Item> CollectOfferItems(List<Item> allItems, string rootAssortId)
    {
        var collected = new List<Item>();
        var queuedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootAssortId);
        queuedIds.Add(rootAssortId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();

            foreach (var item in allItems.Where(x =>
                         x.Id.ToString().Equals(currentId, StringComparison.OrdinalIgnoreCase)
                         || x.ParentId?.ToString().Equals(currentId, StringComparison.OrdinalIgnoreCase) == true))
            {
                if (collected.Any(x => x.Id.Equals(item.Id)))
                {
                    continue;
                }

                collected.Add(item);
                var itemId = item.Id.ToString();
                if (queuedIds.Add(itemId))
                {
                    queue.Enqueue(itemId);
                }
            }
        }

        return collected;
    }

    private static Item CloneItem(Item source)
    {
        return new Item
        {
            Id = source.Id,
            Template = source.Template,
            ParentId = source.ParentId,
            SlotId = source.SlotId,
            Location = source.Location,
            Upd = CloneUpd(source.Upd)
        };
    }

    private static void AddItemReward(
        Quest quest,
        string itemTpl,
        int count,
        bool findInRaid,
        bool isHidden,
        string rewardBucket)
    {
        EnsureRewardBuckets(quest);
        var rewards = quest.Rewards![rewardBucket];
        var rewardItemId = new MongoId();

        rewards.Add(new Reward
        {
            Id = new MongoId(),
            Type = RewardType.Item,
            Index = rewards.Count,
            FindInRaid = findInRaid,
            Unknown = false,
            Value = count,
            Target = rewardItemId.ToString(),
            IsHidden = isHidden,
            GameMode = ["regular", "pve"],
            AvailableInGameEditions = [],
            Items =
            [
                new Item
                {
                    Id = rewardItemId,
                    Template = new MongoId(itemTpl),
                    Upd = findInRaid
                        ? new Upd { SpawnedInSession = true }
                        : null
                }
            ]
        });
    }

    private bool AddWeaponBuildReward(
        Quest quest,
        YATMItemModificationRequest request,
        YATMQuestRewardConfig config,
        int count,
        bool findInRaid,
        bool isHidden,
        string rewardBucket)
    {
        IReadOnlyList<Item>? sourceItems = null;
        var sourceDescription = string.Empty;

        if (!string.IsNullOrWhiteSpace(config.WeaponBuildId))
        {
            if (!weaponBuildService.TryGetBuild(config.WeaponBuildId, request.ItemId, out var configuredBuild))
            {
                YATMLogger.Log(
                    $"[DeferredQuestItems] Weapon build '{config.WeaponBuildId}' could not be resolved for item {request.ItemId}.");
                return false;
            }

            sourceItems = configuredBuild.Items;
            sourceDescription = $"weapon build {configuredBuild.BuildId}";
        }
        else if (TryResolveRewardTraderOffer(config, out var traderOfferItems, out var traderOfferDescription))
        {
            sourceItems = traderOfferItems;
            sourceDescription = traderOfferDescription;
        }
        else if (weaponBuildService.TryGetUniqueBuildForWeaponTpl(request.ItemId, out var automaticBuild))
        {
            sourceItems = automaticBuild.Items;
            sourceDescription = $"automatic weapon build {automaticBuild.BuildId}";
        }
        else if (!string.IsNullOrWhiteSpace(config.PresetId))
        {
            var presetItems = ResolvePresetItems(request, config.PresetId);
            if (presetItems.Count > 0)
            {
                sourceItems = presetItems;
                sourceDescription = $"preset fallback {config.PresetId}";
            }
        }

        if (sourceItems == null || sourceItems.Count == 0)
        {
            YATMLogger.Log(
                $"[DeferredQuestItems] rewardType Weapon for item {request.ItemId} requires weaponBuildId, a full trader assortId, or one unique registered build for that weapon tpl. Use rewardType Item for a bare receiver.");
            return false;
        }

        var added = AddFullWeaponTreeReward(
            quest,
            sourceItems,
            count,
            findInRaid,
            isHidden,
            rewardBucket);

        if (added)
        {
            YATMLogger.LogDebug(
                $"[DeferredQuestItems] Added {count} full weapon reward(s) for {request.ItemId} from {sourceDescription}.");
        }

        return added;
    }

    private bool TryResolveRewardTraderOffer(
        YATMQuestRewardConfig config,
        out IReadOnlyList<Item> items,
        out string description)
    {
        items = [];
        description = string.Empty;

        if (string.IsNullOrWhiteSpace(config.AssortId))
        {
            return false;
        }

        var tables = databaseServer.GetTables();
        var configuredTraderId = ResolveTraderId(config.TraderId);

        if (!string.IsNullOrWhiteSpace(configuredTraderId)
            && tables.Traders.TryGetValue(configuredTraderId, out var configuredTrader)
            && configuredTrader?.Assort != null
            && TraderContainsAssort(configuredTrader, config.AssortId))
        {
            var configuredItems = CollectOfferItems(configuredTrader.Assort.Items, config.AssortId);
            if (configuredItems.Count > 1)
            {
                items = configuredItems;
                description = $"trader {configuredTraderId} assort {config.AssortId}";
                return true;
            }
        }

        foreach (var (traderId, trader) in tables.Traders)
        {
            if (trader?.Assort == null || !TraderContainsAssort(trader, config.AssortId))
            {
                continue;
            }

            var offerItems = CollectOfferItems(trader.Assort.Items, config.AssortId);
            if (offerItems.Count <= 1)
            {
                continue;
            }

            items = offerItems;
            description = $"trader {traderId} assort {config.AssortId}";
            return true;
        }

        return false;
    }

    private bool AddWeaponPresetReward(
        Quest quest,
        YATMItemModificationRequest request,
        string presetId,
        bool findInRaid,
        bool isHidden,
        string rewardBucket)
    {
        var sourceItems = ResolvePresetItems(request, presetId);
        return AddFullWeaponTreeReward(
            quest,
            sourceItems,
            1,
            findInRaid,
            isHidden,
            rewardBucket);
    }

    private static bool AddFullWeaponTreeReward(
        Quest quest,
        IReadOnlyList<Item> sourceItems,
        int count,
        bool findInRaid,
        bool isHidden,
        string rewardBucket)
    {
        if (sourceItems.Count == 0)
        {
            return false;
        }

        var sourceIds = sourceItems
            .Select(x => x.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roots = sourceItems.Where(x =>
        {
            var parentId = x.ParentId?.ToString();
            return string.IsNullOrWhiteSpace(parentId)
                || parentId.Equals("hideout", StringComparison.OrdinalIgnoreCase)
                || !sourceIds.Contains(parentId);
        }).ToList();

        if (roots.Count != 1)
        {
            return false;
        }

        var rootSourceItem = roots[0];
        EnsureRewardBuckets(quest);
        var rewards = quest.Rewards![rewardBucket];

        for (var weaponIndex = 0; weaponIndex < Math.Max(1, count); weaponIndex++)
        {
            var idMap = sourceItems.ToDictionary(
                x => x.Id.ToString(),
                _ => new MongoId(),
                StringComparer.OrdinalIgnoreCase);

            var rewardItems = new List<Item>();
            foreach (var sourceItem in sourceItems)
            {
                var sourceId = sourceItem.Id.ToString();
                var sourceParentId = sourceItem.ParentId?.ToString();
                string? newParentId = null;

                if (!string.IsNullOrWhiteSpace(sourceParentId)
                    && !sourceParentId.Equals("hideout", StringComparison.OrdinalIgnoreCase)
                    && idMap.TryGetValue(sourceParentId, out var mappedParent))
                {
                    newParentId = mappedParent.ToString();
                }

                var newItem = new Item
                {
                    Id = idMap[sourceId],
                    Template = sourceItem.Template,
                    ParentId = newParentId,
                    SlotId = sourceItem.SlotId,
                    Location = sourceItem.Location,
                    Upd = CloneUpd(sourceItem.Upd)
                };

                if (sourceItem.Id.Equals(rootSourceItem.Id))
                {
                    newItem.ParentId = null;
                    newItem.SlotId = null;
                }

                if (findInRaid)
                {
                    newItem.Upd ??= new Upd();
                    newItem.Upd.SpawnedInSession = true;
                }

                rewardItems.Add(newItem);
            }

            var rewardRootId = idMap[rootSourceItem.Id.ToString()];
            rewards.Add(new Reward
            {
                Id = new MongoId(),
                Type = RewardType.Item,
                Index = rewards.Count,
                Target = rewardRootId.ToString(),
                Value = 1,
                FindInRaid = findInRaid,
                IsHidden = isHidden,
                Unknown = false,
                GameMode = ["regular", "pve"],
                AvailableInGameEditions = [],
                Items = rewardItems
            });
        }

        return true;
    }

    private List<Item> ResolvePresetItems(YATMItemModificationRequest request, string presetId)
    {
        var tables = databaseServer.GetTables();

        if (tables.Globals.ItemPresets.TryGetValue(presetId, out var preset)
            && preset?.Items is { Count: > 0 })
        {
            return preset.Items;
        }

        var presetByRuntimeId = tables.Globals.ItemPresets.Values.FirstOrDefault(x =>
            x != null
            && x.Id.ToString().Equals(presetId, StringComparison.OrdinalIgnoreCase)
            && x.Items is { Count: > 0 });

        if (presetByRuntimeId?.Items is { Count: > 0 })
        {
            return presetByRuntimeId.Items;
        }

        // WTT/CommonLib preset trader configs can reference a cache/source preset id
        // instead of the global runtime preset id. Resolve that reference through the
        // final trader offer and clone the built offer tree.
        foreach (var (traderKey, entries) in request.PresetTraders)
        {
            var traderId = ResolveTraderId(traderKey);
            if (string.IsNullOrWhiteSpace(traderId)
                || !tables.Traders.TryGetValue(traderId, out var trader)
                || trader?.Assort == null)
            {
                continue;
            }

            foreach (var (sourceAssortId, presetConfig) in entries)
            {
                if (presetConfig == null
                    || !presetConfig.PresetId.Equals(presetId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var questConfig = new YATMQuestAssortConfig
                {
                    TraderId = traderId,
                    AssortId = sourceAssortId,
                    PresetId = presetId
                };

                var finalAssortId = ResolveFinalAssortId(request, questConfig, traderId, trader);
                if (!string.IsNullOrWhiteSpace(finalAssortId))
                {
                    var items = CollectOfferItems(trader.Assort.Items, finalAssortId);
                    if (items.Count > 0)
                    {
                        return items;
                    }
                }
            }
        }

        // Last fallback: locate a unique global preset whose root template is the
        // custom item represented by this JSON request.
        var matchingPresets = tables.Globals.ItemPresets.Values
            .Where(x => x?.Items is { Count: > 0 })
            .Where(x =>
            {
                var itemIds = x!.Items
                    .Select(item => item.Id.ToString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var root = x.Items.FirstOrDefault(item =>
                {
                    var parentId = item.ParentId?.ToString();
                    return string.IsNullOrWhiteSpace(parentId)
                        || parentId.Equals("hideout", StringComparison.OrdinalIgnoreCase)
                        || !itemIds.Contains(parentId);
                });

                return root != null
                    && root.Template.ToString().Equals(request.ItemId, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        return matchingPresets.Count == 1
            ? matchingPresets[0]!.Items
            : [];
    }

    private static bool HasValidTraderAssort(Trader trader, MongoId assortId)
    {
        return trader.Assort?.Items?.Any(x => x.Id.Equals(assortId)) == true
            && trader.Assort.BarterScheme?.ContainsKey(assortId) == true
            && trader.Assort.LoyalLevelItems?.ContainsKey(assortId) == true;
    }

    private static bool TraderContainsAssort(Trader trader, string assortId)
    {
        return TryCreateMongoId(assortId, out var mongoId) && HasValidTraderAssort(trader, mongoId);
    }

    private static string? ResolveTraderId(string? traderKey)
    {
        if (string.IsNullOrWhiteSpace(traderKey))
        {
            return null;
        }

        if (TraderAliases.TryGetValue(traderKey.Trim(), out var traderId))
        {
            return traderId;
        }

        return TryCreateMongoId(traderKey, out _)
            ? traderKey
            : null;
    }

    private static string ResolveQuestAssortBucketKey(Trader trader, string? status)
    {
        var desired = NormalizeRewardBucket(status);
        var existingKey = trader.QuestAssort!.Keys.FirstOrDefault(x =>
            x.Equals(desired, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            return existingKey;
        }

        trader.QuestAssort[desired] = new Dictionary<MongoId, MongoId>();
        return desired;
    }

    private static string NormalizeRewardBucket(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return SuccessBucket;
        }

        if (status.Equals(StartedBucket, StringComparison.OrdinalIgnoreCase)
            || status.Equals("started", StringComparison.OrdinalIgnoreCase))
        {
            return StartedBucket;
        }

        if (status.Equals(FailBucket, StringComparison.OrdinalIgnoreCase)
            || status.Equals("fail", StringComparison.OrdinalIgnoreCase))
        {
            return FailBucket;
        }

        return SuccessBucket;
    }

    private static void EnsureRewardBuckets(Quest quest)
    {
        quest.Rewards ??= new Dictionary<string, List<Reward>>(StringComparer.OrdinalIgnoreCase);
        quest.Rewards.TryAdd(StartedBucket, []);
        quest.Rewards.TryAdd(SuccessBucket, []);
        quest.Rewards.TryAdd(FailBucket, []);
    }

    private static string NormalizeCurrencyTpl(string currencyTpl)
    {
        if (currencyTpl.Equals("RUB", StringComparison.OrdinalIgnoreCase))
        {
            return RubTpl;
        }

        if (currencyTpl.Equals("USD", StringComparison.OrdinalIgnoreCase))
        {
            return UsdTpl;
        }

        if (currencyTpl.Equals("EUR", StringComparison.OrdinalIgnoreCase))
        {
            return EurTpl;
        }

        return TryCreateMongoId(currencyTpl, out _)
            ? currencyTpl
            : RubTpl;
    }

    private static bool TryCreateMongoId(string? value, out MongoId mongoId)
    {
        mongoId = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 24)
        {
            return false;
        }

        try
        {
            mongoId = new MongoId(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Upd? CloneUpd(Upd? original)
    {
        if (original == null)
        {
            return null;
        }

        return new Upd
        {
            UnlimitedCount = original.UnlimitedCount,
            StackObjectsCount = original.StackObjectsCount,
            BuyRestrictionMax = original.BuyRestrictionMax,
            BuyRestrictionCurrent = original.BuyRestrictionCurrent,
            Repairable = original.Repairable,
            Foldable = original.Foldable,
            FireMode = original.FireMode,
            Key = original.Key,
            MedKit = original.MedKit,
            Resource = original.Resource,
            Dogtag = original.Dogtag,
            FoodDrink = original.FoodDrink,
            RecodableComponent = original.RecodableComponent,
            RepairKit = original.RepairKit,
            Togglable = original.Togglable,
            FaceShield = original.FaceShield,
            Sight = original.Sight,
            SpawnedInSession = original.SpawnedInSession
        };
    }

    private static string BuildQuestAssortKey(YATMItemModificationRequest request, YATMQuestAssortConfig config)
    {
        return $"{request.SourceFile}|{request.ItemId}|{config.TraderId}|{config.QuestId}|{config.AssortId}|{config.Status}";
    }

    private static string BuildQuestRewardKey(YATMItemModificationRequest request, YATMQuestRewardConfig config)
    {
        return $"{request.SourceFile}|{request.ItemId}|{config.QuestId}|{config.RewardType}|{config.Count}|{config.FindInRaid}|{config.IsHidden}|{config.CurrencyTpl}|{config.WeaponBuildId}|{config.PresetId}|{config.TraderId}|{config.AssortId}|{config.Status}";
    }

    private static void LogDeferredFailure(bool finalPass, YATMItemModificationRequest request, string detail)
    {
        if (finalPass)
        {
            YATMLogger.Log($"[DeferredQuestItems] Could not resolve {detail} from '{request.SourceFile}' for item {request.ItemId}.");
        }
        else
        {
            YATMLogger.LogDebug($"[DeferredQuestItems] Deferred unresolved {detail} from item {request.ItemId}; it will be retried.");
        }
    }
}
