using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src;

/// <summary>
/// Registers a tiny Tony trader shell early so WTT quest/assort import code can find the trader
/// before YATMTraderRuntimeService builds the final real assortment.
///
/// The real runtime still loads db/CustomTrader/Tony/assort.json, rolls barter/out-of-stock,
/// and then replaces this shell while preserving any QuestAssort data already imported.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public sealed class YATMTraderDbBootstrap(
    ModHelper modHelper,
    AddCustomTraderHelper addCustomTraderHelper) : IOnLoad
{
    private const string DefaultTonyTraderId = "66a0f6b2c4d8e90123456789";

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        YATMLogger.Init(pathToMod);
        YATMLogger.LogDebug("[TraderBootstrap] Registering early Tony DB placeholder.");

        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(
            pathToMod,
            Path.Combine("db", "CustomTrader", "Tony", "base.json"));

        if (string.IsNullOrWhiteSpace(traderBase.Id))
        {
            traderBase.Id = DefaultTonyTraderId;
        }

        var emptyAssort = new TraderAssort
        {
            Items = [],
            BarterScheme = [],
            LoyalLevelItems = []
        };

        addCustomTraderHelper.AddTraderToDb(traderBase, emptyAssort);

        YATMLogger.LogDebug($"[TraderBootstrap] Early Tony DB placeholder ready: {traderBase.Id}");
        return Task.CompletedTask;
    }
}
