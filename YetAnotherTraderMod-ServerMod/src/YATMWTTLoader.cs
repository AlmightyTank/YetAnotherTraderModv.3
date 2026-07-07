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
    YATMSlotCopyBootstrap yatmSlotCopyBootstrap) : IOnLoad
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
            Path.Join("db", "CustomItems"),
            Path.Join("db", "CustomAmmo"),
            Path.Join("db", "CustomQuestItems"),
            Path.Join("db", "CustomWeapons"),
        };

        var slotCopyPaths = new[]
        {
            Path.Join("db", "CustomWeapons")
        };

        var presetPath = Path.Join("db", "CustomWeaponPresets");

        // 1. WTT creates custom ammo/parts/weapons
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading WTT custom items...");

        foreach (var path in itemPaths)
        {
            await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly, path);
        }

        // 2. YATM slot clone helper copies missing slots onto custom weapons
        YATMLogger.LogRealDebug("[CustomContentLoader] Processing YATM slot copies...");

        foreach (var path in slotCopyPaths)
        {
            await yatmSlotCopyBootstrap.ProcessSlotCopies(assembly, path);
        }

        // 3. WTT creates weapon presets
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading custom weapon presets...");

        await wttCommon.CustomWeaponPresetService.CreateCustomWeaponPresets(assembly, presetPath);

        // 4. WTT loads hideout recipes, locales, loot spawns
        YATMLogger.LogRealDebug("[CustomContentLoader] Loading hideout recipes, locales, and loot spawns...");

        await wttCommon.CustomHideoutRecipeService.CreateHideoutRecipes(assembly);
        await wttCommon.CustomLocaleService.CreateCustomLocales(assembly);
        await wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);

        // 5. Custom quests are loaded later by YATMQuestLoader after Tony has been added to the DB.
        // This prevents WTT quest-assort import from running before trader 66a0f6b2c4d8e90123456789 exists.

        YATMLogger.Log("[CustomContentLoader] Finished loading all custom content.");
    }

}