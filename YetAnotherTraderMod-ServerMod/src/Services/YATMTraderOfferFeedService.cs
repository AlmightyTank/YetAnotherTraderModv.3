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
/// Supported addon data:
/// - db/CustomTraderOffers/*.json/jsonc inside Tony itself
/// - db/YATM/CustomTraderOffers/*.json/jsonc inside addon mods
///   Shape: { TonyTraderId: { items, barter_scheme, loyal_level_items, yatm_settings } }
/// - db/CustomWeaponPresets/*.json/jsonc inside Tony itself
/// - db/YATM/CustomWeaponPresets/*.json/jsonc inside addon mods
///
/// Addons may either call this service directly from an early loader, or simply ship files in
/// the db/YATM folders and let Tony auto-discover them before the runtime assort is built.
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
    private const string DefaultAddonOfferDir = "db/YATM/CustomTraderOffers";
    private const string DefaultAddonPresetDir = "db/YATM/CustomWeaponPresets";

    private readonly List<YATMRawTraderOfferFile> _rawOfferFiles = [];
    private readonly HashSet<string> _rootOfferIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedOfferFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedPresetFiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<YATMRawTraderOfferFile> GetRegisteredTonyTraderOffers()
    {
        return _rawOfferFiles;
    }

    public void ClearRegisteredTonyTraderOffers()
    {
        _rawOfferFiles.Clear();
        _rootOfferIds.Clear();
        _loadedOfferFiles.Clear();
    }

    /// <summary>
    /// Registers an in-memory Tony offer built by another YATM service, such as
    /// CustomConsumablesLoader. This uses the same pipeline as addon
    /// db/YATM/CustomTraderOffers files, so the offer survives Tony startup
    /// generation and restock rerolls.
    /// </summary>
    public bool RegisterRawTonyTraderOffer(
        string source,
        JsonArray items,
        JsonObject barterScheme,
        JsonObject loyalLevelItems,
        JsonObject? yatmSettings = null)
    {
        if (items.Count == 0)
        {
            logger.Warning($"[YATM Offer Feed] Skipped in-memory offer {source}: no item rows.");
            return false;
        }

        var rootIds = GetRootOfferIds(items);
        if (rootIds.Count == 0)
        {
            logger.Warning($"[YATM Offer Feed] Skipped in-memory offer {source}: no root hideout offer row.");
            return false;
        }

        var duplicateRootIds = rootIds.Where(x => !_rootOfferIds.Add(x)).ToList();
        if (duplicateRootIds.Count > 0)
        {
            logger.Warning($"[YATM Offer Feed] Skipped in-memory offer {source}: duplicate root offer id(s): {string.Join(", ", duplicateRootIds)}.");
            foreach (var id in rootIds.Except(duplicateRootIds, StringComparer.OrdinalIgnoreCase))
            {
                _rootOfferIds.Remove(id);
            }

            return false;
        }

        _rawOfferFiles.Add(new YATMRawTraderOfferFile
        {
            SourceFile = source,
            Items = CloneArray(items),
            BarterScheme = CloneObject(barterScheme),
            LoyalLevelItems = CloneObject(loyalLevelItems),
            YatmSettings = CloneObject(yatmSettings)
        });

        logger.Info($"[YATM Offer Feed] Registered in-memory Tony offer {source}: {items.Count} item row(s), {rootIds.Count} root offer(s).");
        return true;
    }

    /// <summary>
    /// CommonLib-style addon entry point.
    ///
    /// Addons should call this before YetAnotherTraderMod runs its runtime loader at
    /// OnLoadOrder.PostDBModLoader + 4.
    ///
    /// Example:
    /// await yatmCommon.CustomTraderOfferServiceExtended.CreateCustomTraderOffers(
    ///     Assembly.GetExecutingAssembly(),
    ///     Path.Join("db", "CustomTraderOffers"));
    /// </summary>
    public Task CreateCustomTraderOffers(Assembly assembly, string? relativePath = null)
    {
        return CreateTonyTraderOffers(assembly, relativePath);
    }

    /// <summary>
    /// Loads Tony raw offer files from the supplied assembly.
    /// Default path: db/CustomTraderOffers
    /// </summary>
    public async Task CreateTonyTraderOffers(Assembly assembly, string? relativePath = null)
    {
        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        var assemblyLocation = GetModPath(assembly);
        var finalPath = ResolvePath(assemblyLocation, relativePath ?? DefaultOfferDir);

        await RegisterTonyTraderOffersFromPath(finalPath);
    }

    /// <summary>
    /// CommonLib-style path entry point for addons that already resolved a file/folder path.
    /// </summary>
    public Task CreateCustomTraderOffersFromPath(string path)
    {
        return RegisterTonyTraderOffersFromPath(path);
    }

    /// <summary>
    /// Loads a specific Tony raw offer file/folder. Useful for addon loaders that already resolved a path.
    /// </summary>
    public async Task RegisterTonyTraderOffersFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Tony trader offer path cannot be empty.", nameof(path));
        }

        await RegisterTonyTraderOfferFilesFromPath(Path.GetFullPath(path));
    }

    /// <summary>
    /// Auto-discovers addon offer files from every installed mod using db/YATM/CustomTraderOffers.
    /// This is what lets addons still work when Tony runtime is moved earlier in PostDBModLoader.
    /// </summary>
    public async Task CreateAddonTonyTraderOffers(Assembly hostAssembly, string? relativePath = null)
    {
        if (hostAssembly is null)
        {
            throw new ArgumentNullException(nameof(hostAssembly));
        }

        var hostModPath = GetModPath(hostAssembly);
        var addonPaths = ResolveAddonSourceFolders(hostModPath, relativePath ?? DefaultAddonOfferDir).ToList();

        if (addonPaths.Count == 0)
        {
            return;
        }

        foreach (var path in addonPaths)
        {
            await RegisterTonyTraderOfferFilesFromPath(path);
        }
    }

    /// <summary>
    /// Loads weapon preset files from Tony/addon mods into Globals.ItemPresets.
    /// Default path: db/CustomWeaponPresets
    /// </summary>
    public async Task CreateCustomWeaponPresets(Assembly assembly, string? relativePath = null)
    {
        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        var assemblyLocation = GetModPath(assembly);
        var finalPath = ResolvePath(assemblyLocation, relativePath ?? DefaultPresetDir);

        await RegisterCustomWeaponPresetsFromPath(finalPath);
    }

    /// <summary>
    /// Auto-discovers addon weapon presets from every installed mod using db/YATM/CustomWeaponPresets.
    /// </summary>
    public async Task CreateAddonCustomWeaponPresets(Assembly hostAssembly, string? relativePath = null)
    {
        if (hostAssembly is null)
        {
            throw new ArgumentNullException(nameof(hostAssembly));
        }

        var hostModPath = GetModPath(hostAssembly);
        var addonPaths = ResolveAddonSourceFolders(hostModPath, relativePath ?? DefaultAddonPresetDir).ToList();

        if (addonPaths.Count == 0)
        {
            return;
        }

        foreach (var path in addonPaths)
        {
            await RegisterCustomWeaponPresetsFromPath(path);
        }
    }

    private async Task RegisterTonyTraderOfferFilesFromPath(string finalPath)
    {
        var jsonFiles = GetJsonAndJsoncFilesFromPath(finalPath)
            .Select(Path.GetFullPath)
            .ToArray();

        if (jsonFiles.Length == 0)
        {
            return;
        }

        var registeredFileCount = 0;
        var registeredRootCount = 0;

        foreach (var file in jsonFiles)
        {
            if (!_loadedOfferFiles.Add(file))
            {
                YATMLogger.LogDebug($"[YATM Offer Feed] Offer file already registered, skipping duplicate read: {file}");
                continue;
            }

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

    private async Task RegisterCustomWeaponPresetsFromPath(string finalPath)
    {
        var jsonFiles = GetJsonAndJsoncFilesFromPath(finalPath)
            .Select(Path.GetFullPath)
            .ToArray();

        if (jsonFiles.Length == 0)
        {
            return;
        }

        var itemPresets = databaseServer.GetTables().Globals.ItemPresets;
        var loadedCount = 0;

        foreach (var file in jsonFiles)
        {
            if (!_loadedPresetFiles.Add(file))
            {
                YATMLogger.LogDebug($"[YATM Offer Feed] Preset file already loaded, skipping duplicate read: {file}");
                continue;
            }

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

    private static string ResolvePath(string rootPath, string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(rootPath, path);
    }

    private static List<string> ResolveAddonSourceFolders(string hostModPath, string relativePath)
    {
        var results = new List<string>();
        var modsRoot = FindModsRoot(hostModPath);
        if (modsRoot is null || !Directory.Exists(modsRoot))
        {
            return results;
        }

        List<string> modFolders;
        try
        {
            modFolders = Directory.EnumerateDirectories(modsRoot)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[YATM Offer Feed] Could not scan mods folder for addon offer folders: {ex.Message}");
            return results;
        }

        foreach (var modFolder in modFolders)
        {
            var resolved = ResolvePath(modFolder, relativePath);
            if (Directory.Exists(resolved) || File.Exists(resolved))
            {
                results.Add(Path.GetFullPath(resolved));
            }
        }

        return results;
    }

    private static string? FindModsRoot(string modPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(modPath));

        while (current is not null)
        {
            if (string.Equals(current.Name, "mods", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
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
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
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
