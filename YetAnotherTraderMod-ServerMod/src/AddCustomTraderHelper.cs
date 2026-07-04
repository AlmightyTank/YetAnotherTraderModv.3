using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YetAnotherTraderMod.src;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class AddCustomTraderHelper(
    ISptLogger<AddCustomTraderHelper> logger,
    ICloner cloner,
    DatabaseService databaseService,
    LocaleService localeService)
{
    public void LogInfo(string message)
    {
        logger.Info(message);
    }

    public void SetTraderUpdateTime(TraderConfig traderConfig, TraderBase baseJson, int refreshTimeSecondsMin, int refreshTimeSecondsMax)
    {
        // Remove any existing entry for this trader to avoid duplicates
        traderConfig.UpdateTime.RemoveAll(x => x.TraderId == baseJson.Id);
        
        var traderRefreshRecord = new UpdateTime
        {
            TraderId = baseJson.Id,
            Seconds = new MinMax<int>(refreshTimeSecondsMin, refreshTimeSecondsMax)
        };

        traderConfig.UpdateTime.Add(traderRefreshRecord);
    }

    public void AddTraderToDb(TraderBase traderDetailsToAdd, TraderAssort assort)
    {
        var traderDataToAdd = new Trader
        {
            Assort = assort,
            Base = traderDetailsToAdd,
            QuestAssort = new()
            {
                { "Started", new() },
                { "Success", new() },
                { "Fail", new() }
            },
            Dialogue = []
        };

        if (!databaseService.GetTables().Traders.TryAdd(traderDetailsToAdd.Id, traderDataToAdd))
        {
            logger.Warning($"Trader already exists in DB: {traderDetailsToAdd.Id}");
        }
    }

    public void AddTraderToLocales(TraderBase baseJson, string firstName, string description)
    {
        var locales = databaseService.GetTables().Locales.Global;
        var newTraderId = baseJson.Id;
        var fullName = baseJson.Name;
        var nickName = baseJson.Nickname;
        var location = baseJson.Location;

        foreach (var (_, localeKvP) in locales)
        {
            localeKvP.AddTransformer(lazyloadedLocaleData =>
            {
                lazyloadedLocaleData.TryAdd($"{newTraderId} FullName", fullName);
                lazyloadedLocaleData.TryAdd($"{newTraderId} FirstName", firstName);
                lazyloadedLocaleData.TryAdd($"{newTraderId} Nickname", nickName);
                lazyloadedLocaleData.TryAdd($"{newTraderId} Location", location);
                lazyloadedLocaleData.TryAdd($"{newTraderId} Description", description);
                return lazyloadedLocaleData;
            });
        }
    }

    public void OverwriteTraderAssort(string traderId, TraderAssort newAssorts)
    {
        if (!databaseService.GetTables().Traders.TryGetValue(traderId, out var traderToEdit))
        {
            logger.Warning($"Unable to update assorts for trader: {traderId}");
            return;
        }

        traderToEdit.Assort = newAssorts;
    }
}
