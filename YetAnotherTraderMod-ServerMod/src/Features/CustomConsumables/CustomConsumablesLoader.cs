using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using YetAnotherTraderMod.src;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.Features.CustomConsumables;

/// <summary>
/// Loads custom stim/med JSON files from db/CustomsComsumables/*.json.
/// This is a C# SPT 4.x port of the old ConsumablesGalore loader pattern.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 5)]
public sealed class CustomConsumablesLoader(
    DatabaseService databaseService,
    CustomItemService customItemService) : IOnLoad
{
    private const string RoublesTpl = "5449016a4bdc2d6f028b456f";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task OnLoad()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var modPath = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        var consumablesPath = Path.Combine(modPath, "db", "CustomConsumables");

        Load(consumablesPath);
        await Task.CompletedTask;
    }

    public void Load(string consumablesPath)
    {
        if (!Directory.Exists(consumablesPath))
        {
            YATMLogger.Log($"Custom consumables folder not found: {consumablesPath}");
            return;
        }

        var files = Directory.EnumerateFiles(consumablesPath, "*.json", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            if (YATMLogger.IsDebugEnabled)
            {
                YATMLogger.LogDebug($"No custom consumables found in {consumablesPath}");
            }
            return;
        }

        var tables = databaseService.GetTables();
        var loaded = 0;

        foreach (var file in files)
        {
            try
            {
                var definition = JsonSerializer.Deserialize<CustomConsumableDefinition>(File.ReadAllText(file), JsonOptions);
                if (definition is null)
                {
                    YATMLogger.Log($"[Tony] Skipped empty custom consumable file: {file}");
                    continue;
                }

                ValidateDefinition(definition, file);
                LoadOne(tables, definition, file);
                loaded++;
            }
            catch (Exception ex)
            {
                YATMLogger.Log($"[Tony] Failed to load custom consumable '{file}': {ex}");
            }
        }
        if (YATMLogger.IsDebugEnabled)
        {
            YATMLogger.LogDebug($"Loaded {loaded}/{files.Count} custom consumable JSON file(s)");
        }
    }

    private void LoadOne(object tables, CustomConsumableDefinition definition, string file)
    {
        var itemsDb = GetPath(tables, "Templates.Items") ?? databaseService.GetItems();
        var originItem = GetDictionaryValue(itemsDb, definition.CloneOrigin)
            ?? throw new InvalidOperationException($"cloneOrigin '{definition.CloneOrigin}' was not found in templates.items");

        var parentId = ReadString(originItem, "Parent")
            ?? ReadString(originItem, "_parent")
            ?? throw new InvalidOperationException($"Unable to read parent id from cloneOrigin '{definition.CloneOrigin}'");

        var handbookParentId = FindHandbookParentId(tables, definition.CloneOrigin) ?? parentId;
        var originFleaPrice = FindPrice(tables, definition.CloneOrigin) ?? 1;
        var originHandbookPrice = FindHandbookPrice(tables, definition.CloneOrigin) ?? originFleaPrice;

        var fleaPrice = ResolvePrice(definition.FleaPrice, originFleaPrice, originHandbookPrice);
        var handbookPrice = ResolvePrice(definition.HandBookPrice, originFleaPrice, originHandbookPrice);

        var overrideProperties = BuildOverrideProperties(originItem, definition);
        var locales = BuildLocales(definition);

        // If another file/mod already created this tpl, do not hard-fail the whole loader.
        // This is useful during testing when the same custom consumable exists in two folders.
        var alreadyExists = GetDictionaryValue(itemsDb, definition.Id) is not null;
        if (alreadyExists)
        {
            YATMLogger.Log($"[Tony] Custom consumable item id '{definition.Id}' already exists. Skipping clone creation, but still applying buffs/quests/spawns/trader data.");
        }
        else
        {
            var cloneDetails = new NewItemFromCloneDetails
            {
                ItemTplToClone = new MongoId(definition.CloneOrigin),
                NewId = definition.Id,
                ParentId = parentId,
                HandbookParentId = handbookParentId,
                FleaPriceRoubles = fleaPrice,
                HandbookPriceRoubles = handbookPrice,
                OverrideProperties = overrideProperties,
                Locales = locales
            };

            var result = customItemService.CreateItemFromClone(cloneDetails);
            if (result.Success != true)
            {
                var errors = result.Errors is null ? "unknown error" : string.Join("; ", result.Errors);
                if (errors.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    YATMLogger.Log($"[Tony] CreateItemFromClone says '{definition.Id}' already exists. Continuing with buffs/quests/spawns/trader data. Details: {errors}");
                }
                else
                {
                    throw new InvalidOperationException($"CreateItemFromClone failed for '{definition.Id}': {errors}");
                }
            }
        }

        LoadStimBuffs(tables, originItem, definition);

        if (definition.IncludeInSameQuestsAsOrigin)
        {
            AddToSameQuestsAsOrigin(tables, definition);
        }

        if (definition.AddSpawnsInSamePlacesAsOrigin)
        {
            AddSpawnsInSamePlacesAsOrigin(tables, definition);
        }

        if (definition.Trader is not null)
        {
            AddToTrader(tables, definition);
        }

        if (definition.Craft.HasValue)
        {
            AddCraft(tables, definition.Craft.Value);
        }

        if (YATMLogger.IsRealDebugEnabled)
        {
            YATMLogger.LogRealDebug($"Loaded custom consumable {definition.Id} from {Path.GetFileName(file)}");
        }
    }

    private TemplateItemProperties BuildOverrideProperties(object originItem, CustomConsumableDefinition definition)
    {
        var overrides = new TemplateItemProperties();

        // The new item must use its own StimulatorBuffs key so we can merge origin buffs + custom buffs.
        SetPropertyIfExists(overrides, "StimulatorBuffs", definition.Id);

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BackgroundColor"] = "BackgroundColor",
            ["effects_health"] = "EffectsHealth",
            ["effects_damage"] = "EffectsDamage",
            ["MaxResource"] = "MaxHpResource",
            ["MaxHpResource"] = "MaxHpResource",
            ["medUseTime"] = "MedUseTime",
            ["Prefab"] = "Prefab",
            ["UsePrefab"] = "UsePrefab",
            ["ItemSound"] = "ItemSound",
            ["CanSellOnRagfair"] = "CanSellOnRagfair"
        };

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cloneOrigin", "id", "fleaPrice", "handBookPrice", "includeInSameQuestsAsOrigin",
            "addSpawnsInSamePlacesAsOrigin", "spawnWeightComparedToOrigin", "inheritOriginBuffs",
            "Buffs", "locales", "trader", "craft", "overrideProperties"
        };

        if (definition.ExtraProperties is not null)
        {
            foreach (var (jsonKey, value) in definition.ExtraProperties)
            {
                if (reserved.Contains(jsonKey))
                {
                    continue;
                }

                ApplyOverrideProperty(overrides, aliases, jsonKey, value);
            }
        }

        if (definition.OverrideProperties is not null)
        {
            foreach (var (jsonKey, value) in definition.OverrideProperties)
            {
                ApplyOverrideProperty(overrides, aliases, jsonKey, value);
            }
        }

        // Force this again after JSON overrides so the custom stim always points at its own buff group.
        SetPropertyIfExists(overrides, "StimulatorBuffs", definition.Id);

        return overrides;
    }

    private static void ApplyOverrideProperty(object overrides, Dictionary<string, string> aliases, string jsonKey, JsonElement value)
    {
        var propertyName = aliases.TryGetValue(jsonKey, out var alias) ? alias : jsonKey;
        SetPropertyIfExists(overrides, propertyName, value);
    }

    private static Dictionary<string, LocaleDetails> BuildLocales(CustomConsumableDefinition definition)
    {
        return definition.Locales.ToDictionary(
            pair => pair.Key,
            pair => new LocaleDetails
            {
                Name = pair.Value.Name,
                ShortName = pair.Value.ShortName,
                Description = pair.Value.Description
            });
    }

    private void LoadStimBuffs(object tables, object originItem, CustomConsumableDefinition definition)
    {
        var buffDictionary = FindStimulatorBuffDictionary(tables);
        if (buffDictionary is null)
        {
            YATMLogger.Log($"[Tony] Could not find the stimulator buff dictionary. Checked Globals.Config/Globals.Configuration and DatabaseService.GetGlobals(). Stim buffs were not added for {definition.Id}");
            return;
        }

        var originBuffKey = ReadString(GetMember(originItem, "Properties"), "StimulatorBuffs")
            ?? ReadString(GetMember(originItem, "_props"), "StimulatorBuffs")
            ?? definition.CloneOrigin;

        object? originBuffList = null;
        if (definition.InheritOriginBuffs && !string.IsNullOrWhiteSpace(originBuffKey))
        {
            originBuffList = GetDictionaryValue(buffDictionary, originBuffKey);
        }

        var targetListType = originBuffList?.GetType() ?? GetFirstDictionaryValueType(buffDictionary) ?? typeof(List<Dictionary<string, object?>>);
        var mergedList = CreateListClone(targetListType, originBuffList);
        var elementType = GetListElementType(targetListType) ?? typeof(Dictionary<string, object?>);

        if (definition.Buffs is not null)
        {
            foreach (var buff in definition.Buffs)
            {
                var buffObject = JsonSerializer.Deserialize(buff.GetRawText(), elementType, JsonOptions);
                if (buffObject is not null && mergedList is IList list)
                {
                    list.Add(buffObject);
                }
            }
        }

        SetDictionaryValue(buffDictionary, definition.Id, mergedList);
    }

    private void AddToTrader(object tables, CustomConsumableDefinition definition)
    {
        var traderDefinition = definition.Trader!;
        var traders = GetMember(tables, "Traders") ?? GetMember(tables, "traders");
        var trader = GetDictionaryValue(traders, traderDefinition.TraderId);

        if (trader is null)
        {
            YATMLogger.Log($"[Tony] Trader '{traderDefinition.TraderId}' not found. Skipping trader offer for {definition.Id}");
            return;
        }

        var assort = GetMember(trader, "Assort") ?? GetMember(trader, "assort");
        if (assort is null)
        {
            YATMLogger.Log($"[Tony] Trader '{traderDefinition.TraderId}' has no assort. Skipping trader offer for {definition.Id}");
            return;
        }

        var items = GetMember(assort, "Items") ?? GetMember(assort, "items");
        var barterScheme = GetMember(assort, "BarterScheme") ?? GetMember(assort, "barter_scheme");
        var loyalLevelItems = GetMember(assort, "LoyalLevelItems") ?? GetMember(assort, "loyal_level_items");

        if (items is not IList itemList || barterScheme is null || loyalLevelItems is null)
        {
            YATMLogger.Log($"[Tony] Trader '{traderDefinition.TraderId}' assort shape was not recognized. Skipping trader offer for {definition.Id}");
            return;
        }

        var upd = new Dictionary<string, object?>
        {
            ["UnlimitedCount"] = traderDefinition.UnlimitedCount,
            ["StackObjectsCount"] = traderDefinition.AmountForSale,
            ["BuyRestrictionCurrent"] = 0
        };

        if (traderDefinition.BuyRestrictionMax.HasValue)
        {
            upd["BuyRestrictionMax"] = traderDefinition.BuyRestrictionMax.Value;
        }

        var assortmentId = string.IsNullOrWhiteSpace(traderDefinition.AssortmentId)
            ? definition.Id
            : traderDefinition.AssortmentId.Trim();

        var itemData = new Dictionary<string, object?>
        {
            ["_id"] = assortmentId,
            ["_tpl"] = definition.Id,
            ["parentId"] = "hideout",
            ["slotId"] = "hideout",
            ["upd"] = upd
        };

        if (!itemList.Cast<object>().Any(item => string.Equals(ReadString(item, "Id") ?? ReadString(item, "_id"), assortmentId, StringComparison.OrdinalIgnoreCase)))
        {
            AddObjectToList(itemList, itemData);
        }
        else
        {
            YATMLogger.Log($"[Tony] Trader offer item row already exists for {assortmentId}; updating barter/loyalty only.");
        }

        object barterData;
        var barterSchemeJson = traderDefinition.BarterScheme ?? traderDefinition.BarterSchemeSnake;
        if (barterSchemeJson.HasValue)
        {
            barterData = JsonSerializer.Deserialize<object>(barterSchemeJson.Value.GetRawText(), JsonOptions)!;
        }
        else
        {
            var currency = string.IsNullOrWhiteSpace(traderDefinition.CurrencyTpl) ? RoublesTpl : traderDefinition.CurrencyTpl;
            barterData = new List<List<Dictionary<string, object?>>>
            {
                new()
                {
                    new Dictionary<string, object?>
                    {
                        ["count"] = traderDefinition.Price,
                        ["_tpl"] = currency
                    }
                }
            };
        }

        SetDictionaryValue(barterScheme, assortmentId, barterData);
        SetDictionaryValue(loyalLevelItems, assortmentId, traderDefinition.LoyaltyReq);
    }

    private void AddToSameQuestsAsOrigin(object tables, CustomConsumableDefinition definition)
    {
        var quests = GetPath(tables, "Templates.Quests") ?? GetMember(tables, "Quests") ?? GetMember(tables, "quests");
        if (quests is null)
        {
            YATMLogger.Log($"[Tony] Could not find quest database. Quest target injection skipped for {definition.Id}");
            return;
        }

        foreach (var quest in EnumerateDictionaryValues(quests))
        {
            var finishConditions = GetPath(quest, "Conditions.AvailableForFinish")
                ?? GetPath(quest, "conditions.AvailableForFinish");

            if (finishConditions is not IEnumerable conditions)
            {
                continue;
            }

            foreach (var condition in conditions)
            {
                TryInjectQuestConditionTarget(condition, definition.CloneOrigin, definition.Id);
            }
        }
    }

    private bool TryInjectQuestConditionTarget(object? condition, string originTpl, string newTpl)
    {
        if (condition is null)
        {
            return false;
        }

        var conditionType = ReadString(condition, "ConditionType") ?? ReadString(condition, "conditionType");
        var target = GetMember(condition, "Target") ?? GetMember(condition, "target");

        var injected = false;
        if ((string.Equals(conditionType, "HandoverItem", StringComparison.OrdinalIgnoreCase)
             || string.Equals(conditionType, "FindItem", StringComparison.OrdinalIgnoreCase))
            && target is IList targetList
            && ListContainsString(targetList, originTpl)
            && !ListContainsString(targetList, newTpl))
        {
            AddValueToList(targetList, newTpl);
            injected = true;
        }

        // Recurse through common nested condition containers, e.g. CounterCreator.counter.conditions.
        foreach (var nestedName in new[] { "Conditions", "conditions" })
        {
            var nestedObject = GetMember(condition, nestedName);
            if (nestedObject is IEnumerable nestedConditions && nestedObject is not string)
            {
                foreach (var nested in nestedConditions)
                {
                    injected |= TryInjectQuestConditionTarget(nested, originTpl, newTpl);
                }
            }
        }

        var counterConditions = GetPath(condition, "Counter.Conditions")
            ?? GetPath(condition, "counter.conditions");
        if (counterConditions is IEnumerable counterList && counterConditions is not string)
        {
            foreach (var nested in counterList)
            {
                injected |= TryInjectQuestConditionTarget(nested, originTpl, newTpl);
            }
        }

        return injected;
    }

    private void AddSpawnsInSamePlacesAsOrigin(object tables, CustomConsumableDefinition definition)
    {
        var locations = GetMember(tables, "Locations") ?? GetMember(tables, "locations");
        if (locations is null)
        {
            YATMLogger.Log($"[Tony] Could not find locations database. Spawn injection skipped for {definition.Id}");
            return;
        }

        var validMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bigmap", "woods", "factory4day", "factory4night", "interchange", "laboratory",
            "lighthouse", "rezervbase", "shoreline", "tarkovstreets", "sandbox", "sandboxhigh"
        };

        foreach (var (mapName, mapData) in EnumerateDictionaryEntries(locations))
        {
            if (!validMaps.Contains(NormalizeMapKey(mapName)))
            {
                continue;
            }

            AddLooseLootSpawns(mapData, definition);
            AddStaticLootSpawns(mapData, definition);
        }
    }

    private void AddLooseLootSpawns(object mapData, CustomConsumableDefinition definition)
    {
        var spawnPoints = GetPath(mapData, "LooseLoot.Spawnpoints") ?? GetPath(mapData, "looseLoot.spawnpoints");
        if (spawnPoints is not IEnumerable points)
        {
            return;
        }

        foreach (var point in points)
        {
            var templateItems = GetPath(point, "Template.Items") ?? GetPath(point, "template.Items");
            var distribution = GetMember(point, "ItemDistribution") ?? GetMember(point, "itemDistribution");

            if (templateItems is not IList itemList || distribution is not IList distributionList)
            {
                continue;
            }

            var matches = itemList.Cast<object>()
                .Where(item => string.Equals(ReadString(item, "Tpl") ?? ReadString(item, "_tpl"), definition.CloneOrigin, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var originSpawnItem in matches)
            {
                var originItemId = ReadString(originSpawnItem, "Id") ?? ReadString(originSpawnItem, "_id");
                if (string.IsNullOrWhiteSpace(originItemId))
                {
                    continue;
                }

                var originProbability = FindLooseLootProbability(distributionList, originItemId) ?? 1;
                var composedKey = $"{definition.Id}_{originItemId}_composedkey";

                if (!itemList.Cast<object>().Any(x => string.Equals(ReadString(x, "Id") ?? ReadString(x, "_id"), composedKey, StringComparison.OrdinalIgnoreCase)))
                {
                    AddObjectToList(itemList, new Dictionary<string, object?>
                    {
                        ["_id"] = composedKey,
                        ["_tpl"] = definition.Id
                    });
                }

                if (!distributionList.Cast<object>().Any(entry =>
                    string.Equals(ReadString(GetMember(entry, "ComposedKey") ?? GetMember(entry, "composedKey"), "Key")
                                  ?? ReadString(GetMember(entry, "ComposedKey") ?? GetMember(entry, "composedKey"), "key"),
                        composedKey,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    AddObjectToList(distributionList, new Dictionary<string, object?>
                    {
                        ["composedKey"] = new Dictionary<string, object?> { ["key"] = composedKey },
                        ["relativeProbability"] = Math.Max((int)Math.Round(originProbability * definition.SpawnWeightComparedToOrigin), 1)
                    });
                }
            }
        }
    }

    private void AddStaticLootSpawns(object mapData, CustomConsumableDefinition definition)
    {
        var staticLoot = GetMember(mapData, "StaticLoot") ?? GetMember(mapData, "staticLoot");
        if (staticLoot is null)
        {
            return;
        }

        foreach (var (_, container) in EnumerateDictionaryEntries(staticLoot))
        {
            var distribution = GetMember(container, "ItemDistribution") ?? GetMember(container, "itemDistribution");
            if (distribution is not IList distributionList)
            {
                continue;
            }

            var originEntry = distributionList.Cast<object>().FirstOrDefault(entry =>
                string.Equals(ReadString(entry, "Tpl") ?? ReadString(entry, "tpl"), definition.CloneOrigin, StringComparison.OrdinalIgnoreCase));

            if (originEntry is null)
            {
                continue;
            }

            if (distributionList.Cast<object>().Any(entry =>
                    string.Equals(ReadString(entry, "Tpl") ?? ReadString(entry, "tpl"), definition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var originProbability = ReadDouble(originEntry, "RelativeProbability")
                ?? ReadDouble(originEntry, "relativeProbability")
                ?? 1;

            AddObjectToList(distributionList, new Dictionary<string, object?>
            {
                ["tpl"] = definition.Id,
                ["relativeProbability"] = Math.Max((int)Math.Round(originProbability * definition.SpawnWeightComparedToOrigin), 1)
            });
        }
    }

    private void AddCraft(object tables, JsonElement craft)
    {
        var recipes = GetPath(tables, "Hideout.Production.Recipes")
            ?? GetPath(tables, "hideout.production.recipes");

        if (recipes is not IList recipeList)
        {
            YATMLogger.Log("[Tony] Could not find hideout.production.recipes. Craft was skipped.");
            return;
        }

        var craftObject = JsonSerializer.Deserialize<object>(craft.GetRawText(), JsonOptions)!;
        AddObjectToList(recipeList, craftObject);
    }

    private static void ValidateDefinition(CustomConsumableDefinition definition, string file)
    {
        if (string.IsNullOrWhiteSpace(definition.CloneOrigin))
        {
            throw new InvalidOperationException($"Missing cloneOrigin in {file}");
        }

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new InvalidOperationException($"Missing id in {file}");
        }

        if (definition.Locales.Count == 0)
        {
            throw new InvalidOperationException($"Missing locales in {file}");
        }
    }

    private static double ResolvePrice(JsonElement element, double originFleaPrice, double originHandbookPrice)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return originFleaPrice;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.Equals(text, "asOriginal", StringComparison.OrdinalIgnoreCase))
            {
                return originHandbookPrice;
            }

            if (double.TryParse(text, out var parsed))
            {
                return parsed <= 10 ? Math.Round(originFleaPrice * parsed) : parsed;
            }

            return originFleaPrice;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value <= 10 ? Math.Round(originFleaPrice * value) : value;
        }

        return originFleaPrice;
    }

    private static object? GetPath(object? source, string path)
    {
        var current = source;
        foreach (var part in path.Split('.'))
        {
            current = GetMember(current, part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static object? GetMember(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        if (source is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
        }

        var type = source.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        return type.GetProperty(name, flags)?.GetValue(source)
               ?? type.GetField(name, flags)?.GetValue(source);
    }

    private static string? ReadString(object? source, string name)
    {
        var value = GetMember(source, name);
        return value?.ToString();
    }

    private static double? ReadDouble(object? source, string name)
    {
        var value = GetMember(source, name);
        return ReadNumericValue(value);
    }

    private static object? GetDictionaryValue(object? dictionaryObject, string key)
    {
        if (dictionaryObject is null)
        {
            return null;
        }

        if (dictionaryObject is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
        }

        var tryGetValue = dictionaryObject.GetType().GetMethods()
            .FirstOrDefault(method => method.Name == "TryGetValue" && method.GetParameters().Length == 2);

        if (tryGetValue is null)
        {
            return null;
        }

        var parameters = tryGetValue.GetParameters();
        var convertedKey = ConvertValue(key, parameters[0].ParameterType);
        var outValue = parameters[1].ParameterType.GetElementType() is { } outType
            ? CreateDefault(outType)
            : null;

        var args = new[] { convertedKey, outValue };
        var found = (bool)(tryGetValue.Invoke(dictionaryObject, args) ?? false);
        return found ? args[1] : null;
    }

    private static void SetDictionaryValue(object dictionaryObject, string key, object? value)
    {
        if (dictionaryObject is IDictionary dictionary)
        {
            var dictionaryKeyType = dictionary.GetType().GetGenericArguments().ElementAtOrDefault(0) ?? typeof(string);
            var dictionaryValueType = dictionary.GetType().GetGenericArguments().ElementAtOrDefault(1) ?? value?.GetType() ?? typeof(object);
            dictionary[ConvertValue(key, dictionaryKeyType)] = ConvertValue(value, dictionaryValueType);
            return;
        }

        var indexer = dictionaryObject.GetType().GetProperty("Item");
        if (indexer is null)
        {
            return;
        }

        var indexerKeyType = indexer.GetIndexParameters().First().ParameterType;
        var indexerValueType = indexer.PropertyType;
        indexer.SetValue(dictionaryObject, ConvertValue(value, indexerValueType), new[] { ConvertValue(key, indexerKeyType) });
    }

    private static IEnumerable<object> EnumerateDictionaryValues(object dictionaryObject)
    {
        if (dictionaryObject is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not null)
                {
                    yield return entry.Value;
                }
            }

            yield break;
        }

        var values = GetMember(dictionaryObject, "Values") as IEnumerable;
        if (values is null)
        {
            yield break;
        }

        foreach (var value in values)
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<(string Key, object Value)> EnumerateDictionaryEntries(object dictionaryObject)
    {
        if (dictionaryObject is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not null)
                {
                    yield return (entry.Key?.ToString() ?? string.Empty, entry.Value);
                }
            }

            yield break;
        }

        // Some SPT C# models, like Locations, are typed containers with public properties
        // instead of dictionaries. Enumerate those properties instead of casting to IEnumerable.
        var type = dictionaryObject.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

        foreach (var property in type.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var value = property.GetValue(dictionaryObject);
            if (value is not null)
            {
                yield return (property.Name, value);
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            var value = field.GetValue(dictionaryObject);
            if (value is not null)
            {
                yield return (field.Name, value);
            }
        }

        if (dictionaryObject is IEnumerable enumerable and not string)
        {
            foreach (var entry in enumerable)
            {
                var key = ReadString(entry, "Key") ?? string.Empty;
                var value = GetMember(entry, "Value");
                if (value is not null)
                {
                    yield return (key, value);
                }
            }
        }
    }


    private object? FindStimulatorBuffDictionary(object tables)
    {
        var directCandidates = new[]
        {
            "Globals.Config.Health.Effects.Stimulator.Buffs",
            "Globals.Configuration.Health.Effects.Stimulator.Buffs",
            "globals.config.Health.Effects.Stimulator.Buffs",
            "globals.configuration.Health.Effects.Stimulator.Buffs"
        };

        foreach (var path in directCandidates)
        {
            var value = GetPath(tables, path);
            if (value is not null)
            {
                return value;
            }
        }

        var globals = TryCallNoArg(databaseService, "GetGlobals")
                      ?? GetMember(tables, "Globals")
                      ?? GetMember(tables, "globals");

        if (globals is null)
        {
            return null;
        }

        var config = GetFirstMember(globals, "Config", "Configuration", "config", "configuration") ?? globals;
        var health = GetFirstMember(config, "Health", "health");
        var effects = GetFirstMember(health, "Effects", "effects");
        var stimulator = GetFirstMember(effects, "Stimulator", "stimulator");
        return GetFirstMember(stimulator, "Buffs", "buffs");
    }

    private static object? TryCallNoArg(object source, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var method = source.GetType().GetMethods(flags).FirstOrDefault(method =>
            string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase)
            && method.GetParameters().Length == 0);

        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(source, null);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetFirstMember(object? source, params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var value = GetMember(source, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeMapKey(string mapName)
    {
        return mapName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static double? FindPrice(object tables, string tpl)
    {
        var prices = GetPath(tables, "Templates.Prices");
        var price = GetDictionaryValue(prices, tpl);
        return ReadNumericValue(price);
    }

    private static string? FindHandbookParentId(object tables, string tpl)
    {
        var handbookItems = GetPath(tables, "Templates.Handbook.Items") as IEnumerable;
        if (handbookItems is null)
        {
            return null;
        }

        foreach (var item in handbookItems)
        {
            var id = ReadString(item, "Id") ?? ReadString(item, "ID");
            if (string.Equals(id, tpl, StringComparison.OrdinalIgnoreCase))
            {
                return ReadString(item, "ParentId") ?? ReadString(item, "ParentID");
            }
        }

        return null;
    }

    private static double? FindHandbookPrice(object tables, string tpl)
    {
        var handbookItems = GetPath(tables, "Templates.Handbook.Items") as IEnumerable;
        if (handbookItems is null)
        {
            return null;
        }

        foreach (var item in handbookItems)
        {
            var id = ReadString(item, "Id") ?? ReadString(item, "ID");
            if (string.Equals(id, tpl, StringComparison.OrdinalIgnoreCase))
            {
                return ReadDouble(item, "Price");
            }
        }

        return null;
    }

    private static double? FindLooseLootProbability(IList distributionList, string originItemId)
    {
        foreach (var entry in distributionList.Cast<object>())
        {
            var key = ReadString(GetMember(entry, "ComposedKey") ?? GetMember(entry, "composedKey"), "Key")
                      ?? ReadString(GetMember(entry, "ComposedKey") ?? GetMember(entry, "composedKey"), "key");
            if (string.Equals(key, originItemId, StringComparison.OrdinalIgnoreCase))
            {
                return ReadDouble(entry, "RelativeProbability") ?? ReadDouble(entry, "relativeProbability");
            }
        }

        return null;
    }

    private static void AddObjectToList(IList list, object data)
    {
        var elementType = GetListElementType(list.GetType()) ?? typeof(object);
        list.Add(ConvertValue(data, elementType));
    }

    private static bool ListContainsString(IList list, string value)
    {
        return list.Cast<object?>().Any(item => string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddValueToList(IList list, string value)
    {
        var elementType = GetListElementType(list.GetType()) ?? typeof(string);
        list.Add(ConvertValue(value, elementType));
    }

    private static Type? GetFirstDictionaryValueType(object dictionaryObject)
    {
        if (dictionaryObject is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not null)
                {
                    return entry.Value.GetType();
                }
            }
        }

        return dictionaryObject.GetType().GetGenericArguments().ElementAtOrDefault(1);
    }

    private static object CreateListClone(Type listType, object? source)
    {
        if (source is null)
        {
            return Activator.CreateInstance(listType) ?? new List<Dictionary<string, object?>>();
        }

        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize(json, listType, JsonOptions)
               ?? Activator.CreateInstance(listType)
               ?? new List<Dictionary<string, object?>>();
    }

    private static Type? GetListElementType(Type listType)
    {
        if (listType.IsArray)
        {
            return listType.GetElementType();
        }

        if (listType.IsGenericType && listType.GetGenericArguments().Length == 1)
        {
            return listType.GetGenericArguments()[0];
        }

        return listType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
        {
            return CreateDefault(targetType);
        }

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType is not null)
        {
            targetType = nullableType;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(object))
        {
            return value is JsonElement objectElement ? JsonElementToPlainObject(objectElement) : value;
        }

        if (value is JsonElement element)
        {
            return ConvertJsonElement(element, targetType);
        }

        if (targetType == typeof(string))
        {
            return value.ToString();
        }

        if (targetType == typeof(MongoId))
        {
            return new MongoId(value.ToString());
        }

        if (targetType.IsEnum)
        {
            return ConvertEnum(value, targetType);
        }

        if (TryConvertPrimitive(value, targetType, out var primitiveValue))
        {
            return primitiveValue;
        }

        if (TryConvertToDictionary(value, targetType, out var dictionaryValue))
        {
            return dictionaryValue;
        }

        if (TryConvertToList(value, targetType, out var listValue))
        {
            return listValue;
        }

        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            var deserialized = JsonSerializer.Deserialize(json, targetType, JsonOptions);
            if (deserialized is not null)
            {
                return deserialized;
            }
        }
        catch
        {
            // Fall through to the manual object mapper below.
        }

        if (value is IDictionary dictionary)
        {
            var mapped = ConvertDictionaryToObject(dictionary, targetType);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        // Only use ChangeType for primitive/convertible values. Calling it on SPT models,
        // dictionaries, or lists is what causes: "Object must implement IConvertible."
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
        {
            return Convert.ChangeType(value, targetType);
        }

        throw new InvalidOperationException($"Cannot convert value of type '{value.GetType().FullName}' to '{targetType.FullName}'.");
    }

    private static object? ConvertJsonElement(JsonElement element, Type targetType)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return CreateDefault(targetType);
        }

        if (targetType == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        if (targetType == typeof(MongoId))
        {
            return new MongoId(element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString());
        }

        if (targetType.IsEnum)
        {
            return ConvertEnum(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString(), targetType);
        }

        if (TryConvertPrimitive(element, targetType, out var primitiveValue))
        {
            return primitiveValue;
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize(element.GetRawText(), targetType, JsonOptions);
            if (deserialized is not null)
            {
                return deserialized;
            }
        }
        catch
        {
            // Fall through to manual mapping. This handles SPT classes whose C# property
            // names do not directly match raw EFT JSON keys like _id and _tpl.
        }

        if (element.ValueKind == JsonValueKind.Array && TryConvertToList(element, targetType, out var listValue))
        {
            return listValue;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText(), JsonOptions);
            if (dictionary is not null)
            {
                if (TryConvertToDictionary(dictionary, targetType, out var dictionaryValue))
                {
                    return dictionaryValue;
                }

                var mapped = ConvertDictionaryToObject(dictionary, targetType);
                if (mapped is not null)
                {
                    return mapped;
                }
            }
        }

        throw new InvalidOperationException($"Cannot convert JSON value '{element.ValueKind}' to '{targetType.FullName}'.");
    }

    private static object? JsonElementToPlainObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => JsonElementToPlainObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToPlainObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryConvertPrimitive(object value, Type targetType, out object? converted)
    {
        converted = null;

        try
        {
            if (value is JsonElement element)
            {
                if (targetType == typeof(bool) && (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
                {
                    converted = element.GetBoolean();
                    return true;
                }

                if (targetType == typeof(int) && element.TryGetInt32(out var intValue))
                {
                    converted = intValue;
                    return true;
                }

                if (targetType == typeof(long) && element.TryGetInt64(out var longValue))
                {
                    converted = longValue;
                    return true;
                }

                if (targetType == typeof(float) && element.TryGetSingle(out var floatValue))
                {
                    converted = floatValue;
                    return true;
                }

                if (targetType == typeof(double) && element.TryGetDouble(out var doubleValue))
                {
                    converted = doubleValue;
                    return true;
                }

                if (targetType == typeof(decimal) && element.TryGetDecimal(out var decimalValue))
                {
                    converted = decimalValue;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    return TryConvertPrimitive(element.GetString()!, targetType, out converted);
                }

                return false;
            }

            if (targetType == typeof(bool) || targetType == typeof(byte) || targetType == typeof(short)
                || targetType == typeof(int) || targetType == typeof(long) || targetType == typeof(float)
                || targetType == typeof(double) || targetType == typeof(decimal))
            {
                if (value is IConvertible)
                {
                    converted = Convert.ChangeType(value, targetType);
                    return true;
                }
            }
        }
        catch
        {
            converted = null;
            return false;
        }

        return false;
    }

    private static object ConvertEnum(object value, Type enumType)
    {
        if (value is JsonElement element)
        {
            value = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString();
        }

        if (value is string text)
        {
            return Enum.Parse(enumType, text, true);
        }

        var underlying = Enum.GetUnderlyingType(enumType);
        var numeric = Convert.ChangeType(value, underlying);
        return Enum.ToObject(enumType, numeric);
    }

    private static bool TryConvertToList(object value, Type targetType, out object? converted)
    {
        converted = null;

        var elementType = GetListElementType(targetType);
        if (elementType is null || !typeof(IList).IsAssignableFrom(targetType))
        {
            return false;
        }

        IEnumerable? enumerable = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray().ToList(),
            IEnumerable candidate when candidate is not string => candidate,
            _ => null
        };

        if (enumerable is null)
        {
            return false;
        }

        var list = Activator.CreateInstance(targetType) as IList;
        if (list is null)
        {
            var fallbackType = typeof(List<>).MakeGenericType(elementType);
            list = Activator.CreateInstance(fallbackType) as IList;
        }

        if (list is null)
        {
            return false;
        }

        foreach (var item in enumerable)
        {
            list.Add(ConvertValue(item, elementType));
        }

        converted = list;
        return true;
    }

    private static bool TryConvertToDictionary(object value, Type targetType, out object? converted)
    {
        converted = null;

        if (!typeof(IDictionary).IsAssignableFrom(targetType))
        {
            return false;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText(), JsonOptions)!;
        }

        if (value is not IDictionary sourceDictionary)
        {
            return false;
        }

        var genericArgs = targetType.GetGenericArguments();
        var targetDictionaryKeyType = genericArgs.ElementAtOrDefault(0) ?? typeof(string);
        var targetDictionaryValueType = genericArgs.ElementAtOrDefault(1) ?? typeof(object);
        var dictionary = Activator.CreateInstance(targetType) as IDictionary;

        if (dictionary is null)
        {
            var fallbackType = typeof(Dictionary<,>).MakeGenericType(targetDictionaryKeyType, targetDictionaryValueType);
            dictionary = Activator.CreateInstance(fallbackType) as IDictionary;
        }

        if (dictionary is null)
        {
            return false;
        }

        foreach (DictionaryEntry entry in sourceDictionary)
        {
            dictionary[ConvertValue(entry.Key, targetDictionaryKeyType)] = ConvertValue(entry.Value, targetDictionaryValueType);
        }

        converted = dictionary;
        return true;
    }

    private static object? ConvertDictionaryToObject(IDictionary dictionary, Type targetType)
    {
        if (targetType.IsAbstract || targetType.IsInterface || targetType == typeof(string) || targetType.IsPrimitive || targetType.IsEnum)
        {
            return null;
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(targetType) ?? throw new InvalidOperationException();
        }
        catch
        {
            return null;
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            SetMemberIfExists(instance, key, entry.Value);
        }

        return instance;
    }

    private static object? CreateDefault(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static double? ReadNumericValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonDouble))
            {
                return jsonDouble;
            }

            if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var jsonStringDouble))
            {
                return jsonStringDouble;
            }

            return null;
        }

        if (value is double d) return d;
        if (value is float f) return f;
        if (value is decimal m) return (double)m;
        if (value is int i) return i;
        if (value is long l) return l;

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static void SetPropertyIfExists(object target, string propertyName, object value)
    {
        SetMemberIfExists(target, propertyName, value);
    }

    private static bool SetMemberIfExists(object target, string sourceName, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var type = target.GetType();

        var property = FindWritableProperty(type, sourceName, flags);
        if (property is not null)
        {
            property.SetValue(target, ConvertValue(value, property.PropertyType));
            return true;
        }

        var field = FindWritableField(type, sourceName, flags);
        if (field is not null)
        {
            field.SetValue(target, ConvertValue(value, field.FieldType));
            return true;
        }

        return false;
    }

    private static PropertyInfo? FindWritableProperty(Type type, string sourceName, BindingFlags flags)
    {
        var sourceCandidates = GetNameCandidates(sourceName).Select(NormalizeJsonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return type.GetProperties(flags)
            .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0)
            .FirstOrDefault(property =>
            {
                var names = GetNameCandidates(property.Name).ToList();
                var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (!string.IsNullOrWhiteSpace(jsonName))
                {
                    names.Add(jsonName);
                }

                return names.Select(NormalizeJsonName).Any(sourceCandidates.Contains);
            });
    }

    private static FieldInfo? FindWritableField(Type type, string sourceName, BindingFlags flags)
    {
        var sourceCandidates = GetNameCandidates(sourceName).Select(NormalizeJsonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return type.GetFields(flags)
            .Where(field => !field.IsInitOnly)
            .FirstOrDefault(field =>
            {
                var names = GetNameCandidates(field.Name).ToList();
                var jsonName = field.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (!string.IsNullOrWhiteSpace(jsonName))
                {
                    names.Add(jsonName);
                }

                return names.Select(NormalizeJsonName).Any(sourceCandidates.Contains);
            });
    }

    private static IEnumerable<string> GetNameCandidates(string name)
    {
        yield return name;

        var trimmed = name.TrimStart('_');
        if (!string.Equals(trimmed, name, StringComparison.Ordinal))
        {
            yield return trimmed;
        }

        switch (NormalizeJsonName(name))
        {
            case "id":
                yield return "Id";
                yield return "ID";
                yield return "_id";
                break;
            case "tpl":
                yield return "Tpl";
                yield return "Template";
                yield return "TemplateId";
                yield return "TemplateID";
                yield return "_tpl";
                break;
            case "parentid":
                yield return "ParentId";
                yield return "ParentID";
                yield return "parentId";
                break;
            case "slotid":
                yield return "SlotId";
                yield return "SlotID";
                yield return "slotId";
                break;
            case "upd":
                yield return "Upd";
                yield return "Update";
                break;
            case "composedkey":
                yield return "ComposedKey";
                yield return "composedKey";
                break;
            case "relativeprobability":
                yield return "RelativeProbability";
                yield return "relativeProbability";
                break;
            case "count":
                yield return "Count";
                yield return "count";
                break;
            case "key":
                yield return "Key";
                yield return "key";
                break;
        }
    }

    private static string NormalizeJsonName(string name)
    {
        return new string(name.Where(character => character != '_' && character != '-' && character != '.').ToArray()).ToLowerInvariant();
    }
}
