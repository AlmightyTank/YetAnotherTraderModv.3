using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using YetAnotherTraderMod.src.Models;
using YetAnotherTraderMod.src.Services;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.Services.ItemHelpers;

/// <summary>
/// Converts Tony-targeted presetTraders and weaponBuildTraders entries into
/// YATM raw offer-feed rows before Tony builds its final runtime assort.
/// Both sections use the existing addToTraders flag.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMWttPresetOfferBridgeService(
    DatabaseServer databaseServer,
    YATMTraderOfferFeedService traderOfferFeedService,
    YATMWeaponBuildService weaponBuildService)
{
    private const string TonyTraderId = "66a0f6b2c4d8e90123456789";
    private const string RoublesTpl = "5449016a4bdc2d6f028b456f";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HashSet<string> _processedOfferKeys = new(StringComparer.OrdinalIgnoreCase);

    public async Task RegisterTonyPresetOffers(Assembly assembly, IEnumerable<string> relativePaths)
    {
        var modPath = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve mod path.");

        var registered = 0;
        var preservedEarly = 0;

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
                var result = await RegisterFile(file);
                registered += result.Registered;
                preservedEarly += result.PreservedEarly;
            }
        }

        if (registered > 0)
        {
            YATMLogger.Log($"[WttWeaponOfferBridge] Registered {registered} Tony full weapon offer(s) through the YATM offer feed.");
        }

        if (preservedEarly > 0)
        {
            YATMLogger.LogDebug(
                $"[WttWeaponOfferBridge] Preserved {preservedEarly} complete early full weapon offer(s). Runtime preservation will carry them forward.");
        }
    }

    private async Task<(int Registered, int PreservedEarly)> RegisterFile(string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var requests = JsonSerializer.Deserialize<Dictionary<string, YATMItemModificationRequest>>(json, JsonOptions);
            if (requests == null || requests.Count == 0)
            {
                return (0, 0);
            }

            var registered = 0;
            var preservedEarly = 0;

            foreach (var (itemId, request) in requests)
            {
                if (request == null)
                {
                    continue;
                }

                request.ItemId = itemId;
                request.SourceFile = file;

                // Use the same WTT gate as every other trader addition.
                if (!request.AddToTraders)
                {
                    continue;
                }

                var presetResult = RegisterPresetTraderOffers(request, file);
                registered += presetResult.Registered;
                preservedEarly += presetResult.PreservedEarly;

                var buildResult = RegisterWeaponBuildTraderOffers(request, file);
                registered += buildResult.Registered;
                preservedEarly += buildResult.PreservedEarly;
            }

            return (registered, preservedEarly);
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[WttWeaponOfferBridge] Failed to process '{file}': {ex.Message}");
            return (0, 0);
        }
    }

    private (int Registered, int PreservedEarly) RegisterPresetTraderOffers(
        YATMItemModificationRequest request,
        string file)
    {
        if (request.PresetTraders == null || request.PresetTraders.Count == 0)
        {
            return (0, 0);
        }

        var registered = 0;
        var preservedEarly = 0;

        foreach (var (traderKey, offers) in request.PresetTraders)
        {
            if (!IsTonyTrader(traderKey) || offers == null)
            {
                continue;
            }

            foreach (var (sourceAssortId, presetConfig) in offers)
            {
                if (presetConfig == null
                    || !TryCreateMongoId(sourceAssortId, out _)
                    || string.IsNullOrWhiteSpace(presetConfig.PresetId))
                {
                    continue;
                }

                var offerKey = $"preset|{Path.GetFullPath(file)}|{request.ItemId}|{sourceAssortId}";
                if (!_processedOfferKeys.Add(offerKey))
                {
                    continue;
                }

                var preset = ResolvePreset(request.ItemId, presetConfig.PresetId);
                if (preset?.Items == null || preset.Items.Count == 0)
                {
                    YATMLogger.Log(
                        $"[WttWeaponOfferBridge] Could not resolve preset '{presetConfig.PresetId}' for item {request.ItemId} in '{file}'.");
                    continue;
                }

                if (EarlyTonyHasCompleteOffer(sourceAssortId, preset.Items))
                {
                    preservedEarly++;
                    continue;
                }

                RemoveIncompleteEarlyTonyOffer(sourceAssortId);

                if (RegisterFullWeaponOffer(
                        $"WTT preset bridge: {file} [{sourceAssortId}]",
                        sourceAssortId,
                        preset.Items,
                        presetConfig.BarterSettings,
                        presetConfig.Barters))
                {
                    registered++;
                }
            }
        }

        return (registered, preservedEarly);
    }

    private (int Registered, int PreservedEarly) RegisterWeaponBuildTraderOffers(
        YATMItemModificationRequest request,
        string file)
    {
        if (request.WeaponBuildTraders == null
            || request.WeaponBuildTraders.Count == 0)
        {
            return (0, 0);
        }

        var registered = 0;
        var preservedEarly = 0;

        foreach (var (traderKey, offers) in request.WeaponBuildTraders)
        {
            if (!IsTonyTrader(traderKey) || offers == null)
            {
                continue;
            }

            foreach (var (sourceAssortId, buildConfig) in offers)
            {
                if (buildConfig == null
                    || !TryCreateMongoId(sourceAssortId, out _)
                    || string.IsNullOrWhiteSpace(buildConfig.WeaponBuildId))
                {
                    continue;
                }

                var offerKey = $"build|{Path.GetFullPath(file)}|{request.ItemId}|{sourceAssortId}";
                if (!_processedOfferKeys.Add(offerKey))
                {
                    continue;
                }

                if (!weaponBuildService.TryGetBuild(
                        buildConfig.WeaponBuildId,
                        expectedRootTpl: null,
                        out var build))
                {
                    YATMLogger.Log(
                        $"[WttWeaponOfferBridge] Could not resolve weapon build '{buildConfig.WeaponBuildId}' for item/build entry {request.ItemId} in '{file}'.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(request.ItemId)
                    && !request.ItemId.Equals(build.RootTemplateId, StringComparison.OrdinalIgnoreCase)
                    && !request.ItemId.Equals(build.BuildId, StringComparison.OrdinalIgnoreCase))
                {
                    YATMLogger.LogDebug(
                        $"[WttWeaponOfferBridge] Entry {request.ItemId} references build {build.BuildId} with root tpl {build.RootTemplateId}; the build root is authoritative for trader offer {sourceAssortId}.");
                }

                if (EarlyTonyHasCompleteOffer(sourceAssortId, build.Items))
                {
                    preservedEarly++;
                    continue;
                }

                RemoveIncompleteEarlyTonyOffer(sourceAssortId);

                if (RegisterFullWeaponOffer(
                        $"WTT weapon build bridge: {file} [{sourceAssortId}] build={build.BuildId}",
                        sourceAssortId,
                        build.Items,
                        buildConfig.BarterSettings,
                        buildConfig.Barters))
                {
                    registered++;
                }
            }
        }

        return (registered, preservedEarly);
    }

    private bool RegisterFullWeaponOffer(
        string source,
        string sourceAssortId,
        IReadOnlyList<Item> sourceItems,
        YATMPresetBarterSettings barterSettings,
        List<YATMBarterComponent> barters)
    {
        var items = BuildOfferItems(sourceItems, sourceAssortId, barterSettings);
        if (items.Count == 0)
        {
            YATMLogger.Log($"[WttWeaponOfferBridge] Failed to build offer {sourceAssortId} from {source}.");
            return false;
        }

        var barterScheme = BuildBarterScheme(sourceAssortId, barters);
        var loyalLevelItems = new JsonObject
        {
            [sourceAssortId] = Math.Max(1, barterSettings.LoyalLevel)
        };

        var yatmSettings = new JsonObject
        {
            [sourceAssortId] = new JsonObject
            {
                ["CashOnly"] = false,
                ["AlwaysBarter"] = false,
                ["AlwaysInStock"] = false
            }
        };

        // Register the offer in Tony's generated-offer feed so it survives the
        // final trader replacement and every restock reroll.
        var feedRegistered = traderOfferFeedService.RegisterRawTonyTraderOffer(
            source,
            items,
            barterScheme,
            loyalLevelItems,
            yatmSettings);

        // CommonLibExtended inserts presetTraders into the early Tony placeholder.
        // weaponBuildTraders is YATM-only, so it must be inserted here as well or it
        // will not be visible to other PostDB loaders before Tony builds the final
        // runtime assort. The final runtime capture/merge is duplicate-safe.
        var earlyInjected = TryInjectOfferIntoEarlyTonyAssort(
            source,
            sourceAssortId,
            items,
            barterScheme,
            loyalLevelItems);

        return feedRegistered || earlyInjected;
    }

    private bool TryInjectOfferIntoEarlyTonyAssort(
        string source,
        string sourceAssortId,
        JsonArray items,
        JsonObject barterScheme,
        JsonObject loyalLevelItems)
    {
        try
        {
            var tables = databaseServer.GetTables();
            if (!tables.Traders.TryGetValue(TonyTraderId, out var trader)
                || trader?.Assort == null)
            {
                YATMLogger.Log(
                    $"[WttWeaponOfferBridge] Could not inject {sourceAssortId} into Tony's early assort: trader placeholder is missing.");
                return false;
            }

            var temporaryAssortNode = new JsonObject
            {
                ["items"] = items.DeepClone(),
                ["barter_scheme"] = barterScheme.DeepClone(),
                ["loyal_level_items"] = loyalLevelItems.DeepClone()
            };

            var temporaryAssort = JsonSerializer.Deserialize<TraderAssort>(
                temporaryAssortNode.ToJsonString(JsonOptions),
                JsonOptions);

            if (temporaryAssort == null || temporaryAssort.Items.Count == 0)
            {
                YATMLogger.Log(
                    $"[WttWeaponOfferBridge] Could not deserialize early assort rows for {sourceAssortId} from {source}.");
                return false;
            }

            YATMJsonExtensionDataSanitizer.SanitizeAssort(temporaryAssort);

            var existingIds = trader.Assort.Items
                .Select(x => x.Id.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var addedRows = 0;
            foreach (var item in temporaryAssort.Items)
            {
                if (existingIds.Add(item.Id.ToString()))
                {
                    trader.Assort.Items.Add(item);
                    addedRows++;
                }
            }

            foreach (var (offerId, scheme) in temporaryAssort.BarterScheme)
            {
                trader.Assort.BarterScheme[offerId] = scheme;
            }

            foreach (var (offerId, loyaltyLevel) in temporaryAssort.LoyalLevelItems)
            {
                trader.Assort.LoyalLevelItems[offerId] = loyaltyLevel;
            }

            YATMJsonExtensionDataSanitizer.SanitizeAssort(trader.Assort);

            if (addedRows > 0)
            {
                YATMLogger.Log(
                    $"[WttWeaponOfferBridge] Added full weapon offer {sourceAssortId} to Tony's early assort: {addedRows} item row(s).");
            }
            else
            {
                YATMLogger.LogDebug(
                    $"[WttWeaponOfferBridge] Early Tony assort already contained every row for full weapon offer {sourceAssortId}.");
            }

            return HasValidOffer(
                trader.Assort,
                new MongoId(sourceAssortId));
        }
        catch (Exception ex)
        {
            YATMLogger.Log(
                $"[WttWeaponOfferBridge] Failed to inject full weapon offer {sourceAssortId} into Tony's early assort from {source}: {ex.Message}");
            return false;
        }
    }

    private bool EarlyTonyHasCompleteOffer(string sourceAssortId, IReadOnlyList<Item> expectedItems)
    {
        var tables = databaseServer.GetTables();
        if (!tables.Traders.TryGetValue(TonyTraderId, out var trader)
            || trader?.Assort == null
            || !TryCreateMongoId(sourceAssortId, out var sourceAssortMongoId)
            || !HasValidOffer(trader.Assort, sourceAssortMongoId))
        {
            return false;
        }

        var existingItems = CollectOfferItems(trader.Assort.Items, sourceAssortId);
        if (existingItems.Count != expectedItems.Count)
        {
            return false;
        }

        var expectedTemplates = expectedItems
            .GroupBy(x => x.Template.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var existingTemplates = existingItems
            .GroupBy(x => x.Template.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        return expectedTemplates.Count == existingTemplates.Count
            && expectedTemplates.All(x =>
                existingTemplates.TryGetValue(x.Key, out var count) && count == x.Value);
    }

    private void RemoveIncompleteEarlyTonyOffer(string sourceAssortId)
    {
        var tables = databaseServer.GetTables();
        if (!tables.Traders.TryGetValue(TonyTraderId, out var trader)
            || trader?.Assort == null)
        {
            return;
        }

        var existingItems = CollectOfferItems(trader.Assort.Items, sourceAssortId);
        if (existingItems.Count == 0)
        {
            return;
        }

        var ids = existingItems.Select(x => x.Id).ToHashSet();
        trader.Assort.Items.RemoveAll(x => ids.Contains(x.Id));

        if (TryCreateMongoId(sourceAssortId, out var rootId))
        {
            trader.Assort.BarterScheme.Remove(rootId);
            trader.Assort.LoyalLevelItems.Remove(rootId);
        }

        YATMLogger.LogDebug(
            $"[WttWeaponOfferBridge] Removed incomplete early Tony offer {sourceAssortId} before registering its full attached build.");
    }

    private Preset? ResolvePreset(string itemTpl, string configuredPresetId)
    {
        var presets = databaseServer.GetTables().Globals.ItemPresets;

        if (presets.TryGetValue(configuredPresetId, out var exactPreset) && exactPreset != null)
        {
            return exactPreset;
        }

        var byRuntimeId = presets.Values.FirstOrDefault(x =>
            x != null && x.Id.ToString().Equals(configuredPresetId, StringComparison.OrdinalIgnoreCase));

        if (byRuntimeId != null)
        {
            return byRuntimeId;
        }

        var matches = presets.Values
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
                    && root.Template.ToString().Equals(itemTpl, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (matches.Count == 1)
        {
            YATMLogger.LogDebug(
                $"[WttWeaponOfferBridge] Resolved configured preset '{configuredPresetId}' by root template {itemTpl} to runtime preset {matches[0]!.Id}.");
            return matches[0];
        }

        return null;
    }

    private static JsonArray BuildOfferItems(
        IReadOnlyList<Item> sourceItems,
        string rootOfferId,
        YATMPresetBarterSettings barterSettings)
    {
        if (sourceItems.Count == 0)
        {
            return [];
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
            return [];
        }

        var sourceRoot = roots[0];
        var idMap = sourceItems.ToDictionary(
            x => x.Id.ToString(),
            x => x.Id.Equals(sourceRoot.Id) ? rootOfferId : new MongoId().ToString(),
            StringComparer.OrdinalIgnoreCase);

        var output = new JsonArray();

        foreach (var sourceItem in sourceItems)
        {
            var sourceId = sourceItem.Id.ToString();
            var newId = idMap[sourceId];
            var sourceParentId = sourceItem.ParentId?.ToString();
            var isRoot = sourceItem.Id.Equals(sourceRoot.Id);

            var node = new JsonObject
            {
                ["_id"] = newId,
                ["_tpl"] = sourceItem.Template.ToString()
            };

            if (isRoot)
            {
                node["parentId"] = "hideout";
                node["slotId"] = "hideout";
            }
            else if (!string.IsNullOrWhiteSpace(sourceParentId)
                     && idMap.TryGetValue(sourceParentId, out var mappedParentId))
            {
                node["parentId"] = mappedParentId;
                if (!string.IsNullOrWhiteSpace(sourceItem.SlotId))
                {
                    node["slotId"] = sourceItem.SlotId;
                }
            }

            if (sourceItem.Location != null)
            {
                node["location"] = JsonSerializer.SerializeToNode(sourceItem.Location, JsonOptions);
            }

            var upd = sourceItem.Upd == null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(sourceItem.Upd, JsonOptions) as JsonObject ?? new JsonObject();

            if (isRoot)
            {
                upd["UnlimitedCount"] = barterSettings.UnlimitedCount;
                upd["StackObjectsCount"] = Math.Max(1, barterSettings.StackObjectsCount);
                if (barterSettings.BuyRestrictionMax.HasValue)
                {
                    upd["BuyRestrictionMax"] = barterSettings.BuyRestrictionMax.Value;
                    upd["BuyRestrictionCurrent"] = 0;
                }
            }

            if (upd.Count > 0)
            {
                node["upd"] = upd;
            }

            output.Add(node);
        }

        return output;
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

    private static JsonObject BuildBarterScheme(string offerId, List<YATMBarterComponent> configuredBarters)
    {
        var components = configuredBarters
            .Where(x => x != null && TryCreateMongoId(x.TemplateId, out _) && x.Count > 0)
            .ToList();

        if (components.Count == 0)
        {
            components =
            [
                new YATMBarterComponent
                {
                    TemplateId = RoublesTpl,
                    Count = 1
                }
            ];
        }

        var option = new JsonArray();
        foreach (var component in components)
        {
            option.Add(new JsonObject
            {
                ["_tpl"] = component.TemplateId,
                ["count"] = component.Count
            });
        }

        var options = new JsonArray();
        options.Add(option);

        return new JsonObject
        {
            [offerId] = options
        };
    }

    private static bool HasValidOffer(TraderAssort assort, MongoId rootId)
    {
        return assort.Items.Any(x => x.Id.Equals(rootId))
            && assort.BarterScheme.ContainsKey(rootId)
            && assort.LoyalLevelItems.ContainsKey(rootId);
    }

    private static bool IsTonyTrader(string traderKey)
    {
        return traderKey.Equals("tony", StringComparison.OrdinalIgnoreCase)
            || traderKey.Equals(TonyTraderId, StringComparison.OrdinalIgnoreCase);
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
}
