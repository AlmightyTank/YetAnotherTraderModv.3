using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using WTTServerCommonLib;
using YetAnotherTraderMod.src.Services.ItemHelpers;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public sealed class YATMWTTLoader(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    YATMSlotCopyBootstrap yatmSlotCopyBootstrap,
    YATMWeaponBuildService weaponBuildService,
    YATMWttPresetOfferBridgeService wttPresetOfferBridgeService,
    YATMDeferredQuestItemService deferredQuestItemService) : IOnLoad
{
    public async Task OnLoad()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var modPath = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve mod path.");

        YATMLogger.Init(modPath);
        YATMLogger.Log("[CustomContentLoader] Starting custom content load...");

        var dbPath = Path.Combine(modPath, "db");

        YATMLogger.LogRealDebug($"[CustomContentLoader] Mod path: {modPath}");
        YATMLogger.LogRealDebug($"[CustomContentLoader] DB path: {dbPath}");

        if (!Directory.Exists(dbPath))
        {
            YATMLogger.Log($"[CustomContentLoader] DB folder not found: {dbPath}");
            return;
        }

        var itemPaths = new[]
        {
            Path.Join("db", "CustomItems", "Ammo"),
            Path.Join("db", "CustomItems", "Cases"),
            Path.Join("db", "CustomItems", "HeadWear"),
            Path.Join("db", "CustomItems", "Armor", "Builtins"),
            Path.Join("db", "CustomItems", "Armor"),
            Path.Join("db", "CustomWeapons", "Parts"),
            Path.Join("db", "CustomQuests", "Items"),
            Path.Join("db", "CustomWeapons", "Info"),
        };

        var presetPath = Path.Join("db", "CustomWeapons", "Presets");
        var weaponBuildPath = Path.Join("db", "CustomWeapons", "Builds");

        // 1. WTT creates custom ammo/parts/weapons
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading WTT custom items...");

        foreach (var path in itemPaths)
        {
            await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly, path);
        }

        // 2. YATM slot clone helper copies requested slots onto custom items
        YATMLogger.LogRealDebug("[CustomContentLoader] Processing YATM slot copies...");

        // Scan every custom-item folder. Files without copySlot=true are ignored.
        // This is required for parts such as barrels and receivers in db/CustomParts.
        foreach (var path in itemPaths)
        {
            await yatmSlotCopyBootstrap.ProcessSlotCopies(assembly, path);
        }

        // 3. Load reusable non-preset full weapon trees.
        // These are used by addToTraders + weaponBuildTraders and rewardType=Weapon.
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading reusable non-preset weapon builds...");
        await weaponBuildService.LoadWeaponBuilds(assembly, weaponBuildPath);

        // 4. WTT creates normal global weapon presets.
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading custom weapon presets...");

        await wttCommon.CustomWeaponPresetService.CreateCustomWeaponPresets(assembly, presetPath);

        // 5. Convert addToTraders-enabled Tony presetTraders/weaponBuildTraders entries into YATM offer-feed rows.
        // This runs after presets exist and before Tony builds the final runtime assort.
        YATMLogger.LogRealDebug("[CustomContentLoader] Bridging WTT Tony preset/weapon-build offers into Tony's early assort and YATM offer feed...");

        // Item files are the normal home for presetTraders/weaponBuildTraders.
        // The build folder is also scanned so exported build definitions may carry
        // addToTraders + weaponBuildTraders directly when desired.
        var traderBridgePaths = itemPaths
            .Append(weaponBuildPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await wttPresetOfferBridgeService.RegisterTonyPresetOffers(assembly, traderBridgePaths);

        // 6. Register quest-assort/reward extensions now, but do not apply them yet.
        // They are flushed by YATMTraderRuntimeService after the final Tony assort and quests exist.
        YATMLogger.LogRealDebug("[CustomContentLoader] Registering deferred item quest data...");
        await deferredQuestItemService.RegisterFromItemFolders(assembly, itemPaths);

        // 7. WTT loads hideout recipes, locales, loot spawns
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading hideout recipes, locales, and loot spawns...");

        await wttCommon.CustomHideoutRecipeService.CreateHideoutRecipes(assembly);
        await wttCommon.CustomLocaleService.CreateCustomLocales(assembly);
        await wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);

        // 8. Custom quests are loaded later by YATMTraderRuntimeService after Tony has been added to the DB.
        // This prevents quest-assort processing from running before trader 66a0f6b2c4d8e90123456789 has its final assort.

        YATMLogger.Log("[CustomContentLoader] Finished loading all custom content.");
    }

}