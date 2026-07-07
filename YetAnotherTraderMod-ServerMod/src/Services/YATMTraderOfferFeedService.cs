using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using YetAnotherTraderMod.src.GeneratedOffers;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.Services;

/// <summary>
/// Public addon-facing service for feeding Tony offers.
///
/// Current supported addon data:
/// - db/CustomTraderOffers/*.json/jsonc
///   Shape: { TonyTraderId: { items, barter_scheme, loyal_level_items, yatm_settings } }
/// - db/CustomWeaponPresets/*.json/jsonc
///   Optional preset registration for other systems/addons.
///
/// No alternate offer schemas are read here. Keep the feed simple and explicit.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMTraderOfferFeedService(
    ModHelper modHelper,
    DatabaseServer databaseServer,
    ISptLogger<YATMTraderOfferFeedService> logger)
{
    public const string TonyTraderId = "66a0f6b2c4d8e90123456789";

    private const string DefaultOfferDir = "db/CustomTraderOffers";
    private const string DefaultPresetDir = "db/CustomWeaponPresets";

    private readonly List<YATMRawTraderOfferFile> _rawOfferFiles = [];
    private readonly HashSet<string> _rootOfferIds = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<YATMRawTraderOfferFile> GetRegisteredTonyTraderOffers()
    {
        return _rawOfferFiles;
    }

    public void ClearRegisteredTonyTraderOffers()
    {
        _rawOfferFiles.Clear();
        _rootOfferIds.Clear();
    }

    /// <summary>
    /// Loads Tony raw offer files. Default path: db/CustomTraderOffers
    /// </summary>
    public async Task CreateTonyTraderOffers(Assembly assembly, string? relativePath = null)
    {
        var assemblyLocation = GetModPath(assembly);
        var finalPath = GetOfferPath(assemblyLocation, relativePath);
        var jsonFiles = GetJsonAndJsoncFilesFromPath(finalPath);

        if (jsonFiles.Length == 0)
        {
            return;
        }

        var registeredFileCount = 0;
        var registeredRootCount = 0;

        foreach (var file in jsonFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var rawFile = ParseRawTonyOfferFile(json, file);

                if (rawFile == null)
                {
                    logger.Warning($"[YATM Offer Feed] Skipped {file}: expected {{ '{TonyTraderId}': {{ items, barter_scheme, loyal_level_items, yatm_settings }} }}.");
                    continue;
                }

                var rootIds = GetRootOfferIds(rawFile.Items);
                var duplicateRootIds = rootIds.Where(x => !_rootOfferIds.Add(x)).ToList();
                if (duplicateRootIds.Count > 0)
                {
                    logger.Warning($"[YATM Offer Feed] Skipped {file}: duplicate root offer id(s): {string.Join(", ", duplicateRootIds)}.");
                    foreach (var id in rootIds.Except(duplicateRootIds, StringComparer.OrdinalIgnoreCase))
                    {
                        _rootOfferIds.Remove(id);
                    }
                    continue;
                }

                _rawOfferFiles.Add(rawFile);
                registeredFileCount++;
                registeredRootCount += rootIds.Count;

                logger.Info($"[YATM Offer Feed] Registered raw YATM offer file {file}: {rawFile.Items.Count} item row(s), {rootIds.Count} root offer(s).");
            }
            catch (Exception ex)
            {
                logger.Error($"[YATM Offer Feed] Failed to load raw offer file {file}: {ex.Message}");
            }
        }

        if (registeredFileCount > 0)
        {
            logger.Info($"[YATM Offer Feed] Registered {registeredRootCount} Tony root offer(s) from {registeredFileCount} file(s) in {finalPath}.");
        }
    }

    /// <summary>
    /// Loads weapon preset files from Tony/addon mods into Globals.ItemPresets.
    /// Default path: db/CustomWeaponPresets
    /// </summary>
    public async Task CreateCustomWeaponPresets(Assembly assembly, string? relativePath = null)
    {
        var assemblyLocation = GetModPath(assembly);
        var finalPath = GetPresetPath(assemblyLocation, relativePath);

        var jsonFiles = GetJsonAndJsoncFilesFromPath(finalPath);
        if (jsonFiles.Length == 0)
        {
            return;
        }

        var itemPresets = databaseServer.GetTables().Globals.ItemPresets;
        var loadedCount = 0;

        foreach (var file in jsonFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var presets = Deserialize<Dictionary<string, Preset>>(json);

                if (presets == null || presets.Count == 0)
                {
                    logger.Warning($"[YATM Offer Feed] No presets found in {file}");
                    continue;
                }

                foreach (var kvp in presets)
                {
                    var preset = kvp.Value;
                    if (preset == null)
                    {
                        continue;
                    }

                    if (preset.Items == null || preset.Items.Count == 0)
                    {
                        logger.Warning($"[YATM Offer Feed] Preset {kvp.Key} has no items. Skipping.");
                        continue;
                    }

                    itemPresets[preset.Id] = preset;
                    loadedCount++;

                    YATMLogger.LogDebug($"[YATM Offer Feed] Loaded weapon preset: {preset.Id}");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[YATM Offer Feed] Failed to load preset file {file}: {ex.Message}");
            }
        }

        if (loadedCount > 0)
        {
            logger.Info($"[YATM Offer Feed] Loaded {loadedCount} custom weapon preset(s) from {finalPath}");
        }
    }

    private static YATMRawTraderOfferFile? ParseRawTonyOfferFile(string json, string file)
    {
        var documentOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var root = JsonNode.Parse(json, null, documentOptions) as JsonObject;
        if (root == null)
        {
            return null;
        }

        // This is intentionally not supported by the current feed.
        if (root.ContainsKey("SchemaVersion") || root.ContainsKey("Offers"))
        {
            return null;
        }

        if (!root.TryGetPropertyValue(TonyTraderId, out var traderNode) || traderNode is not JsonObject traderObject)
        {
            return null;
        }

        var items = CloneArray(GetArray(traderObject, "items"));
        var barterScheme = CloneObject(GetObject(traderObject, "barter_scheme"));
        var loyalLevelItems = CloneObject(GetObject(traderObject, "loyal_level_items"));
        var yatmSettings = CloneObject(GetObject(traderObject, "yatm_settings"));

        if (items.Count == 0)
        {
            return null;
        }

        return new YATMRawTraderOfferFile
        {
            SourceFile = file,
            Items = items,
            BarterScheme = barterScheme,
            LoyalLevelItems = loyalLevelItems,
            YatmSettings = yatmSettings
        };
    }

    private static List<string> GetRootOfferIds(JsonArray items)
    {
        var rootIds = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonObject itemObj)
            {
                continue;
            }

            var parentId = ReadJsonString(itemObj, "parentId");
            var slotId = ReadJsonString(itemObj, "slotId");
            var id = ReadJsonString(itemObj, "_id");

            if (string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase)
                && string.Equals(slotId, "hideout", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(id))
            {
                rootIds.Add(id);
            }
        }

        return rootIds;
    }

    private string GetModPath(Assembly assembly)
    {
        return modHelper.GetAbsolutePathToModFolder(assembly);
    }

    private static string GetOfferPath(string assemblyLocation, string? relativePath)
    {
        return Path.Combine(assemblyLocation, relativePath ?? DefaultOfferDir);
    }

    private static string GetPresetPath(string assemblyLocation, string? relativePath)
    {
        return Path.Combine(assemblyLocation, relativePath ?? DefaultPresetDir);
    }

    private static string[] GetJsonAndJsoncFilesFromPath(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.GetFiles(path, "*.json")
            .Concat(Directory.GetFiles(path, "*.jsonc"))
            .OrderBy(x => x)
            .ToArray();
    }

    private static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static JsonArray? GetArray(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonArray array ? array : null;
    }

    private static JsonObject? GetObject(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonObject jsonObject ? jsonObject : null;
    }

    private static JsonArray CloneArray(JsonArray? source)
    {
        var clone = new JsonArray();
        if (source == null)
        {
            return clone;
        }

        foreach (var item in source)
        {
            clone.Add(item?.DeepClone());
        }

        return clone;
    }

    private static JsonObject CloneObject(JsonObject? source)
    {
        var clone = new JsonObject();
        if (source == null)
        {
            return clone;
        }

        foreach (var kvp in source)
        {
            clone[kvp.Key] = kvp.Value?.DeepClone();
        }

        return clone;
    }

    private static string? ReadJsonString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node != null ? node.ToString() : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
